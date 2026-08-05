using System.Globalization;
using DingLater.App.ViewModels;
using DingLater.Core.Models;

namespace DingLater.App.Services;

internal sealed record TrayIconPresentation(
    string BadgeText,
    string ToolTipText,
    string Title,
    string Body,
    string Footer);

internal static class TrayIconPresentationBuilder
{
    private const int ToolTipLimit = 127;
    private const int ConversationLimit = 42;
    private const int PreviewLimit = 72;

    internal static TrayIconPresentation Build(
        int pendingCount,
        StoredMessage? latestMessage,
        bool includePreview,
        bool paused)
    {
        pendingCount = Math.Max(0, pendingCount);
        var status = paused ? "已暂停" : "捕获中";
        if (pendingCount == 0)
        {
            return new TrayIconPresentation(
                string.Empty,
                $"DingLater · {status} · 暂无待处理消息",
                "暂无待处理消息",
                paused ? "捕获已暂停，可打开应用重新开始。" : "正在后台捕获新消息。",
                $"{status} · 单击打开");
        }

        var badge = pendingCount > 99
            ? "99+"
            : pendingCount.ToString(CultureInfo.InvariantCulture);
        var title = $"共 {pendingCount} 条待处理";
        if (!includePreview || latestMessage is null)
        {
            var hidden = "消息预览已关闭，可在设置中开启。";
            return new TrayIconPresentation(
                badge,
                Trim($"DingLater · {title}\n{hidden}", ToolTipLimit),
                title,
                hidden,
                $"{status} · 单击打开");
        }

        var conversation = Trim(Clean(ConversationPresentation.GetTitle([latestMessage])), ConversationLimit);
        var sender = Clean(latestMessage.Captured.Sender);
        var body = Clean(latestMessage.Captured.VisibleBody);
        var preview = string.IsNullOrWhiteSpace(sender)
            ? body
            : $"{sender}：{body}";
        preview = Trim(string.IsNullOrWhiteSpace(preview) ? "[非文字消息]" : preview, PreviewLimit);
        return new TrayIconPresentation(
            badge,
            Trim($"DingLater · {title}\n{conversation} · {preview}", ToolTipLimit),
            title,
            $"{conversation}\n{preview}",
            $"{status} · 单击打开");
    }

    private static string Clean(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
