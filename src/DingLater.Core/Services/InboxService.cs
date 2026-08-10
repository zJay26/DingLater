using DingLater.Core.Capture;
using DingLater.Core.Models;
using DingLater.Core.Storage;

namespace DingLater.Core.Services;

public sealed class InboxService : IAsyncDisposable
{
    private readonly IMessageStore _store;
    private readonly IReminderScheduler _reminders;
    private readonly IReadOnlyList<ICaptureSource> _sources;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private AppSettings _settings = new();
    private bool _started;

    public InboxService(
        IMessageStore store,
        IReminderScheduler reminders,
        IEnumerable<ICaptureSource> sources,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _reminders = reminders;
        _sources = sources.ToList();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? InboxChanged;
    public event EventHandler<CaptureHealth>? HealthChanged;

    public AppSettings Settings => _settings;
    public IReadOnlyList<CaptureHealth> Health => _sources.Select(source => source.Health).ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _settings = await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        await RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartCaptureAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started || _settings.CapturePaused)
            {
                return;
            }

            var subscribedSources = new List<ICaptureSource>();
            try
            {
                foreach (var source in _sources)
                {
                    source.BatchCaptured += OnBatchCapturedAsync;
                    source.HealthChanged += OnHealthChanged;
                    subscribedSources.Add(source);
                    await source.StartAsync(cancellationToken).ConfigureAwait(false);
                }

                _started = true;
            }
            catch (Exception startupException)
            {
                var failures = new List<Exception> { startupException };
                foreach (var source in subscribedSources.AsEnumerable().Reverse())
                {
                    try
                    {
                        await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                    }
                    finally
                    {
                        source.BatchCaptured -= OnBatchCapturedAsync;
                        source.HealthChanged -= OnHealthChanged;
                    }
                }

                _started = false;
                if (failures.Count > 1)
                {
                    throw new AggregateException("捕获源启动失败，且部分回滚操作也未完成。", failures);
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            var failures = new List<Exception>();
            foreach (var source in _sources.AsEnumerable().Reverse())
            {
                try
                {
                    await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                finally
                {
                    source.BatchCaptured -= OnBatchCapturedAsync;
                    source.HealthChanged -= OnHealthChanged;
                }
            }

            _started = false;
            if (failures.Count > 0)
            {
                throw new AggregateException("一个或多个捕获源未能正常停止。", failures);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<IReadOnlyList<StoredMessage>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListAsync(cancellationToken);

    public Task<StoredMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _store.GetAsync(id, cancellationToken);

    public async Task SnoozeAsync(Guid id, DateTimeOffset dueAt, CancellationToken cancellationToken = default)
    {
        var message = await RequireMessageAsync(id, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetLocalNow();
        if (dueAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(dueAt), "稍后时间必须晚于当前时间。");
        }

        if (dueAt > message.ExpiresAt)
        {
            throw new ArgumentOutOfRangeException(nameof(dueAt), "稍后时间不能超过消息到期时间。");
        }

        await _store.UpdateStateAsync(id, InboxState.Snoozed, dueAt, now, cancellationToken).ConfigureAwait(false);
        await _reminders.ScheduleAsync(
            message with { State = InboxState.Snoozed, SnoozedUntil = dueAt, UpdatedAt = now },
            dueAt,
            _settings.ShowReminderPreview,
            cancellationToken).ConfigureAwait(false);
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkHandledAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireMessageAsync(id, cancellationToken).ConfigureAwait(false);
        await _store.UpdateStateAsync(id, InboxState.Handled, null, _timeProvider.GetLocalNow(), cancellationToken).ConfigureAwait(false);
        await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> MarkAllHandledAsync(
        InboxState currentState,
        CancellationToken cancellationToken = default)
    {
        if (currentState is not InboxState.Inbox and not InboxState.Snoozed)
        {
            throw new ArgumentOutOfRangeException(nameof(currentState), "只能批量处理待处理或稍后提醒消息。");
        }

        IReadOnlyList<Guid> snoozedIds = [];
        if (currentState == InboxState.Snoozed)
        {
            snoozedIds = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(message => message.State == InboxState.Snoozed)
                .Select(message => message.Id)
                .ToList();
        }

        var updated = await _store.UpdateStateByStateAsync(
            currentState,
            InboxState.Handled,
            _timeProvider.GetLocalNow(),
            cancellationToken).ConfigureAwait(false);
        if (currentState == InboxState.Snoozed)
        {
            foreach (var id in snoozedIds)
            {
                await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }

        if (updated > 0)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }

        return updated;
    }

    public async Task RestoreInboxAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireMessageAsync(id, cancellationToken).ConfigureAwait(false);
        await _store.UpdateStateAsync(id, InboxState.Inbox, null, _timeProvider.GetLocalNow(), cancellationToken).ConfigureAwait(false);
        await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireMessageAsync(id, cancellationToken).ConfigureAwait(false);
        if (!await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("消息已不存在。");
        }

        await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> DeleteHandledAsync(CancellationToken cancellationToken = default)
    {
        var deleted = await _store.DeleteByStateAsync(InboxState.Handled, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }

        return deleted;
    }

    public Task<int> CountRetentionImpactAsync(int retentionDays, CancellationToken cancellationToken = default) =>
        _store.CountExpiringWhenRetentionChangesAsync(retentionDays, _timeProvider.GetLocalNow(), cancellationToken);

    public async Task SaveSettingsAsync(AppSettings settings, bool applyRetention, CancellationToken cancellationToken = default)
    {
        settings = settings.Normalize();
        var previewChanged = settings.ShowReminderPreview != _settings.ShowReminderPreview;
        if (applyRetention && settings.RetentionDays != _settings.RetentionDays)
        {
            var removed = await _store.ApplyRetentionAsync(settings.RetentionDays, _timeProvider.GetLocalNow(), cancellationToken).ConfigureAwait(false);
            foreach (var id in removed)
            {
                await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
            }

            foreach (var message in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message.State == InboxState.Snoozed
                    && message.SnoozedUntil is { } dueAt
                    && dueAt > message.ExpiresAt)
                {
                    await _store.UpdateStateAsync(
                        message.Id,
                        InboxState.Inbox,
                        null,
                        _timeProvider.GetLocalNow(),
                        cancellationToken).ConfigureAwait(false);
                    await _reminders.CancelAsync(message.Id, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await _store.SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        _settings = settings;
        if (previewChanged)
        {
            var now = _timeProvider.GetLocalNow();
            foreach (var message in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message.State == InboxState.Snoozed && message.SnoozedUntil is { } dueAt && dueAt > now)
                {
                    await _reminders.ScheduleAsync(message, dueAt, settings.ShowReminderPreview, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetCapturePausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        var updated = _settings with { CapturePaused = paused };
        await _store.SaveSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
        _settings = updated;
        if (paused)
        {
            await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
        }

        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        var messageIds = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(message => message.Id)
            .ToList();
        await _store.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var messageId in messageIds)
        {
            await _reminders.CancelAsync(messageId, cancellationToken).ConfigureAwait(false);
        }

        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RunMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetLocalNow();
        var expired = await _store.DeleteExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var id in expired)
        {
            await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var released = await _store.ReleaseDueAsync(now, cancellationToken).ConfigureAwait(false);
        if (expired.Count > 0 || released.Count > 0)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RestoreReminderScheduleAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetLocalNow();
        foreach (var message in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message.State == InboxState.Snoozed
                && message.SnoozedUntil is { } dueAt
                && dueAt > now)
            {
                await _reminders.ScheduleAsync(
                    message,
                    dueAt,
                    _settings.ShowReminderPreview,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        try
        {
            await StopCaptureAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        foreach (var source in _sources)
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        _captureGate.Dispose();
        _lifecycleGate.Dispose();
        if (failures.Count > 0)
        {
            throw new AggregateException("DingLater 后台服务未能完全释放。", failures);
        }
    }

    private async Task OnBatchCapturedAsync(
        object sender,
        CaptureBatch batch,
        CancellationToken cancellationToken)
    {
        try
        {
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _store.AppendCaptureBatchAsync(
                    batch,
                    _settings.RetentionDays,
                    cancellationToken).ConfigureAwait(false);
                if (result.InsertedMessages.Count > 0)
                {
                    InboxChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Message content must never be written to logs. Source health exposes only generic failure state.
            throw;
        }
    }

    private void OnHealthChanged(object? sender, CaptureHealth health) => HealthChanged?.Invoke(this, health);

    private async Task<StoredMessage> RequireMessageAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
               ?? throw new KeyNotFoundException($"Message {id:D} does not exist.");
    }
}
