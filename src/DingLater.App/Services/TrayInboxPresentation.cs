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
    internal static TrayInboxPresentation Build(
        IEnumerable<StoredMessage> messages,
        bool includePreview,
        DateTimeOffset? now = null)
    {
        var pending = messages.Where(message => message.State == InboxState.Inbox)
            .OrderByDescending(ConversationPresentation.GetMessageTime)
            .ThenBy(message => message.Id)
            .ToArray();
        if (!includePreview)
        {
            return new TrayInboxPresentation(pending.Length, pending.FirstOrDefault(), [], false);
        }

        var clock = now ?? DateTimeOffset.Now;
        var contacts = pending.GroupBy(GetPersonKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.First();
                var name = PresentationText.SingleLine(latest.Captured.Sender);
                if (name.Length == 0) name = "未知发送者";
                return new TrayContactViewModel(
                    group.Key,
                    name,
                    PresentationText.Initial(name),
                    group.Count(),
                    PresentationText.Preview(latest.Captured.VisibleBody),
                    DisplayTimeFormatter.Compact(ConversationPresentation.GetMessageTime(latest), clock),
                    latest.Id);
            }).ToArray();
        return new TrayInboxPresentation(pending.Length, pending.FirstOrDefault(), contacts, true);
    }

    private static string GetPersonKey(StoredMessage message)
    {
        var sender = PresentationText.SingleLine(message.Captured.Sender);
        return sender.Length > 0 && !string.Equals(sender, "未知发送者", StringComparison.Ordinal)
            ? $"sender:{sender}"
            : $"unknown:{ConversationPresentation.GetKey(message)}";
    }
}
