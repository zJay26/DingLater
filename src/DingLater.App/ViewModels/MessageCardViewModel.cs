using DingLater.Core.Models;

namespace DingLater.App.ViewModels;

public sealed class MessageCardViewModel(StoredMessage model)
{
    public StoredMessage Model { get; } = model;
    public Guid Id => Model.Id;
    public string Conversation => Model.Captured.Conversation;
    public string Sender => string.IsNullOrWhiteSpace(Model.Captured.Sender) ? "未知发送者" : Model.Captured.Sender;
    public string Body => Model.Captured.VisibleBody;
    public InboxState State => Model.State;
    public DateTimeOffset ExpiresAt => Model.ExpiresAt;
    public DateTimeOffset? SnoozedUntil => Model.SnoozedUntil;
    public string CapturedAtText => (Model.Captured.MessageAt ?? Model.Captured.CapturedAt).LocalDateTime.ToString("M月d日 HH:mm");
    public string ExactCapturedAt => (Model.Captured.MessageAt ?? Model.Captured.CapturedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string ExpiryText => $"{Model.ExpiresAt.LocalDateTime:M月d日 HH:mm} 自动清理";
    public bool HasSnoozedReminder => Model.SnoozedUntil is not null;
    public string SnoozedUntilText => Model.SnoozedUntil is null
        ? string.Empty
        : $"提醒时间：{Model.SnoozedUntil.Value.LocalDateTime:yyyy年M月d日 HH:mm}";
    public string KindLabel => Model.Captured.Kind switch
    {
        MessageKind.Mention => "提到我",
        MessageKind.Ding => "DING",
        MessageKind.Attachment => "附件",
        MessageKind.Unknown => "未分类",
        _ => "普通消息"
    };

    public string SourceLabel => Model.Captured.Source switch
    {
        CaptureSourceKind.WindowsNotification => "Windows 通知",
        CaptureSourceKind.PopupAutomation => "桌面弹窗",
        CaptureSourceKind.PopupOcr => "弹窗 OCR",
        CaptureSourceKind.DingTalkDatabase => "钉钉本地数据库",
        _ => "合成数据"
    };

    public string ConfidenceText => $"识别 {Model.Captured.Confidence:P0}";
    public bool CanSnooze => State != InboxState.Handled;
    public bool CanHandle => State != InboxState.Handled;
    public bool CanRestore => State == InboxState.Handled;
}
