namespace DingLater.Core.Models;

public sealed record CaptureBatch(
    IReadOnlyList<CapturedMessage> Messages,
    IReadOnlyList<CaptureCheckpoint> Checkpoints)
{
    public static CaptureBatch Single(CapturedMessage message) => new([message], []);
}
