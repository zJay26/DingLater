using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DingLater.Core.Updates;

public static class PortableUpdatePackage
{
    public sealed record PackageFile(string Path, long Size, string Sha256);
    public sealed record Manifest(int Schema, string Version, PackageFile[] Files);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const long MaximumExpandedSize = 2L * 1024 * 1024 * 1024;

    public static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    public static string SafePath(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsDeviceName(part)))
        {
            throw new InvalidDataException($"更新包包含不安全路径：{relative}");
        }

        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新路径超出目标目录。");
        }

        RejectReparsePoints(path);
        return path;
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("更新目录不能经过符号链接或目录联接，请选择普通文件夹。");
            }
        }
    }

    public static async Task ExtractAsync(string archivePath, string destination, Version expectedVersion, CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination))
        {
            throw new IOException("更新解压目录已存在。");
        }

        RejectReparsePoints(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (archive.Entries.Count > 5000)
        {
            throw new InvalidDataException("更新包文件数过多。");
        }

        // Validate the complete entry table before writing any file.
        foreach (var entry in archive.Entries)
        {
            var path = SafePath(destination, entry.FullName.TrimEnd('/', '\\'));
            total = checked(total + entry.Length);
            if (!names.Add(path) || total > MaximumExpandedSize
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("更新包包含重复路径、链接或过大的文件。");
            }
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafePath(destination, entry.FullName.TrimEnd('/', '\\'));
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                written += count;
                if (written > entry.Length)
                {
                    throw new InvalidDataException("解压文件超出声明大小。");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
        }

        var manifest = await ValidateAsync(destination, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (archive.Entries.Count(entry => entry.Name.Length > 0) != manifest.Files.Length + 1)
        {
            throw new InvalidDataException("更新包包含清单外的文件。");
        }
    }

    public static async Task<Manifest> ReadManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var path = SafePath(directory, "package-files.json");
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("更新包清单过大。");
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (manifest is null || manifest.Schema != 1 || manifest.Files is not { Length: > 0 and <= 5000 }
            || !UpdatePolicy.TryParseVersion(manifest.Version, out _))
        {
            throw new InvalidDataException("更新包文件清单无效。");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            var full = SafePath(directory, file.Path);
            total = checked(total + file.Size);
            if (!names.Add(full) || file.Size < 0 || total > MaximumExpandedSize || file.Sha256 is null || !IsSha256(file.Sha256)
                || full.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包文件清单包含无效或重复记录。");
            }
        }

        if (!names.Contains(SafePath(directory, "DingLater.exe")) || !names.Contains(SafePath(directory, "DingLater.dll")))
        {
            throw new InvalidDataException("更新包缺少 DingLater 主程序。");
        }

        return manifest;
    }

    public static async Task<Manifest> ValidateAsync(string directory, Version expectedVersion, CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
        if (manifest.Version != expectedVersion.ToString())
        {
            throw new InvalidDataException("更新包版本与发布版本不符。");
        }

        foreach (var file in manifest.Files)
        {
            var path = SafePath(directory, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size
                || !file.Sha256.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"更新文件缺失或损坏：{file.Path}");
            }
        }

        return manifest;
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(name[3]));
    }
}
