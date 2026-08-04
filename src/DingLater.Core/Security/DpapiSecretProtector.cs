using System.Security.Cryptography;

namespace DingLater.Core.Security;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "DingLater.LocalKey.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) =>
        ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}
