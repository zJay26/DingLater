using System.Diagnostics;
using System.Text.Json;
using DingLater.Core.Updates;

namespace DingLater.App.Services;

internal static class UpdateHandoff
{
    internal sealed record Request(int ParentId, long ParentStartTicks, string TargetDirectory, string Version, bool SmokeTest);

    public static async Task StartAsync(PreparedUpdate update)
    {
        PortableUpdateInstaller.ValidateDirectories(update.Directory, AppContext.BaseDirectory);
        await PortableUpdatePackage.ValidateAsync(update.Directory, update.Release.Version, CancellationToken.None);
        var requestPath = Path.Combine(Path.GetDirectoryName(update.Directory)!, $"update-{Guid.NewGuid():N}.json");
        using var parent = Process.GetCurrentProcess();
        var request = new Request(parent.Id, parent.StartTime.ToUniversalTime().Ticks, AppContext.BaseDirectory, update.Release.Version.ToString(),
            Environment.GetCommandLineArgs().Contains("--package-smoke-test", StringComparer.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        var start = new ProcessStartInfo(Path.Combine(update.Directory, "DingLater.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = update.Directory,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(requestPath);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(requestPath + ".ready"))
        {
            if (File.Exists(requestPath + ".error"))
            {
                throw new IOException(await File.ReadAllTextAsync(requestPath + ".error"));
            }

            if (helper.HasExited)
            {
                throw new IOException("更新助手未能启动，原程序继续运行。");
            }

            await Task.Delay(100, timeout.Token);
        }
    }

    public static async Task ApplyAsync(string requestPath)
    {
        requestPath = Path.GetFullPath(requestPath);
        var stage = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var work = Path.GetDirectoryName(stage)!;
        if (!string.Equals(Path.GetDirectoryName(requestPath), work, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新请求必须位于下载目录。");
        }

        PortableUpdatePackage.RejectReparsePoints(requestPath);
        try
        {
            var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(requestPath))
                ?? throw new InvalidDataException("更新请求无效。");
            if (!UpdatePolicy.TryParseVersion(request.Version, out var version))
            {
                throw new InvalidDataException("更新版本无效。");
            }

            PortableUpdateInstaller.ValidateDirectories(stage, request.TargetDirectory);
            await PortableUpdatePackage.ValidateAsync(stage, version, CancellationToken.None);
            await PortableUpdatePackage.ReadManifestAsync(request.TargetDirectory, CancellationToken.None);
            using var parent = Process.GetProcessById(request.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks
                || !string.Equals(parent.MainModule?.FileName, Path.Combine(request.TargetDirectory, "DingLater.exe"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("原程序身份不匹配。");
            }

            var lockPath = PortableUpdatePackage.SafePath(request.TargetDirectory, ".dinglater-update.lock");
            using var updateLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await File.WriteAllTextAsync(requestPath + ".ready", "ready");
            // Never kill the old process. A failed/cancelled handoff leaves it running.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
            await PortableUpdateInstaller.InstallAsync(stage, request.TargetDirectory, version, CancellationToken.None);
            var restart = new ProcessStartInfo(Path.Combine(request.TargetDirectory, "DingLater.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = request.TargetDirectory
            };
            if (request.SmokeTest)
            {
                restart.ArgumentList.Add("--package-smoke-test");
            }

            using var restarted = Process.Start(restart) ?? throw new IOException("新版已安装，但无法启动，请手动打开 DingLater.exe。");
            if (request.SmokeTest)
            {
                using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await restarted.WaitForExitAsync(restartTimeout.Token);
                if (restarted.ExitCode != 0)
                {
                    throw new IOException($"新版启动测试失败：{restarted.ExitCode}");
                }

                await File.WriteAllTextAsync(requestPath + ".success", "Updated and restarted using isolated synthetic data.");
            }
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(requestPath + ".error", exception.Message);
            throw;
        }
    }
}
