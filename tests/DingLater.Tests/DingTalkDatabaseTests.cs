using System.Security.Cryptography;
using System.Text;
using DingLater.Core.Capture.DingTalkDatabase;
using DingLater.Core.Models;
using DingLater.Core.Security;
using DingLater.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DingLater.Tests;

[TestClass]
public sealed class DingTalkDatabaseTests
{
    [TestMethod]
    public void V3KeyDerivation_MatchesKnownVector()
    {
        var key = DingTalkV3KeyDeriver.Derive(
            "1234567890",
            "0123456789abcdef0123456789abcdef");

        Assert.AreEqual("da8827b14e140675", Encoding.ASCII.GetString(key));
        CryptographicOperations.ZeroMemory(key);
    }

    [TestMethod]
    public void DatabasePageDecryption_RemainsInMemoryAndPreservesHeader()
    {
        var key = Encoding.ASCII.GetBytes("da8827b14e140675");
        var plaintext = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(plaintext);
        plaintext[16] = 0x10;
        plaintext[17] = 0x00;
        plaintext[18] = 2;
        plaintext[19] = 2;
        var encrypted = new byte[plaintext.Length];
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.EncryptEcb(plaintext, encrypted, PaddingMode.None);
        }

        var snapshot = DingTalkWalMerger.BuildSnapshot(encrypted, [], key, out var walValid);

