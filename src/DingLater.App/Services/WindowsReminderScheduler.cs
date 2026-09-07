using DingLater.Core.Models;
using DingLater.Core.Services;

namespace DingLater.App.Services;

public sealed class ReminderDueEventArgs(StoredMessage message, bool includePreview) : EventArgs
{
    public StoredMessage Message { get; } = message;
    public bool IncludePreview { get; } = includePreview;
}

public sealed class WindowsReminderScheduler : IReminderScheduler, IDisposable
{
    private static readonly TimeSpan ClockRecheckInterval = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ScheduledReminder> _scheduled = [];
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private bool _disposed;

    public WindowsReminderScheduler(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler<ReminderDueEventArgs>? ReminderDue;

    public Task ScheduleAsync(
        StoredMessage message,
        DateTimeOffset dueAt,
        bool includePreview,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (message.State != InboxState.Snoozed || dueAt >= message.ExpiresAt)
            {
                throw new ArgumentOutOfRangeException(nameof(dueAt), "提醒必须早于消息清理时间，且消息处于稍后提醒状态。");
            }

            _scheduled[message.Id] = new ScheduledReminder(message, dueAt, includePreview);
            ArmTimer();
        }

        return Task.CompletedTask;
    }

    public Task CancelAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_disposed && _scheduled.Remove(messageId))
            {
                ArmTimer();
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scheduled.Clear();
            _timer.Dispose();
        }
    }

    private void OnTimer(object? state)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var ready = _scheduled.Values.Where(item => item.DueAt <= now || item.Message.ExpiresAt <= now)
                .OrderBy(item => item.DueAt).ToList();
            foreach (var item in ready)
            {
                // An earlier callback can cancel or replace another item in this batch.
                if (_disposed || !_scheduled.TryGetValue(item.Message.Id, out var current) || !ReferenceEquals(item, current))
                {
                    continue;
                }

                _scheduled.Remove(item.Message.Id);
                if (item.Message.ExpiresAt <= now)
                {
                    continue;
                }

                try
                {
                    ReminderDue?.Invoke(this, new ReminderDueEventArgs(item.Message, item.IncludePreview));
                }
                catch
                {
                    // A notification consumer must not terminate the timer or the application.
                }
            }

            if (!_disposed)
            {
                ArmTimer();
            }
        }
    }

    private void ArmTimer()
    {
        var delay = Timeout.InfiniteTimeSpan;
        if (_scheduled.Count > 0)
        {
            delay = _scheduled.Values.Min(item => item.DueAt) - _timeProvider.GetUtcNow();
            delay = delay <= TimeSpan.Zero ? TimeSpan.Zero
                : delay > ClockRecheckInterval ? ClockRecheckInterval : delay;
        }

        // Recheck wall time after sleep or clock changes, without one Task/CTS per message.
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private sealed record ScheduledReminder(StoredMessage Message, DateTimeOffset DueAt, bool IncludePreview);
}
