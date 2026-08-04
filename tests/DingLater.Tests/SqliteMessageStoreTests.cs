using System.Security.Cryptography;
using System.Text;
using DingLater.Core.Models;
using DingLater.Core.Security;
using DingLater.Core.Storage;
using Microsoft.Data.Sqlite;

namespace DingLater.Tests;

[TestClass]
public sealed class SqliteMessageStoreTests
{
    private string _directory = null!;
    private string _database = null!;

    [TestInitialize]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "DingLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
    }

    [TestCleanup]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Add_PersistsEncryptedFields_AndDeduplicates()
    {
        const string marker = "DINGLATER_PRIVACY_MARKER_39e4d2";
        await using var store = CreateStore();
        await store.InitializeAsync();
        var captured = Message(marker, DateTimeOffset.Parse("2026-08-03T12:00:00+08:00"));

        var first = await store.AddAsync(captured, 7);
        var second = await store.AddAsync(captured, 7);
        var messages = await store.ListAsync();

        Assert.IsTrue(first.Inserted);
        Assert.IsFalse(second.Inserted);
        Assert.AreEqual(first.Message.Id, second.Message.Id);
        Assert.HasCount(1, messages);
        Assert.AreEqual(marker, messages[0].Captured.VisibleBody);

        SqliteConnection.ClearAllPools();
        var markerBytes = Encoding.UTF8.GetBytes(marker);
        foreach (var path in Directory.GetFiles(_directory))
        {
            Assert.IsFalse(ContainsSequence(await File.ReadAllBytesAsync(path), markerBytes), $"Plaintext marker leaked into {Path.GetFileName(path)}");
        }
    }

    [TestMethod]
    public async Task Retention_IsHardDeadlineAcrossStates()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00+08:00");
        await using var store = CreateStore();
        await store.InitializeAsync();
        var inserted = await store.AddAsync(Message("old", now.AddDays(-8)), 7);
        await store.UpdateStateAsync(inserted.Message.Id, InboxState.Snoozed, now.AddHours(1), now);

        var removed = await store.DeleteExpiredAsync(now);

        CollectionAssert.Contains(removed.ToList(), inserted.Message.Id);
        Assert.HasCount(0, await store.ListAsync());
    }

    [TestMethod]
    public async Task CorruptDatabase_IsQuarantinedAndRecreated()
    {
        await File.WriteAllTextAsync(_database, "not-a-sqlite-database");
        await using var store = CreateStore();

        await store.InitializeAsync();

        Assert.IsTrue(File.Exists(_database));
        Assert.IsTrue(Directory.Exists(Path.Combine(_directory, "quarantine")));
        Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(_directory, "quarantine")));
    }

    [TestMethod]
    public async Task Schema2Database_IsUpgradedInPlace_AndPreservesMessages()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var crypto = new MessageCrypto(key);
        CryptographicOperations.ZeroMemory(key);
        var id = Guid.NewGuid();
        var capturedAt = DateTimeOffset.Parse("2026-08-03T12:00:00+08:00");
        await using (var connection = new SqliteConnection($"Data Source={_database}"))
        {
            await connection.OpenAsync();
            await using var schema = connection.CreateCommand();
            schema.CommandText = """
                PRAGMA user_version=2;
                CREATE TABLE messages (
                    id TEXT PRIMARY KEY,
                    captured_at_utc INTEGER NOT NULL,
                    message_at_utc INTEGER NULL,
                    expires_at_utc INTEGER NOT NULL,
                    updated_at_utc INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    snoozed_until_utc INTEGER NULL,
                    source INTEGER NOT NULL,
                    kind INTEGER NOT NULL,
                    confidence REAL NOT NULL,
                    dingtalk_version TEXT NOT NULL,
                    source_identity BLOB NOT NULL,
                    conversation BLOB NOT NULL,
                    sender BLOB NOT NULL,
                    body BLOB NOT NULL,
                    fingerprint BLOB NOT NULL UNIQUE
                );
                """;
            await schema.ExecuteNonQueryAsync();

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO messages (
                    id, captured_at_utc, message_at_utc, expires_at_utc, updated_at_utc,
                    state, snoozed_until_utc, source, kind, confidence, dingtalk_version,
                    source_identity, conversation, sender, body, fingerprint)
                VALUES (
                    $id, $captured, NULL, $expires, $updated,
                    0, NULL, $source, $kind, 1.0, 'legacy',
                    $identity, $conversation, $sender, $body, $fingerprint);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$captured", capturedAt.ToUniversalTime().ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$expires", capturedAt.AddDays(7).ToUniversalTime().ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$updated", capturedAt.ToUniversalTime().ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$source", (int)CaptureSourceKind.DingTalkDatabase);
            insert.Parameters.AddWithValue("$kind", (int)MessageKind.Normal);
            insert.Parameters.AddWithValue("$identity", crypto.Encrypt("legacy:1"));
            insert.Parameters.AddWithValue("$conversation", crypto.Encrypt("460140151"));
            insert.Parameters.AddWithValue("$sender", crypto.Encrypt("毛俊翔"));
            insert.Parameters.AddWithValue("$body", crypto.Encrypt("1"));
            insert.Parameters.AddWithValue("$fingerprint", RandomNumberGenerator.GetBytes(32));
            await insert.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteMessageStore(_database, crypto);
        await store.InitializeAsync();
        var messages = await store.ListAsync();

        Assert.HasCount(1, messages);
        Assert.AreEqual(id, messages[0].Id);
        Assert.AreEqual("460140151", messages[0].Captured.Conversation);
        Assert.AreEqual("毛俊翔", messages[0].Captured.Sender);
        Assert.AreEqual(ConversationScope.Unknown, messages[0].Captured.ConversationScope);

        await using var verify = new SqliteConnection($"Data Source={_database}");
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(3L, (long)(await version.ExecuteScalarAsync())!);
        await using var columns = verify.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name='conversation_scope';";
        Assert.AreEqual(1L, (long)(await columns.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task CrossSourceDuplicates_AreSuppressedAcrossMinuteBoundary()
    {
        await using var store = CreateStore();
        await store.InitializeAsync();
        var first = Message("同一条可见正文", DateTimeOffset.Parse("2026-08-03T12:00:59+08:00"));
        var second = first with
        {
            Source = CaptureSourceKind.PopupAutomation,
            CapturedAt = DateTimeOffset.Parse("2026-08-03T12:01:02+08:00")
        };

        var firstResult = await store.AddAsync(first, 7);
        var secondResult = await store.AddAsync(second, 7);

        Assert.IsTrue(firstResult.Inserted);
        Assert.IsFalse(secondResult.Inserted);
        Assert.AreEqual(firstResult.Message.Id, secondResult.Message.Id);
        Assert.HasCount(1, await store.ListAsync());
    }

    [TestMethod]
    public async Task StableSourceIdentity_KeepsRepeatedTextAndCommitsCheckpointAtomically()
    {
        await using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-08-03T12:00:00+08:00");
        var first = Message("1", now) with
        {
            Source = CaptureSourceKind.DingTalkDatabase,
            SourceIdentity = "account:7:100:cid:mid1"
        };
        var second = first with { SourceIdentity = "account:7:101:cid:mid2" };
        var checkpoint = new CaptureCheckpoint(CaptureSourceKind.DingTalkDatabase, "account", 7, 101);

        var result = await store.AppendCaptureBatchAsync(new CaptureBatch([first, second], [checkpoint]), 7);
        var positions = await store.GetCaptureCheckpointsAsync(CaptureSourceKind.DingTalkDatabase, "account");

        Assert.HasCount(2, result.InsertedMessages);
        Assert.AreEqual(0, result.DuplicateCount);
        Assert.HasCount(2, await store.ListAsync());
        Assert.AreEqual(101, positions[7]);

        var replay = await store.AppendCaptureBatchAsync(new CaptureBatch([first, second], [checkpoint]), 7);
        Assert.HasCount(0, replay.InsertedMessages);
        Assert.AreEqual(2, replay.DuplicateCount);
        Assert.HasCount(2, await store.ListAsync());
    }

    [TestMethod]
    public async Task Settings_MigrateLegacyValues_AndRecoverFromInvalidJson()
    {
        await using var store = CreateStore();
        await store.InitializeAsync();
        await WriteSettingsJsonAsync("{\"RetentionDays\":999,\"OnboardingCompleted\":true}");

        var migrated = await store.GetSettingsAsync();

        Assert.AreEqual(3, migrated.SchemaVersion);
        Assert.AreEqual(365, migrated.RetentionDays);
        Assert.IsTrue(migrated.OnboardingCompleted);
        Assert.IsFalse(migrated.ShowReminderPreview);
        Assert.AreEqual(UiFontScale.Standard, migrated.UiFontScale);
        Assert.AreEqual(30, migrated.QuickSnoozeMinutes);

        await WriteSettingsJsonAsync("{not valid json");
        var recovered = await store.GetSettingsAsync();
        Assert.AreEqual(new AppSettings(), recovered);
    }

    [TestMethod]
    public async Task Settings_FontScaleAndQuickMinutes_RoundTripAndNormalize()
    {
        await using var store = CreateStore();
        await store.InitializeAsync();

        foreach (var scale in Enum.GetValues<UiFontScale>())
        {
            await store.SaveSettingsAsync(new AppSettings(
                UiFontScale: scale,
                QuickSnoozeMinutes: 74));
            var loaded = await store.GetSettingsAsync();
            Assert.AreEqual(scale, loaded.UiFontScale);
            Assert.AreEqual(74, loaded.QuickSnoozeMinutes);
        }

        await WriteSettingsJsonAsync("{\"UiFontScale\":999,\"QuickSnoozeMinutes\":0}");
        var normalized = await store.GetSettingsAsync();
        Assert.AreEqual(UiFontScale.Standard, normalized.UiFontScale);
        Assert.AreEqual(1, normalized.QuickSnoozeMinutes);

        await WriteSettingsJsonAsync("{\"QuickSnoozeMinutes\":99999}");
        normalized = await store.GetSettingsAsync();
        Assert.AreEqual(1440, normalized.QuickSnoozeMinutes);
    }

    private SqliteMessageStore CreateStore() =>
        new(_database, new MessageCrypto(RandomNumberGenerator.GetBytes(32)));

    private async Task WriteSettingsJsonAsync(string value)
    {
        await using var connection = new SqliteConnection($"Data Source={_database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key, value) VALUES('app', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static CapturedMessage Message(string body, DateTimeOffset capturedAt) =>
        new(CaptureSourceKind.Synthetic, capturedAt, "测试会话", "测试发送者", body, MessageKind.Normal, 1, "test");

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
