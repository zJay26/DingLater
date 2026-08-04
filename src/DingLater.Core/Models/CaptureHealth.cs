namespace DingLater.Core.Models;

public enum CaptureHealthState
{
    Stopped,
    Starting,
    Healthy,
    Degraded,
    PermissionDenied,
    Unavailable,
    Faulted
}

public sealed record CaptureHealth(
    string SourceName,
    CaptureHealthState State,
    string Detail,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastCaptureAt = null,
    bool? PermissionGranted = null,
    bool? DingTalkDetected = null,
    int EmptyBodyCount = 0,
    DateTimeOffset? LastObservationAt = null,
    int ObservationCount = 0,
    int CandidateCount = 0,
    bool? DatabaseFound = null,
    bool? KeyDerived = null,
    bool? WalValid = null,
    bool? SchemaCompatible = null,
    string ErrorCode = "");
