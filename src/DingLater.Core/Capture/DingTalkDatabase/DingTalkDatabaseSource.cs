using DingLater.Core.Models;
using DingLater.Core.Storage;

namespace DingLater.Core.Capture.DingTalkDatabase;

public sealed class DingTalkDatabaseSource : ICaptureSource
{
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AccountRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly IMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly DingTalkAccountLocator _locator = new();
    private readonly DingTalkSnapshotReader _snapshotReader = new();
    private readonly DingTalkMessageReader _messageReader = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _loopTask;
    private FileSystemWatcher? _watcher;
    private DingTalkAccount? _account;
    private DingTalkSnapshotReader.FileStamp? _lastStamp;
    private DateTimeOffset _lastAccountRefresh;
    private int _observationCount;
    private int _candidateCount;
    private int _emptyBodyCount;

    public DingTalkDatabaseSource(IMessageStore store, TimeProvider? timeProvider = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Health = NewHealth(CaptureHealthState.Stopped, "本地数据库捕获尚未启动");
    }

    public string Name => "钉钉本地数据库（只读）";
    public CaptureHealth Health { get; private set; }
    public event CaptureBatchHandler? BatchCaptured;
    public event EventHandler<CaptureHealth>? HealthChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lifetime is not null)
            {
                return;
            }

            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            try
            {
                SetHealth(CaptureHealthState.Starting, "正在定位钉钉 V3 数据库");
                using var initialCapture = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
                await CaptureOnceAsync(force: true, initialCapture.Token).ConfigureAwait(false);
                _loopTask = Task.Run(() => CaptureLoopAsync(lifetime.Token), CancellationToken.None);
            }
            catch
            {
                CleanupStoppedState(lifetime);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lifetime is null)
            {
                return;
            }

            var lifetime = _lifetime;
            lifetime.Cancel();
            Wake();
            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            CleanupStoppedState(lifetime);
            SetHealth(CaptureHealthState.Stopped, "本地数据库捕获已停止");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wakeSignal.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signaled = await _wakeSignal.WaitAsync(FallbackPollInterval, cancellationToken).ConfigureAwait(false);
                if (signaled)
                {
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                }

                await CaptureOnceAsync(force: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (DingTalkCaptureException exception)
            {
                SetFailure(exception);
            }
            catch (Exception)
            {
                SetHealth(
                    CaptureHealthState.Degraded,
                    "本轮只读捕获失败，将自动重试",
                    errorCode: "capture_cycle_failed");
            }
        }
    }

    private async Task CaptureOnceAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetLocalNow();
            if (_account is null || now - _lastAccountRefresh >= AccountRefreshInterval)
            {
                await RefreshAccountAsync(cancellationToken).ConfigureAwait(false);
                force = true;
            }

            if (_account is null)
            {
                return;
            }

            var stamp = DingTalkSnapshotReader.GetStamp(_account);
            if (!force && _lastStamp == stamp)
            {
                return;
            }

            await using var snapshot = await _snapshotReader.OpenAsync(_account, cancellationToken).ConfigureAwait(false);
            _observationCount++;
            var currentPositions = await _messageReader.ValidateAndReadPositionsAsync(
                snapshot.Connection,
                cancellationToken).ConfigureAwait(false);
            var storedPositions = await _store.GetCaptureCheckpointsAsync(
                CaptureSourceKind.DingTalkDatabase,
                _account.AccountFingerprint,
                cancellationToken).ConfigureAwait(false);

            var requiresBaseline = storedPositions.Count != DingTalkMessageReader.PartitionCount
                                   || currentPositions.Any(pair =>
                                       !storedPositions.TryGetValue(pair.Key, out var stored)
                                       || stored > pair.Value);
            if (requiresBaseline)
            {
                var baseline = currentPositions.Select(pair => new CaptureCheckpoint(
                    CaptureSourceKind.DingTalkDatabase,
                    _account.AccountFingerprint,
                    pair.Key,
                    pair.Value)).ToList();
                await EmitAsync(new CaptureBatch([], baseline), cancellationToken).ConfigureAwait(false);
                _lastStamp = stamp;
                SetHealth(
                    CaptureHealthState.Healthy,
                    _account.MatchedAccountCount > 1
                        ? "已选择最近活动账号并建立捕获起点，只读取之后的新消息"
                        : "已建立捕获起点，只读取之后的新消息",
                    databaseFound: true,
                    keyDerived: true,
                    walValid: true,
                    schemaCompatible: true);
                return;
            }

            if (!HasAdvancedPositions(storedPositions, currentPositions))
            {
                _lastStamp = stamp;
                SetHealth(
                    CaptureHealthState.Healthy,
                    "只读数据库捕获正常，正在等待新消息",
                    databaseFound: true,
                    keyDerived: true,
                    walValid: true,
                    schemaCompatible: true);
                return;
            }

            var settings = await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            var result = await _messageReader.ReadNewAsync(
                snapshot.Connection,
                _account,
                storedPositions,
                currentPositions,
                settings.GroupCaptureMode,
                now,
                cancellationToken).ConfigureAwait(false);
            if (result.Checkpoints.Count > 0)
            {
                await EmitAsync(
                    new CaptureBatch(result.Messages, result.Checkpoints),
                    cancellationToken).ConfigureAwait(false);
            }

            _candidateCount += result.CandidateCount;
            _emptyBodyCount += result.EmptyBodyCount;
            if (result.HasMore)
            {
                Wake();
            }
            else
            {
                _lastStamp = stamp;
            }
            SetHealth(
                CaptureHealthState.Healthy,
                result.Messages.Count > 0
                    ? result.HasMore
                        ? $"已只读捕获 {result.Messages.Count} 条新消息，正在继续处理本地积压"
                        : $"已只读捕获 {result.Messages.Count} 条新消息"
                    : "只读数据库捕获正常，正在等待新消息",
                lastCaptureAt: result.Messages.Count > 0 ? now : Health.LastCaptureAt,
                databaseFound: true,
                keyDerived: true,
                walValid: true,
                schemaCompatible: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DingTalkCaptureException exception)
        {
            SetFailure(exception);
        }
        catch (IOException)
        {
            SetHealth(
                CaptureHealthState.Degraded,
                "钉钉数据库正在变化，将在下一轮自动重试",
                databaseFound: _account is not null,
                keyDerived: _account is not null,
                errorCode: "database_busy");
        }
        catch (UnauthorizedAccessException)
        {
            SetHealth(
                CaptureHealthState.Unavailable,
                "当前 Windows 用户无法只读访问钉钉数据库",
                databaseFound: true,
                errorCode: "database_access_denied");
        }
    }

    internal static bool HasAdvancedPositions(
        IReadOnlyDictionary<int, long> storedPositions,
        IReadOnlyDictionary<int, long> currentPositions) =>
        currentPositions.Any(pair =>
            storedPositions.TryGetValue(pair.Key, out var stored)
            && pair.Value > stored);

    private void CleanupStoppedState(CancellationTokenSource lifetime)
    {
        _watcher?.Dispose();
        _watcher = null;
        _account?.Dispose();
        _account = null;
        _lastStamp = null;
        _loopTask = null;
        if (ReferenceEquals(_lifetime, lifetime))
        {
            _lifetime = null;
        }

        lifetime.Dispose();
    }

    private async Task RefreshAccountAsync(CancellationToken cancellationToken)
    {
        _lastAccountRefresh = _timeProvider.GetLocalNow();
        var discovered = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        if (_account is not null
            && string.Equals(_account.AccountFingerprint, discovered.AccountFingerprint, StringComparison.Ordinal))
        {
            discovered.Dispose();
            return;
        }

        _watcher?.Dispose();
        _account?.Dispose();
        _account = discovered;
        _lastStamp = null;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(discovered.DatabasePath)!)
        {
            Filter = "dingtalk.db*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnDatabaseChanged;
        _watcher.Created += OnDatabaseChanged;
        _watcher.Deleted += OnDatabaseChanged;
        _watcher.Renamed += OnDatabaseRenamed;
    }

    private async Task EmitAsync(CaptureBatch batch, CancellationToken cancellationToken)
    {
        var handlers = BatchCaptured?.GetInvocationList().Cast<CaptureBatchHandler>().ToList() ?? [];
        if (handlers.Count == 0)
        {
            throw new DingTalkCaptureException("capture_sink_missing", "DingLater 本地存储尚未连接。");
        }

        foreach (var handler in handlers)
        {
            await handler(this, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnDatabaseChanged(object sender, FileSystemEventArgs args) => Wake();
    private void OnDatabaseRenamed(object sender, RenamedEventArgs args) => Wake();

    private void Wake()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            try
            {
                _wakeSignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private void SetFailure(DingTalkCaptureException exception)
    {
        var state = exception.ErrorCode switch
        {
            "dingtalk_data_not_found" or "v3_database_not_found" => CaptureHealthState.Unavailable,
            "real_uid_not_found" or "v3_key_mismatch" => CaptureHealthState.Degraded,
            "schema_incompatible" => CaptureHealthState.Faulted,
            _ => CaptureHealthState.Degraded
        };
        SetHealth(
            state,
            exception.Message,
            databaseFound: exception.ErrorCode != "dingtalk_data_not_found" && exception.ErrorCode != "v3_database_not_found",
            keyDerived: _account is not null,
            walValid: exception.ErrorCode.Contains("wal", StringComparison.Ordinal) ? false : null,
            schemaCompatible: exception.ErrorCode == "schema_incompatible" ? false : null,
            errorCode: exception.ErrorCode);
    }

    private CaptureHealth NewHealth(CaptureHealthState state, string detail) =>
        new(Name, state, detail, _timeProvider.GetLocalNow());

    private void SetHealth(
        CaptureHealthState state,
        string detail,
        DateTimeOffset? lastCaptureAt = null,
        bool? databaseFound = null,
        bool? keyDerived = null,
        bool? walValid = null,
        bool? schemaCompatible = null,
        string errorCode = "")
    {
        var now = _timeProvider.GetLocalNow();
        Health = new CaptureHealth(
            Name,
            state,
            detail,
            now,
            lastCaptureAt ?? Health.LastCaptureAt,
            PermissionGranted: true,
            DingTalkDetected: databaseFound,
            EmptyBodyCount: _emptyBodyCount,
            LastObservationAt: _observationCount > 0 ? now : Health.LastObservationAt,
            ObservationCount: _observationCount,
            CandidateCount: _candidateCount,
            DatabaseFound: databaseFound,
            KeyDerived: keyDerived,
            WalValid: walValid,
            SchemaCompatible: schemaCompatible,
            ErrorCode: errorCode);
        HealthChanged?.Invoke(this, Health);
    }
}