        Assert.IsTrue(walValid);
        CollectionAssert.AreEqual("SQLite format 3\0"u8.ToArray(), snapshot[..16]);
        Assert.AreEqual(1, snapshot[18]);
        Assert.AreEqual(1, snapshot[19]);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(encrypted);
        CryptographicOperations.ZeroMemory(snapshot);
    }

    [TestMethod]
    public void ContentParser_PreservesFullTextAndUnderstandsCoreTypes()
    {
        var longText = string.Concat(Enumerable.Repeat("完整文字🙂\n", 700));
        var text = DingTalkMessageContentParser.Parse(1, System.Text.Json.JsonSerializer.Serialize(new { text = longText }), "");
        var rich = DingTalkMessageContentParser.Parse(
            1200,
            "{\"attachments\":[{\"extension\":{\"markdown\":\"第一行\\n第二行\"}}]}",
            "");
        var file = DingTalkMessageContentParser.Parse(
            501,
            "",
            "[{\"extension\":{\"f_name\":\"方案文档.docx\"}}]");

        Assert.AreEqual(longText.Trim(), text.Body);
        StringAssert.Contains(rich.Body, "第一行");
        StringAssert.Contains(rich.Body, "第二行");
        StringAssert.Contains(file.Body, "方案文档.docx");
        Assert.AreEqual(MessageKind.Attachment, file.Kind);
    }

    [TestMethod]
    public async Task MessageReader_DrainsLargeBacklogAndRecognizesDirectConversationId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            var messageTables = string.Join(Environment.NewLine, Enumerable.Range(0, DingTalkMessageReader.PartitionCount)
                .Select(partition => $"""
                    CREATE TABLE tbmsg_{partition:000} (
                        primaryKey INTEGER PRIMARY KEY, cid TEXT, mid INTEGER, senderId INTEGER,
                        createdAt INTEGER, contentType INTEGER, content TEXT, recallStatus INTEGER,
                        atIds TEXT, attachments TEXT
                    );
                    """));
            schema.CommandText = $"""
                CREATE TABLE tbconversation (cid TEXT PRIMARY KEY, type INTEGER, title TEXT, status INTEGER);
                CREATE TABLE tbuser_profile_v2 (uid INTEGER PRIMARY KEY, nick TEXT, realName TEXT);
                INSERT INTO tbconversation VALUES ('100:200', 0, '私聊测试', 1);
                INSERT INTO tbuser_profile_v2 VALUES (200, '发送者', '');
                {messageTables}
                """;
            await schema.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                WITH digits(n) AS (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)),
                sequence(n) AS (
                    SELECT ones.n + tens.n * 10 + hundreds.n * 100 + thousands.n * 1000 + 1
                    FROM digits ones CROSS JOIN digits tens CROSS JOIN digits hundreds CROSS JOIN digits thousands
                )
                INSERT INTO tbmsg_000 (
                    primaryKey, cid, mid, senderId, createdAt, contentType,
                    content, recallStatus, atIds, attachments)
                SELECT n, '100:200', n, 200, 1700000000000 + n, 1,
                       '{"text":"连续消息"}', 0, '[]', ''
                FROM sequence WHERE n <= 4097;
                """;
            await insert.ExecuteNonQueryAsync();
        }

        var positions = Enumerable.Range(0, DingTalkMessageReader.PartitionCount)
            .ToDictionary(partition => partition, _ => 0L);
        using var account = new DingTalkAccount(
            "test", "test", "test-wal", "account", 100,
            RandomNumberGenerator.GetBytes(16), DateTime.UtcNow, 1);
        var reader = new DingTalkMessageReader();
        var first = await reader.ReadNewAsync(
            connection, account, positions, GroupCaptureMode.MentionsOnly,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.HasCount(4096, first.Messages);
        Assert.IsTrue(first.HasMore);
        Assert.IsTrue(first.Messages.All(message => message.Conversation == "私聊测试"));

        foreach (var checkpoint in first.Checkpoints)
        {
            positions[checkpoint.Partition] = checkpoint.Position;
        }

        var second = await reader.ReadNewAsync(
            connection, account, positions, GroupCaptureMode.MentionsOnly,
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.HasCount(1, second.Messages);
        Assert.IsFalse(second.HasMore);
    }

    [TestMethod]
    public async Task MessageReader_MentionsOnly_CapturesUserAndEveryoneMentions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            var messageTables = string.Join(Environment.NewLine, Enumerable.Range(0, DingTalkMessageReader.PartitionCount)
                .Select(partition => $"""
                    CREATE TABLE tbmsg_{partition:000} (
                        primaryKey INTEGER PRIMARY KEY, cid TEXT, mid INTEGER, senderId INTEGER,
                        createdAt INTEGER, contentType INTEGER, content TEXT, recallStatus INTEGER,
                        atIds TEXT, attachments TEXT
                    );
                    """));
            schema.CommandText = $"""
                CREATE TABLE tbconversation (cid TEXT PRIMARY KEY, type INTEGER, title TEXT, status INTEGER);
                CREATE TABLE tbuser_profile_v2 (uid INTEGER PRIMARY KEY, nick TEXT, realName TEXT);
                INSERT INTO tbconversation VALUES ('group-1', 2, '群聊测试', 1);
                INSERT INTO tbuser_profile_v2 VALUES (200, '发送者', '');
                {messageTables}
                """;
            await schema.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO tbmsg_000 (
                    primaryKey, cid, mid, senderId, createdAt, contentType,
                    content, recallStatus, atIds, attachments)
                VALUES
                    (1, 'group-1', 1, 200, 1700000000001, 1, '{"text":"普通群消息"}', 0, '[]', ''),
                    (2, 'group-1', 2, 200, 1700000000002, 1, '{"text":"@我 的消息"}', 0, '[100]', ''),
                    (3, 'group-1', 3, 200, 1700000000003, 1, '{"text":"全员标记"}', 0, '["all"]', ''),
                    (4, 'group-1', 4, 200, 1700000000004, 1, '{"text":"全员布尔标记"}', 0, '{"isAtAll":true}', ''),
                    (5, 'group-1', 5, 200, 1700000000005, 1, '{"text":"全员数值标记"}', 0, '[-1]', ''),
                    (6, 'group-1', 6, 200, 1700000000006, 1, '{"text":"@所有人 正文回退"}', 0, '[]', ''),
                    (7, 'group-1', 7, 200, 1700000000007, 1, '{"text":"相似但不是全员"}', 0, '["small"]', ''),
                    (8, 'group-1', 8, 200, 1700000000008, 1, '{"text":"显式关闭全员"}', 0, '{"atAll":false}', ''),
                    (9, 'group-1', 9, 200, 1700000000009, 1, '{"text":"零值全员标记"}', 0, '[0]', ''),
                    (10, 'group-1', 10, 200, 1700000000010, 1, '{"text":"内容中的全员标记","at":{"isAtAll":true}}', 0, '[]', '');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        var positions = Enumerable.Range(0, DingTalkMessageReader.PartitionCount)
            .ToDictionary(partition => partition, _ => 0L);
        using var account = new DingTalkAccount(
            "test", "test", "test-wal", "account", 100,
            RandomNumberGenerator.GetBytes(16), DateTime.UtcNow, 1);

        var result = await new DingTalkMessageReader().ReadNewAsync(
            connection, account, positions, GroupCaptureMode.MentionsOnly,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.HasCount(7, result.Messages);
        Assert.IsTrue(result.Messages.All(message => message.Kind == MessageKind.Mention));
        Assert.IsTrue(result.Messages.All(message => message.ConversationScope == ConversationScope.Group));
        Assert.IsFalse(result.Messages.Any(message => message.VisibleBody == "普通群消息"));
        Assert.IsFalse(result.Messages.Any(message => message.VisibleBody == "相似但不是全员"));
        Assert.IsFalse(result.Messages.Any(message => message.VisibleBody == "显式关闭全员"));
    }

    [TestMethod]
    public async Task LiveV3Snapshot_PassesIntegrityAndSchemaProbe_WhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DINGLATER_REAL_DB_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set DINGLATER_REAL_DB_TEST=1 to run the privacy-preserving local integration probe.");
        }

        var locator = new DingTalkAccountLocator();
        using var account = await locator.LocateAsync(CancellationToken.None);
        var snapshotReader = new DingTalkSnapshotReader();
        await using var snapshot = await snapshotReader.OpenAsync(account, CancellationToken.None);
        var messageReader = new DingTalkMessageReader();

        var positions = await messageReader.ValidateAndReadPositionsAsync(snapshot.Connection, CancellationToken.None);
        var unreadBefore = await ReadUnreadCountAsync(snapshot.Connection);

        Assert.HasCount(DingTalkMessageReader.PartitionCount, positions);
        Assert.IsTrue(positions.Values.Any(position => position > 0));

        var directory = Path.Combine(Path.GetTempPath(), "DingLater.LiveProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new SqliteMessageStore(
                Path.Combine(directory, "probe.db"),
                new MessageCrypto(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync();
            await store.SaveSettingsAsync(new AppSettings(GroupCaptureMode: GroupCaptureMode.AllMessages));
            var startingCheckpoints = positions.Select(pair => new CaptureCheckpoint(
                CaptureSourceKind.DingTalkDatabase,
                account.AccountFingerprint,
                pair.Key,
                Math.Max(0, pair.Value - 1))).ToList();
            await store.AppendCaptureBatchAsync(new CaptureBatch([], startingCheckpoints), 7);

            var observedMessages = 0;
            await using var source = new DingTalkDatabaseSource(store);
            source.BatchCaptured += async (_, batch, cancellationToken) =>
            {
                observedMessages += batch.Messages.Count;
                await store.AppendCaptureBatchAsync(batch, 7, cancellationToken);
            };
            await source.StartAsync();
            await source.StopAsync();

            Assert.IsTrue(observedMessages > 0);
            Assert.IsTrue((await store.ListAsync()).All(message =>
                message.Captured.Source == CaptureSourceKind.DingTalkDatabase
                && !string.IsNullOrWhiteSpace(message.Captured.VisibleBody)));

            await using var afterSnapshot = await snapshotReader.OpenAsync(account, CancellationToken.None);
            var unreadAfter = await ReadUnreadCountAsync(afterSnapshot.Connection);
            Assert.AreEqual(unreadBefore, unreadAfter, "只读捕获不应改变本机钉钉未读总数。");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<long> ReadUnreadCountAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(unreadCount), 0) FROM tbconversation;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
