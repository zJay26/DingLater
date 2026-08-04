using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DingLater.Core.Capture.DingTalkDatabase;

internal sealed partial class DingTalkAccountLocator
{
    private const int MaximumConfigBytes = 1024 * 1024;
    private const int MaximumLogFiles = 64;

    internal async Task<DingTalkAccount> LocateAsync(CancellationToken cancellationToken)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = Path.GetFullPath(Path.Combine(roaming, "DingTalk"));
        if (!Directory.Exists(root))
        {
            throw new DingTalkCaptureException("dingtalk_data_not_found", "没有找到钉钉本地数据，请先登录并启动钉钉。");
        }

        var accountDirectories = Directory.EnumerateDirectories(root, "*_v3", SearchOption.TopDirectoryOnly)
            .Where(path => IsSafeChild(root, path))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => new DirectoryInfo(path))
            .Where(directory => File.Exists(Path.Combine(directory.FullName, "DBFiles", "dingtalk.db")))
            .Where(directory => File.Exists(Path.Combine(directory.FullName, "user_config")))
            .ToList();
        if (accountDirectories.Count == 0)
        {
            throw new DingTalkCaptureException("v3_database_not_found", "没有找到受支持的钉钉 V3 数据库。");
        }

        var userIds = await ReadUserIdCandidatesAsync(root, cancellationToken).ConfigureAwait(false);
        if (userIds.Count == 0)
        {
            throw new DingTalkCaptureException("real_uid_not_found", "暂时无法取得本机账号标识，请重新启动钉钉后再试。");
        }

        var matches = new List<DingTalkAccount>();
        foreach (var directory in accountDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string salt;
            try
            {
                salt = await ReadSaltAsync(Path.Combine(directory.FullName, "user_config"), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or JsonException)
            {
                continue;
            }

            var databasePath = Path.Combine(directory.FullName, "DBFiles", "dingtalk.db");
            var walPath = databasePath + "-wal";
            foreach (var userId in userIds)
            {
                var key = DingTalkV3KeyDeriver.Derive(userId, salt);
                if (!await HasSqliteHeaderAsync(databasePath, key, cancellationToken).ConfigureAwait(false))
                {
                    CryptographicOperations.ZeroMemory(key);
                    continue;
                }

                var fingerprintMaterial = Encoding.UTF8.GetBytes(directory.Name + "|" + salt);
                var fingerprintBytes = SHA256.HashData(fingerprintMaterial);
                CryptographicOperations.ZeroMemory(fingerprintMaterial);
                var fingerprint = Convert.ToHexStringLower(fingerprintBytes);
                CryptographicOperations.ZeroMemory(fingerprintBytes);
                var lastActivity = MaxWriteTime(databasePath, walPath);
                matches.Add(new DingTalkAccount(
                    directory.FullName,
                    databasePath,
                    walPath,
                    fingerprint,
                    long.Parse(userId, System.Globalization.CultureInfo.InvariantCulture),
                    key,
                    lastActivity,
                    matchedAccountCount: 0));
            }
        }

        if (matches.Count == 0)
        {
            throw new DingTalkCaptureException("v3_key_mismatch", "本地账号信息与 V3 数据库不匹配，请重新启动钉钉后再试。");
        }

        var selected = matches.OrderByDescending(match => match.LastActivityUtc).First();
        foreach (var other in matches.Where(match => !ReferenceEquals(match, selected)))
        {
            other.Dispose();
        }

        return new DingTalkAccount(
            selected.DataDirectory,
            selected.DatabasePath,
            selected.WalPath,
            selected.AccountFingerprint,
            selected.SelfUserId,
            selected.DatabaseKey,
            selected.LastActivityUtc,
            matches.Count);
    }

    private static async Task<HashSet<string>> ReadUserIdCandidatesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var logDirectory = Path.Combine(root, "log");
        if (!Directory.Exists(logDirectory) || !IsSafeChild(root, logDirectory))
        {
            return result;
        }

        var files = Directory.EnumerateFiles(logDirectory, "gaea.log*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaximumLogFiles);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = OpenReadShared(file.FullName, FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    foreach (Match match in RealUserIdRegex().Matches(line))
                    {
                        result.Add(match.Groups["uid"].Value);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static async Task<string> ReadSaltAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumConfigBytes)
        {
            throw new FormatException("Unsupported user_config size.");
        }

        await using var stream = OpenReadShared(path, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var encoded = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
        var jsonBytes = Convert.FromBase64String(encoded);
        try
        {
            using var document = JsonDocument.Parse(jsonBytes);
            if (!document.RootElement.TryGetProperty("salt", out var value)
                && !document.RootElement.TryGetProperty("slt", out value))
            {
                throw new FormatException("V3 salt is missing.");
            }

            return value.GetString() ?? throw new FormatException("V3 salt is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    private static async Task<bool> HasSqliteHeaderAsync(
        string databasePath,
        byte[] key,
        CancellationToken cancellationToken)
    {
        var encrypted = new byte[16];
        var plaintext = new byte[16];
        try
        {
            await using var stream = OpenReadShared(databasePath, FileOptions.RandomAccess);
            if (await stream.ReadAsync(encrypted, cancellationToken).ConfigureAwait(false) != encrypted.Length)
            {
                return false;
            }

            using var aes = Aes.Create();
            aes.Key = key;
            aes.DecryptEcb(encrypted, plaintext, PaddingMode.None);
            return plaintext.AsSpan().SequenceEqual("SQLite format 3\0"u8);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static FileStream OpenReadShared(string path, FileOptions options) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, options | FileOptions.Asynchronous);

    private static bool IsSafeChild(string root, string candidate)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime MaxWriteTime(string first, string second)
    {
        var firstTime = File.GetLastWriteTimeUtc(first);
        return File.Exists(second) && File.GetLastWriteTimeUtc(second) > firstTime
            ? File.GetLastWriteTimeUtc(second)
            : firstTime;
    }

    [GeneratedRegex(@"real_uid[\s\""':=]+(?<uid>\d{6,15})@dingding\b", RegexOptions.CultureInvariant)]
    private static partial Regex RealUserIdRegex();
}
