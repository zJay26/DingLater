using System.Security.Cryptography;
using System.Text;

namespace DingLater.Core.Capture.DingTalkDatabase;

public static class DingTalkV3KeyDeriver
{
    private static readonly byte[] FixedSalt = "666DingT"u8.ToArray();

    public static byte[] Derive(string userId, string accountSalt)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || userId.Length is < 6 or > 15
            || userId.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("The DingTalk user identifier has an unsupported format.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(accountSalt)
            || accountSalt.Length is < 16 or > 128
            || accountSalt.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The DingTalk V3 salt has an unsupported format.", nameof(accountSalt));
        }

        var password = Encoding.UTF8.GetBytes(userId + accountSalt);
        var derived = new byte[32];
        byte[]? digest = null;
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(
                password,
                FixedSalt,
                derived,
                iterations: 1000,
                HashAlgorithmName.SHA1);
            digest = MD5.HashData(derived);
            var hex = Convert.ToHexStringLower(digest);
            return Encoding.ASCII.GetBytes(hex[..16]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(derived);
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }
}
