using DingLater.Core.Models;

namespace DingLater.Core.Capture;

public delegate Task CaptureBatchHandler(
    object sender,
    CaptureBatch batch,
    CancellationToken cancellationToken);

public interface ICaptureSource : IAsyncDisposable
{
    string Name { get; }
    CaptureHealth Health { get; }
    event CaptureBatchHandler? BatchCaptured;
    event EventHandler<CaptureHealth>? HealthChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
