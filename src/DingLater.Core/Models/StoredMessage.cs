namespace DingLater.Core.Models;

public sealed record StoredMessage(
    Guid Id,
    CapturedMessage Captured,
    InboxState State,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? SnoozedUntil,
    DateTimeOffset UpdatedAt);
