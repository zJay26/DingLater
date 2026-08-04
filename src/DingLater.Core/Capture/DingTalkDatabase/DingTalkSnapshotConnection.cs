using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace DingLater.Core.Capture.DingTalkDatabase;

internal sealed class DingTalkSnapshotConnection : IAsyncDisposable
{
    private readonly int _bufferLength;
    private IntPtr _buffer;
    private bool _disposed;

    private DingTalkSnapshotConnection(SqliteConnection connection, IntPtr buffer, int bufferLength)
    {
        Connection = connection;
        _buffer = buffer;
        _bufferLength = bufferLength;
    }

    internal SqliteConnection Connection { get; }

    internal static async Task<DingTalkSnapshotConnection> CreateAsync(
        byte[] database,
        CancellationToken cancellationToken)
    {
        Batteries_V2.Init();
        var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory;Cache=Private");
        IntPtr buffer = IntPtr.Zero;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            buffer = raw.sqlite3_malloc64(database.LongLength);
            if (buffer == IntPtr.Zero)
            {
                throw new OutOfMemoryException("SQLite could not allocate the in-memory DingTalk snapshot.");
            }

            Marshal.Copy(database, 0, buffer, database.Length);
            var result = raw.sqlite3_deserialize(
                connection.Handle,
                "main",
                buffer,
                database.LongLength,
                database.LongLength,
                raw.SQLITE_DESERIALIZE_READONLY);
            if (result != raw.SQLITE_OK)
            {
                throw new DingTalkCaptureException("snapshot_deserialize_failed", "无法打开钉钉内存快照。");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA quick_check;";
            var check = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new DingTalkCaptureException("snapshot_integrity_failed", "钉钉内存快照一致性检查失败。");
            }

            return new DingTalkSnapshotConnection(connection, buffer, database.Length);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (buffer != IntPtr.Zero)
            {
                ZeroAndFree(buffer, database.Length);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(database);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Connection.DisposeAsync().ConfigureAwait(false);
        if (_buffer != IntPtr.Zero)
        {
            ZeroAndFree(_buffer, _bufferLength);
            _buffer = IntPtr.Zero;
        }
    }

    private static unsafe void ZeroAndFree(IntPtr buffer, int length)
    {
        CryptographicOperations.ZeroMemory(new Span<byte>(buffer.ToPointer(), length));
        raw.sqlite3_free(buffer);
    }
}
