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
    private const int PreviewLimit = 180;

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
                "DingLater",
                $"{status} · 暂无待处理消息",
                "双击打开 DingLater");
        }

        var badge = pendingCount > 99
            ? "99+"
            : pendingCount.ToString(CultureInfo.InvariantCulture);
        var title = $"DingLater · {pendingCount} 条待处理";
        if (!includePreview || latestMessage is null)
        {
            var hidden = "消息预览已关闭，可在设置中开启。";
            return new TrayIconPresentation(
                badge,
                Trim($"{title}\n{hidden}", ToolTipLimit),
                title,
                hidden,
                $"{status} · 双击打开");
        }

        var conversation = ConversationPresentation.GetTitle([latestMessage]);
        var sender = Clean(latestMessage.Captured.Sender);
        var body = Clean(latestMessage.Captured.VisibleBody);
        var preview = string.IsNullOrWhiteSpace(sender)
            ? body
            : $"{sender}：{body}";
        preview = Trim(string.IsNullOrWhiteSpace(preview) ? "[非文字消息]" : preview, PreviewLimit);
        return new TrayIconPresentation(
            badge,
            Trim($"{title}\n{conversation} · {preview}", ToolTipLimit),
            title,
            $"{conversation}\n{preview}",
            $"{status} · 双击打开");
    }

    private static string Clean(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Trim(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
