namespace DingLater.Core.Models;

public sealed record CaptureCheckpoint(
    CaptureSourceKind Source,
    string AccountFingerprint,
    int Partition,
    long Position);
