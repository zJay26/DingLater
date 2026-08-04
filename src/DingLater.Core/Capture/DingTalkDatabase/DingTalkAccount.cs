using System.Security.Cryptography;

namespace DingLater.Core.Capture.DingTalkDatabase;

internal sealed class DingTalkAccount : IDisposable
{
    internal DingTalkAccount(
        string dataDirectory,
        string databasePath,
        string walPath,
        string accountFingerprint,
        long selfUserId,
        byte[] databaseKey,
        DateTime lastActivityUtc,
        int matchedAccountCount)
    {
        DataDirectory = dataDirectory;
        DatabasePath = databasePath;
        WalPath = walPath;
        AccountFingerprint = accountFingerprint;
        SelfUserId = selfUserId;
        DatabaseKey = databaseKey;
        LastActivityUtc = lastActivityUtc;
        MatchedAccountCount = matchedAccountCount;
    }

    internal string DataDirectory { get; }
    internal string DatabasePath { get; }
    internal string WalPath { get; }
    internal string AccountFingerprint { get; }
    internal long SelfUserId { get; }
    internal byte[] DatabaseKey { get; }
    internal DateTime LastActivityUtc { get; }
    internal int MatchedAccountCount { get; }

    public void Dispose() => CryptographicOperations.ZeroMemory(DatabaseKey);
}
