using System.Globalization;
using DingLater.App.ViewModels;
using DingLater.Core.Models;

namespace DingLater.App.Services;

internal sealed record TrayInboxPresentation(
    int PendingCount,
    StoredMessage? LatestMessage,
    IReadOnlyList<TrayContactViewModel> Contacts,
    bool IncludePreview);

internal static class TrayInboxPresentationBuilder
{
    internal static TrayInboxPresentation Build(IEnumerable<StoredMessage> messages, bool includePreview)
    {
        var pending = messages.Where(message => message.State == InboxState.Inbox)
            .OrderByDescending(ConversationPresentation.GetMessageTime)
            .ThenBy(message => message.Id)
            .ToArray();
        if (!includePreview)
        {
            return new TrayInboxPresentation(pending.Length, pending.FirstOrDefault(), [], false);
        }

        var today = DateTime.Today;
        var contacts = pending.GroupBy(GetPersonKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.First();
                var name = Clean(latest.Captured.Sender);
                if (name.Length == 0) name = "未知发送者";
                var body = Clean(latest.Captured.VisibleBody);
                if (body.Length == 0) body = "[非文字消息]";
                var time = ConversationPresentation.GetMessageTime(latest).LocalDateTime;
                return new TrayContactViewModel(
                    group.Key,
                    name,
                    StringInfo.GetNextTextElement(name),
                    group.Count(),
                    body,
                    time.ToString(time.Date == today ? "HH:mm" : "M/d HH:mm", CultureInfo.CurrentCulture),
                    latest.Id);
            }).ToArray();
        return new TrayInboxPresentation(pending.Length, pending.FirstOrDefault(), contacts, true);
    }

    private static string GetPersonKey(StoredMessage message)
    {
        var sender = Clean(message.Captured.Sender);
        return sender.Length > 0 && !string.Equals(sender, "未知发送者", StringComparison.Ordinal)
            ? $"sender:{sender}"
            : $"unknown:{ConversationPresentation.GetKey(message)}";
    }

    private static string Clean(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
}
