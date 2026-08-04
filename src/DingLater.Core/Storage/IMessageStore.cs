using DingLater.Core.Models;

namespace DingLater.Core.Storage;

public sealed record StoreResult(StoredMessage Message, bool Inserted);
public sealed record CaptureBatchResult(IReadOnlyList<StoredMessage> InsertedMessages, int DuplicateCount);

public interface IMessageStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<StoreResult> AddAsync(CapturedMessage message, int retentionDays, CancellationToken cancellationToken = default);
    Task<CaptureBatchResult> AppendCaptureBatchAsync(CaptureBatch batch, int retentionDays, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<int, long>> GetCaptureCheckpointsAsync(
        CaptureSourceKind source,
        string accountFingerprint,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredMessage>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoredMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateStateAsync(Guid id, InboxState state, DateTimeOffset? snoozedUntil, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ReleaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<int> CountExpiringWhenRetentionChangesAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ApplyRetentionAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}
