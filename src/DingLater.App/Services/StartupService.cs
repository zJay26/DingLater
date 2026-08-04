using Microsoft.Win32;

namespace DingLater.App.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DingLater";

    public Task<(bool Available, bool Enabled)> GetStateAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var current = key?.GetValue(ValueName) as string;
            return Task.FromResult((true, string.Equals(current, StartupCommand(), StringComparison.Ordinal)));
        }
        catch
        {
            return Task.FromResult((false, false));
        }
    }

    public Task<bool> SetEnabledAsync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, StartupCommand(), RegistryValueKind.String);
                return Task.FromResult(true);
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return Task.FromResult(false);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static string StartupCommand()
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("无法确定 DingLater.exe 的位置。");
        if (executable.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DingLater.exe 路径包含不受支持的字符。");
        }

        return $"\"{executable}\" --startup";
    }
}
