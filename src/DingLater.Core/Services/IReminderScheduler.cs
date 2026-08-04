using DingLater.Core.Models;

namespace DingLater.Core.Services;

public interface IReminderScheduler
{
    Task ScheduleAsync(StoredMessage message, DateTimeOffset dueAt, bool includePreview, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid messageId, CancellationToken cancellationToken = default);
}
