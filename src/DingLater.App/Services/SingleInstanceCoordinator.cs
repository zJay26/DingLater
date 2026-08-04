using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace DingLater.App.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _serverTask;

    internal SingleInstanceCoordinator(string? instanceSuffix = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value?.Replace('-', '_') ?? Environment.UserName;
        var instanceName = string.IsNullOrWhiteSpace(instanceSuffix)
            ? "DingLater"
            : $"DingLater.{instanceSuffix}";
        _pipeName = $"{instanceName}.Activation.{sid}";
        _mutex = new Mutex(initiallyOwned: true, $"Local\\{instanceName}.{sid}", out var createdNew);
        IsPrimary = createdNew;
    }

    internal bool IsPrimary { get; }
    internal event EventHandler<string>? ActivationReceived;

    internal void StartListening()
    {
        if (!IsPrimary || _serverTask is not null)
        {
            return;
        }

        _serverTask = Task.Run(() => ServerLoopAsync(_lifetime.Token));
    }

    internal async Task<bool> ForwardAsync(string activation)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(1500);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(activation.Length <= 2048 ? activation : string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        _mutex.Dispose();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var activation = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (activation is not null)
                {
                    ActivationReceived?.Invoke(this, activation);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
