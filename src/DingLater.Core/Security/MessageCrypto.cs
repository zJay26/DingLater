using System.Security.Cryptography;
using System.Text;

namespace DingLater.Core.Security;

public sealed class MessageCrypto
{
    private const byte FormatVersion = 1;
    private readonly byte[] _masterKey;
    private readonly byte[] _fingerprintKey;

    public MessageCrypto(byte[] masterKey)
    {
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("The master key must contain 32 bytes.", nameof(masterKey));
        }

        _masterKey = masterKey.ToArray();
        using var derivation = new HMACSHA256(_masterKey);
        _fingerprintKey = derivation.ComputeHash("DingLater.Fingerprint.v1"u8.ToArray());
    }

    public byte[] Encrypt(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plainBytes.Length];
        using var aes = new AesGcm(_masterKey, tag.Length);
        aes.Encrypt(nonce.AsSpan(), plainBytes.AsSpan(), cipher.AsSpan(), tag.AsSpan(), new byte[] { FormatVersion });

        var result = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        result[0] = FormatVersion;
        nonce.CopyTo(result, 1);
        tag.CopyTo(result, 13);
        cipher.CopyTo(result, 29);
        CryptographicOperations.ZeroMemory(plainBytes);
        return result;
    }

    public string Decrypt(byte[] payload)
    {
        if (payload.Length < 29 || payload[0] != FormatVersion)
        {
            throw new CryptographicException("Unsupported encrypted message format.");
        }

        var nonce = payload.AsSpan(1, 12);
        var tag = payload.AsSpan(13, 16);
        var cipher = payload.AsSpan(29);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_masterKey, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain, new byte[] { FormatVersion });
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public byte[] Fingerprint(params string[] values)
    {
        var normalized = Encoding.UTF8.GetBytes(string.Join("\u001F", values.Select(Normalize)));
        try
        {
            using var hmac = new HMACSHA256(_fingerprintKey);
            return hmac.ComputeHash(normalized);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalized);
        }
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
