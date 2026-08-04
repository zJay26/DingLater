using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DingLater.Core.Capture.DingTalkDatabase;

public static class DingTalkWalMerger
{
    private const int DatabasePageSize = 4096;
    private const int WalHeaderSize = 32;
    private const int FrameHeaderSize = 24;
    private const uint WalMagicOne = 0x377f0682;
    private const uint WalMagicTwo = 0x377f0683;

    public static byte[] BuildSnapshot(
        ReadOnlySpan<byte> encryptedDatabase,
        ReadOnlySpan<byte> wal,
        ReadOnlySpan<byte> key,
        out bool walValidated)
    {
        if (encryptedDatabase.Length < DatabasePageSize
            || encryptedDatabase.Length % DatabasePageSize != 0)
        {
            throw new DingTalkCaptureException("database_size_invalid", "钉钉数据库页大小不受支持。");
        }

        if (key.Length != 16)
        {
            throw new DingTalkCaptureException("database_key_invalid", "钉钉数据库密钥长度不受支持。");
        }

        var snapshot = new byte[encryptedDatabase.Length];
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        try
        {
            aes.DecryptEcb(encryptedDatabase, snapshot, PaddingMode.None);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(snapshot);
            throw;
        }

        if (!snapshot.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        {
            CryptographicOperations.ZeroMemory(snapshot);
            throw new DingTalkCaptureException("database_key_mismatch", "钉钉数据库解密校验失败。");
        }

        var declaredPageSize = BinaryPrimitives.ReadUInt16BigEndian(snapshot.AsSpan(16, 2));
        if (declaredPageSize != DatabasePageSize)
        {
            CryptographicOperations.ZeroMemory(snapshot);
            throw new DingTalkCaptureException("database_page_size_unsupported", "钉钉数据库不是受支持的 4096 字节分页格式。");
        }

        walValidated = true;
        if (wal.IsEmpty)
        {
            SetRollbackJournalHeader(snapshot);
            return snapshot;
        }

        try
        {
            ApplyWal(snapshot: ref snapshot, wal, aes);
            SetRollbackJournalHeader(snapshot);
            return snapshot;
        }
        catch
        {
            walValidated = false;
            CryptographicOperations.ZeroMemory(snapshot);
            throw;
        }
        finally
        {
            var aesKey = aes.Key;
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    private static void ApplyWal(ref byte[] snapshot, ReadOnlySpan<byte> wal, Aes aes)
    {
        if (wal.Length < WalHeaderSize)
        {
            throw new DingTalkCaptureException("wal_header_incomplete", "钉钉 WAL 头尚未写完整。");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(wal[..4]);
        if (magic is not (WalMagicOne or WalMagicTwo))
        {
            throw new DingTalkCaptureException("wal_magic_invalid", "钉钉 WAL 格式不受支持。");
        }

        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(8, 4));
        if (pageSize != DatabasePageSize)
        {
            throw new DingTalkCaptureException("wal_page_size_unsupported", "钉钉 WAL 页大小不受支持。");
        }

        var storedHeaderChecksum = new WalChecksum(
            BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(24, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(28, 4)));
        var checksumLittleEndian = MatchChecksumByteOrder(wal[..24], storedHeaderChecksum);
        var rollingChecksum = storedHeaderChecksum;
        var saltOne = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(16, 4));
        var saltTwo = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(20, 4));
        var completeFrameCount = (wal.Length - WalHeaderSize) / (FrameHeaderSize + DatabasePageSize);
        var frames = new List<WalFrame>(completeFrameCount);
        var lastCommitIndex = -1;
        uint finalDatabasePages = 0;

        for (var index = 0; index < completeFrameCount; index++)
        {
            var frameOffset = WalHeaderSize + index * (FrameHeaderSize + DatabasePageSize);
            var frame = wal.Slice(frameOffset, FrameHeaderSize + DatabasePageSize);
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frame[..4]);
            var databasePages = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(4, 4));
            var frameSaltOne = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(8, 4));
            var frameSaltTwo = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(12, 4));
            if (pageNumber == 0 || frameSaltOne != saltOne || frameSaltTwo != saltTwo)
            {
                // SQLite may reset a WAL without truncating its preallocated file. Frames after
                // the new logical end then retain the previous cycle's salts. They are not part
                // of the current log; the first identity mismatch marks the end of its valid
                // prefix rather than a corrupt snapshot.
                break;
            }

            var checksumInput = new byte[8 + DatabasePageSize];
            try
            {
                frame[..8].CopyTo(checksumInput);
                frame.Slice(FrameHeaderSize, DatabasePageSize).CopyTo(checksumInput.AsSpan(8));
                rollingChecksum = ComputeChecksum(checksumInput, rollingChecksum, checksumLittleEndian);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(checksumInput);
            }

            var storedFrameChecksum = new WalChecksum(
                BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(20, 4)));
            if (rollingChecksum != storedFrameChecksum)
            {
                // A partially written frame (or stale preallocated tail) terminates the valid
                // WAL prefix. Only fully checksummed commits collected below are applied.
                break;
            }

            frames.Add(new WalFrame(pageNumber, databasePages, frameOffset + FrameHeaderSize));
            if (databasePages != 0)
            {
                lastCommitIndex = index;
                finalDatabasePages = databasePages;
            }
        }

        if (lastCommitIndex < 0)
        {
            return;
        }

        var finalLength = checked((long)finalDatabasePages * DatabasePageSize);
        if (finalLength is < DatabasePageSize or > int.MaxValue)
        {
            throw new DingTalkCaptureException("wal_database_size_invalid", "钉钉 WAL 提交后的数据库大小不受支持。");
        }

        Array.Resize(ref snapshot, (int)finalLength);
        var decryptedPage = new byte[DatabasePageSize];
        try
        {
            for (var index = 0; index <= lastCommitIndex; index++)
            {
                var frame = frames[index];
                var targetOffset = checked((long)(frame.PageNumber - 1) * DatabasePageSize);
                if (targetOffset + DatabasePageSize > snapshot.LongLength)
                {
                    // A later committed transaction can truncate pages written by an earlier
                    // transaction in the same WAL. SQLite discards those pages at checkpoint.
                    continue;
                }

                aes.DecryptEcb(wal.Slice(frame.PayloadOffset, DatabasePageSize), decryptedPage, PaddingMode.None);
                decryptedPage.CopyTo(snapshot.AsSpan((int)targetOffset, DatabasePageSize));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decryptedPage);
        }
    }

    private static bool MatchChecksumByteOrder(ReadOnlySpan<byte> header, WalChecksum expected)
    {
        var bigEndian = ComputeChecksum(header, default, littleEndian: false);
        if (bigEndian == expected)
        {
            return false;
        }

        var littleEndian = ComputeChecksum(header, default, littleEndian: true);
        if (littleEndian == expected)
        {
            return true;
        }

        throw new DingTalkCaptureException("wal_header_checksum_invalid", "钉钉 WAL 头校验失败。");
    }

    private static WalChecksum ComputeChecksum(
        ReadOnlySpan<byte> bytes,
        WalChecksum initial,
        bool littleEndian)
    {
        if (bytes.Length % 8 != 0)
        {
            throw new ArgumentException("WAL checksum input must contain pairs of 32-bit words.", nameof(bytes));
        }

        var first = initial.First;
        var second = initial.Second;
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var wordOne = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            var wordTwo = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4));
            unchecked
            {
                first += wordOne + second;
                second += wordTwo + first;
            }
        }

        return new WalChecksum(first, second);
    }

    private static void SetRollbackJournalHeader(byte[] database)
    {
        database[18] = 1;
        database[19] = 1;
    }

    private readonly record struct WalChecksum(uint First, uint Second);
    private readonly record struct WalFrame(uint PageNumber, uint DatabasePages, int PayloadOffset);
}
