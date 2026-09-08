namespace DingLater.Core.Updates;

public static class PortableUpdateInstaller
{
    public static void ValidateDirectories(string source, string destination)
    {
        var sourcePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + Path.DirectorySeparatorChar;
        var destinationPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        if (sourcePrefix.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase)
            || destinationPrefix.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("下载目录与程序目录不能相同或互相包含，请在设置中选择其他下载目录。");
        }

        PortableUpdatePackage.RejectReparsePoints(source);
        PortableUpdatePackage.RejectReparsePoints(destination);
    }

    // Invoked by the staged helper only after the original process has exited.
    // The backup is retained for recovery, including if the machine loses power.
    public static async Task<string> InstallAsync(string source, string destination, Version version, CancellationToken cancellationToken)
    {
        ValidateDirectories(source, destination);
        var next = await PortableUpdatePackage.ValidateAsync(source, version, cancellationToken).ConfigureAwait(false);
        var previous = await PortableUpdatePackage.ReadManifestAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!UpdatePolicy.TryParseVersion(previous.Version, out var oldVersion) || oldVersion >= version)
        {
            throw new InvalidDataException("仅支持更新到更高版本。");
        }

        var paths = previous.Files.Select(file => file.Path).Concat(next.Files.Select(file => file.Path))
            .Append("package-files.json").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var backup = PortableUpdatePackage.SafePath(destination, $".dinglater-backup/{previous.Version}-{Guid.NewGuid():N}");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PortableUpdatePackage.SafePath(destination, relative);
            if (Directory.Exists(target))
            {
                throw new IOException($"更新文件位置已被文件夹占用：{relative}");
            }

            if (File.Exists(target))
            {
                var saved = PortableUpdatePackage.SafePath(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Copy(target, saved, overwrite: false);
                existing.Add(relative);
            }
        }

        var changed = new List<string>();
        try
        {
            var replacements = next.Files.Select(file => file.Path).Append("package-files.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = PortableUpdatePackage.SafePath(destination, relative);
                changed.Add(relative);
                if (replacements.Contains(relative))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(PortableUpdatePackage.SafePath(source, relative), target, overwrite: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }

            await PortableUpdatePackage.ValidateAsync(destination, version, cancellationToken).ConfigureAwait(false);
            return backup;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var relative in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var target = PortableUpdatePackage.SafePath(destination, relative);
                    if (existing.Contains(relative))
                    {
                        var saved = PortableUpdatePackage.SafePath(backup, relative);
                        // A locked file that was never changed needs no restore.
                        if (!File.Exists(target) || await PortableUpdatePackage.HashAsync(saved, CancellationToken.None).ConfigureAwait(false)
                            != await PortableUpdatePackage.HashAsync(target, CancellationToken.None).ConfigureAwait(false))
                        {
                            File.Copy(saved, target, overwrite: true);
                        }
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            if (errors.Count > 0)
            {
                throw new IOException($"更新失败，部分文件无法恢复。请退出其他实例后从备份恢复：{backup}", new AggregateException(errors.Prepend(failure)));
            }

            throw new IOException($"更新未完成，已恢复原程序。备份：{backup}。{failure.Message}", failure);
        }
    }
}
