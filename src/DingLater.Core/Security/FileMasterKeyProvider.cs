using System.Security.Cryptography;

namespace DingLater.Core.Security;

public sealed class FileMasterKeyProvider
{
    private readonly string _keyPath;
    private readonly ISecretProtector _protector;

    public FileMasterKeyProvider(string keyPath, ISecretProtector protector)
    {
        _keyPath = keyPath;
        _protector = protector;
    }

    public byte[] GetOrCreate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        if (File.Exists(_keyPath))
        {
            return _protector.Unprotect(File.ReadAllBytes(_keyPath));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = _protector.Protect(key);
        var temporaryPath = _keyPath + ".new";
        File.WriteAllBytes(temporaryPath, protectedKey);
        File.Move(temporaryPath, _keyPath, overwrite: true);
        return key;
    }
}
