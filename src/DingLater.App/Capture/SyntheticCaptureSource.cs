#if DEBUG
using DingLater.Core.Capture;
using DingLater.Core.Models;

namespace DingLater.App.Capture;

internal sealed class SyntheticCaptureSource : ICaptureSource
{
    public string Name => "界面预览数据";
    public CaptureHealth Health { get; private set; } = new("界面预览数据", CaptureHealthState.Stopped, "仅 Debug 构建可用", DateTimeOffset.Now);
    public event CaptureBatchHandler? BatchCaptured;
    public event EventHandler<CaptureHealth>? HealthChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Health = new CaptureHealth(Name, CaptureHealthState.Healthy, "使用合成内容检查界面，不读取钉钉", DateTimeOffset.Now);
        HealthChanged?.Invoke(this, Health);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Health = new CaptureHealth(Name, CaptureHealthState.Stopped, "界面预览已停止", DateTimeOffset.Now);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var messages = new[]
        {
            new CapturedMessage(CaptureSourceKind.Synthetic, now.AddMinutes(-2), "460140151", "林舟", "1", MessageKind.Normal, 1, "Synthetic", ConversationScope: ConversationScope.Direct),
            new CapturedMessage(CaptureSourceKind.Synthetic, now.AddMinutes(-3), "product-demo-direct", "小林", "明天下午的评审可以延后半小时，我把新版流程图也整理好了。", MessageKind.Normal, 0.96, "Synthetic", ConversationScope: ConversationScope.Direct),
            new CapturedMessage(CaptureSourceKind.Synthetic, now.AddMinutes(-18), "研发协作群", "周远", "@我 日志里最后一个超时已经定位，不需要现在回复。", MessageKind.Mention, 0.91, "Synthetic", ConversationScope: ConversationScope.Group),
            new CapturedMessage(CaptureSourceKind.Synthetic, now.AddMinutes(-24), "研发协作群", "小林", "测试环境已经恢复，晚一点再看也可以。", MessageKind.Normal, 0.98, "Synthetic", ConversationScope: ConversationScope.Group),
            new CapturedMessage(CaptureSourceKind.Synthetic, now.AddHours(-1), "design-demo-direct", "阿青", "[文件] DingLater-empty-state.fig", MessageKind.Attachment, 0.88, "Synthetic", ConversationScope: ConversationScope.Direct)
        };
        if (BatchCaptured is { } handler)
        {
            await handler(this, new CaptureBatch(messages, []), cancellationToken);
        }
    }
}
#endif
