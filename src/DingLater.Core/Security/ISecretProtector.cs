namespace DingLater.Core.Security;

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedBytes);
}
