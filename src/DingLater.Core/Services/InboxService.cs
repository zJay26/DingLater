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
        if (_started || _settings.CapturePaused)
        {
            return;
        }

        foreach (var source in _sources)
        {
            source.BatchCaptured += OnBatchCapturedAsync;
            source.HealthChanged += OnHealthChanged;
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        _started = true;
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        foreach (var source in _sources)
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
            source.BatchCaptured -= OnBatchCapturedAsync;
            source.HealthChanged -= OnHealthChanged;
        }

        _started = false;
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
        await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        await _store.UpdateStateAsync(id, InboxState.Handled, null, _timeProvider.GetLocalNow(), cancellationToken).ConfigureAwait(false);
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RestoreInboxAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireMessageAsync(id, cancellationToken).ConfigureAwait(false);
        await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        await _store.UpdateStateAsync(id, InboxState.Inbox, null, _timeProvider.GetLocalNow(), cancellationToken).ConfigureAwait(false);
        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireMessageAsync(id, cancellationToken).ConfigureAwait(false);
        await _reminders.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        if (!await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("消息已不存在。");
        }

        InboxChanged?.Invoke(this, EventArgs.Empty);
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
                    await _reminders.CancelAsync(message.Id, cancellationToken).ConfigureAwait(false);
                    await _store.UpdateStateAsync(
                        message.Id,
                        InboxState.Inbox,
                        null,
                        _timeProvider.GetLocalNow(),
                        cancellationToken).ConfigureAwait(false);
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
        foreach (var message in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            await _reminders.CancelAsync(message.Id, cancellationToken).ConfigureAwait(false);
        }

        await _store.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
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
        await StopCaptureAsync().ConfigureAwait(false);
        foreach (var source in _sources)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }

        await _store.DisposeAsync().ConfigureAwait(false);
        _captureGate.Dispose();
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
