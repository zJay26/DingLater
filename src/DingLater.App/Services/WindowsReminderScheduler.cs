using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _scheduled = new();

    public event EventHandler<ReminderDueEventArgs>? ReminderDue;

    public Task ScheduleAsync(
        StoredMessage message,
        DateTimeOffset dueAt,
        bool includePreview,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelCore(message.Id);
        var lifetime = new CancellationTokenSource();
        _scheduled[message.Id] = lifetime;
        _ = WaitAndNotifyAsync(message, dueAt, includePreview, lifetime);
        return Task.CompletedTask;
    }

    public Task CancelAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelCore(messageId);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var messageId in _scheduled.Keys)
        {
            CancelCore(messageId);
        }
    }

    private async Task WaitAndNotifyAsync(
        StoredMessage message,
        DateTimeOffset dueAt,
        bool includePreview,
        CancellationTokenSource lifetime)
    {
        try
        {
            var delay = dueAt - DateTimeOffset.Now;
            while (delay > TimeSpan.FromDays(30))
            {
                await Task.Delay(TimeSpan.FromDays(30), lifetime.Token).ConfigureAwait(false);
                delay = dueAt - DateTimeOffset.Now;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            }

            if (!lifetime.IsCancellationRequested)
            {
                ReminderDue?.Invoke(this, new ReminderDueEventArgs(message, includePreview));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_scheduled.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(message.Id, lifetime)))
            {
                lifetime.Dispose();
            }
        }
    }

    private void CancelCore(Guid messageId)
    {
        if (_scheduled.TryRemove(messageId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
    }
}
