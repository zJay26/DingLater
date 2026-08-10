using System.Security.Cryptography;
using System.Text;
using DingLater.Core.Security;

namespace DingLater.Tests;

[TestClass]
public sealed class MessageCryptoTests
{
    [TestMethod]
    public void Encrypt_RoundTrips_WithoutPlaintextBytes()
    {
        using var crypto = new MessageCrypto(RandomNumberGenerator.GetBytes(32));
        const string marker = "DINGLATER_PRIVACY_MARKER_7f81f04e";

        var encrypted = crypto.Encrypt(marker);

        Assert.AreEqual(marker, crypto.Decrypt(encrypted));
        Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains(marker, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Dpapi_IsBoundToEntropyAndCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("DPAPI is Windows-only.");
        }

        var protector = new DpapiSecretProtector();
        var clear = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = protector.Protect(clear);

        CollectionAssert.AreEqual(clear, protector.Unprotect(protectedBytes));
        Assert.ThrowsExactly<CryptographicException>(() =>
            ProtectedData.Unprotect(protectedBytes, "different-entropy"u8.ToArray(), DataProtectionScope.CurrentUser));
    }

    [TestMethod]
    public void Decrypt_RejectsAuthenticatedCiphertextTampering()
    {
        using var crypto = new MessageCrypto(RandomNumberGenerator.GetBytes(32));
        var encrypted = crypto.Encrypt("不可被篡改的消息");
        encrypted[^1] ^= 0x40;

        Assert.ThrowsExactly<AuthenticationTagMismatchException>(() => crypto.Decrypt(encrypted));
    }

    [TestMethod]
    public void Dispose_RejectsFurtherCryptographicOperations()
    {
        var crypto = new MessageCrypto(RandomNumberGenerator.GetBytes(32));

        crypto.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => crypto.Encrypt("消息"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => crypto.Decrypt(new byte[29]));
        Assert.ThrowsExactly<ObjectDisposedException>(() => crypto.Fingerprint("消息"));
    }
}
