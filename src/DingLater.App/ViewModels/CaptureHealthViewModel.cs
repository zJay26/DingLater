using DingLater.Core.Models;

namespace DingLater.App.ViewModels;

public sealed class CaptureHealthViewModel(CaptureHealth health)
{
    public string Name => health.SourceName;
    public string State => health.State switch
    {
        CaptureHealthState.Healthy => "可用",
        CaptureHealthState.Degraded => "受限",
        CaptureHealthState.PermissionDenied => "未授权",
        CaptureHealthState.Starting => "启动中",
        CaptureHealthState.Stopped => "已停止",
        CaptureHealthState.Faulted => "异常",
        _ => "不可用"
    };
    public string Detail => health.Detail;
    public string UpdatedAt => health.UpdatedAt.LocalDateTime.ToString("HH:mm:ss");
}
