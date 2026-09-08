using DingLater.Core.Models;
using DingLater.Core.Updates;

namespace DingLater.App.ViewModels;

public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly IUpdateClient _client;
    private readonly Func<AppSettings> _settings;
    private readonly Func<Func<AppSettings, AppSettings>, Task<bool>> _save;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private bool _automaticCheck;

    public UpdateViewModel(IUpdateClient client, Version currentVersion, Func<AppSettings> settings,
        Func<Func<AppSettings, AppSettings>, Task<bool>> save)
    {
        _client = client;
        CurrentVersion = currentVersion;
        _settings = settings;
        _save = save;
    }

    public Version CurrentVersion { get; }
    public string CurrentVersionText => $"当前版本：{CurrentVersion}";
    public string Status { get; private set; } = "自动检查只获取版本信息，下载和重启更新都由你决定。";
    public bool IsBusy { get; private set; }
    public bool IsDownloading { get; private set; }
    public double Progress { get; private set; }
    public bool ShowNotice { get; private set; }
    public UpdateRelease? AvailableRelease { get; private set; }
    public PreparedUpdate? Prepared { get; private set; }
    public string AvailableVersionText => AvailableRelease is null ? string.Empty : $"发现新版本 {AvailableRelease.Version}";
    public string DownloadDirectory => UpdatePolicy.DownloadDirectory(_settings());
    public string LastCheckText => _settings().LastUpdateCheckUtc is { } last
        ? $"上次检查尝试：{last.ToLocalTime():yyyy-MM-dd HH:mm}" : "尚未检查更新";
    public bool CanCheck => !IsBusy;
    public bool CanDownload => !IsBusy && AvailableRelease is not null && Prepared is null;
    public bool CanInstall => !IsBusy && Prepared is not null;
    public bool CanSkip => !IsBusy && AvailableRelease is not null;
    public Func<PreparedUpdate, Task>? InstallRequested { get; set; }

    public Task CheckIfDueAsync(DateTimeOffset? now = null) => UpdatePolicy.IsDue(_settings(), now ?? DateTimeOffset.UtcNow)
        ? CheckAsync(manual: false) : Task.CompletedTask;

    public async Task CheckAsync(bool manual = true)
    {
        if (_disposed || IsBusy || (!manual && !UpdatePolicy.IsDue(_settings(), DateTimeOffset.UtcNow)))
        {
            return;
        }

        IsBusy = true;
        _automaticCheck = !manual;
        Status = "正在检查 GitHub 上的最新正式版本…";
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        Refresh();
        try
        {
            // Persist attempts, including failures, so restart/offline conditions do not hammer GitHub.
            if (!await _save(settings => settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow }))
            {
                throw new IOException("无法保存检查时间，请稍后重试。");
            }

            operation.Token.ThrowIfCancellationRequested();
            var release = await _client.CheckAsync(CurrentVersion, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!manual && !_settings().AutomaticallyCheckUpdates)
            {
                return;
            }

            if (release is not null && !manual && _settings().SkippedUpdateVersion == release.Version.ToString())
            {
                Status = $"已跳过版本 {release.Version}，仍会检查之后的版本。";
                return;
            }

            if (Prepared?.Release.Version != release?.Version)
            {
                Prepared = null;
            }

            AvailableRelease = release;
            ShowNotice = release is not null;
            Status = release is null ? "当前已是最新正式版本。" : Prepared is null
                ? $"发现 {release.Version}（{release.Size / 1024d / 1024d:F1} MB），可选择下载或跳过此版本。"
                : $"新版已下载并通过校验：{Prepared.ArchivePath}。可随时选择重启更新。";
        }
        catch (OperationCanceledException)
        {
            Status = operation.IsCancellationRequested ? "检查已取消。" : "检查超时，请稍后重试。";
        }
        catch (Exception exception)
        {
            Status = $"检查更新失败：{exception.Message} 可手动重试。";
        }
        finally
        {
            _operation = null;
            _automaticCheck = false;
            IsBusy = false;
            Refresh();
        }
    }

    public async Task DownloadAsync()
    {
        if (_disposed || !CanDownload || AvailableRelease is not { } release)
        {
            return;
        }

        IsBusy = IsDownloading = true;
        Progress = 0;
        Status = "正在下载更新包…";
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        Refresh();
        try
        {
            Prepared = await _client.DownloadAsync(release, DownloadDirectory, new Progress<double>(value =>
            {
                if (_disposed || _operation != operation)
                {
                    return;
                }

                Progress = value;
                if (value >= 95)
                {
                    Status = "正在解压并校验更新文件…";
                }

                Refresh();
            }), operation.Token);
            Status = $"新版已下载并通过校验：{Prepared.ArchivePath}。可随时选择重启更新。";
        }
        catch (OperationCanceledException)
        {
            Status = operation.IsCancellationRequested ? "下载已取消，可以重新下载。" : "下载超时，可以重试。";
        }
        catch (Exception exception)
        {
            Status = $"下载更新失败：{exception.Message}";
        }
        finally
        {
            _operation = null;
            IsBusy = IsDownloading = false;
            Refresh();
        }
    }

    public async Task InstallAsync()
    {
        if (_disposed || !CanInstall || Prepared is not { } prepared || InstallRequested is null)
        {
            return;
        }

        IsBusy = true;
        Status = "正在准备重启更新…";
        Refresh();
        try
        {
            await InstallRequested(prepared);
        }
        catch (Exception exception)
        {
            Status = $"无法开始更新：{exception.Message} 下载包仍保留，可重试或手动解压更新。";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    public async Task SkipAsync()
    {
        if (_disposed || !CanSkip || AvailableRelease is not { } release)
        {
            return;
        }

        IsBusy = true;
        Refresh();
        try
        {
            if (await _save(settings => settings with { SkippedUpdateVersion = release.Version.ToString() }))
            {
                AvailableRelease = null;
                Prepared = null;
                ShowNotice = false;
                Status = $"已跳过版本 {release.Version}。手动检查可再次显示此版本。";
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    public void SettingsChanged()
    {
        if (!_settings().AutomaticallyCheckUpdates && _automaticCheck)
        {
            _operation?.Cancel();
        }

        Refresh();
    }

    public void DismissNotice()
    {
        ShowNotice = false;
        Refresh();
    }

    public void Cancel() => _operation?.Cancel();

    private void Refresh()
    {
        if (!_disposed)
        {
            OnPropertyChanged(string.Empty);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        (_client as IDisposable)?.Dispose();
    }
}
