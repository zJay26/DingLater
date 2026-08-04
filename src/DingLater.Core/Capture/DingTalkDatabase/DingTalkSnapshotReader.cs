using System.Security.Cryptography;

namespace DingLater.Core.Capture.DingTalkDatabase;

internal sealed class DingTalkSnapshotReader
{
    private const int MaximumAttempts = 4;

    internal async Task<DingTalkSnapshotConnection> OpenAsync(
        DingTalkAccount account,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? encryptedDatabase = null;
            byte[]? wal = null;
            try
            {
                var before = FileStamp.Read(account.DatabasePath, account.WalPath);
                encryptedDatabase = await ReadSharedAsync(account.DatabasePath, cancellationToken).ConfigureAwait(false);
                wal = File.Exists(account.WalPath)
                    ? await ReadSharedAsync(account.WalPath, cancellationToken).ConfigureAwait(false)
                    : [];
                var after = FileStamp.Read(account.DatabasePath, account.WalPath);
                if (before != after)
                {
                    throw new DingTalkCaptureException("snapshot_changed_during_read", "钉钉数据库正在提交，请稍后重试。");
                }

                var database = DingTalkWalMerger.BuildSnapshot(
                    encryptedDatabase,
                    wal,
                    account.DatabaseKey,
                    out _);
                return await DingTalkSnapshotConnection.CreateAsync(database, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or DingTalkCaptureException)
            {
                lastError = exception;
                if (attempt + 1 < MaximumAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (encryptedDatabase is not null)
                {
                    CryptographicOperations.ZeroMemory(encryptedDatabase);
                }

                if (wal is not null)
                {
                    CryptographicOperations.ZeroMemory(wal);
                }
            }
        }

        throw new DingTalkCaptureException(
            "snapshot_unstable",
            "多次读取后钉钉数据库仍在变化，将在下一轮自动重试。",
            lastError ?? new IOException("Snapshot did not stabilize."));
    }

    internal static FileStamp GetStamp(DingTalkAccount account) => FileStamp.Read(account.DatabasePath, account.WalPath);

    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
        {
            throw new DingTalkCaptureException("database_too_large", "钉钉数据库超过当前版本支持的大小。");
        }

        var result = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        var offset = 0;
        while (offset < result.Length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The DingTalk database changed while it was being read.");
            }

            offset += read;
        }

        return result;
    }

    internal readonly record struct FileStamp(
        long DatabaseLength,
        DateTime DatabaseWriteUtc,
        bool WalExists,
        long WalLength,
        DateTime WalWriteUtc)
    {
        internal static FileStamp Read(string databasePath, string walPath)
        {
            var database = new FileInfo(databasePath);
            database.Refresh();
            var wal = new FileInfo(walPath);
            wal.Refresh();
            return new FileStamp(
                database.Length,
                database.LastWriteTimeUtc,
                wal.Exists,
                wal.Exists ? wal.Length : 0,
                wal.Exists ? wal.LastWriteTimeUtc : DateTime.MinValue);
        }
    }
}
