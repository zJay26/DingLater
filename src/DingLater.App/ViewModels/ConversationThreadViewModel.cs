using System.Collections.ObjectModel;
using System.Globalization;
using DingLater.Core.Models;

namespace DingLater.App.ViewModels;

public sealed class ConversationThreadViewModel
{
    private readonly IReadOnlyList<StoredMessage> _models;

    public ConversationThreadViewModel(IEnumerable<StoredMessage> messages, DateTimeOffset? now = null)
    {
        _models = messages.OrderByDescending(ConversationPresentation.GetMessageTime).ThenBy(message => message.Id).ToList();
        if (_models.Count == 0)
        {
            throw new ArgumentException("A conversation requires at least one message.", nameof(messages));
        }

        var clock = now ?? DateTimeOffset.Now;
        Key = ConversationPresentation.GetKey(_models[0]);
        Title = ConversationPresentation.GetTitle(_models);
        IsGroup = ConversationPresentation.IsGroup(_models);
        AvatarText = PresentationText.Initial(Title);
        LatestAt = ConversationPresentation.GetMessageTime(_models[0]);
        LatestAtText = DisplayTimeFormatter.Compact(LatestAt, clock);
        NextReminderAt = _models.Min(message => message.SnoozedUntil) ?? DateTimeOffset.MaxValue;
        var ordered = _models[0].State == InboxState.Snoozed
            ? _models.OrderBy(message => message.SnoozedUntil).ThenByDescending(ConversationPresentation.GetMessageTime)
            : _models.AsEnumerable();
        Messages = new ObservableCollection<MessageCardViewModel>(ordered.Select(message => new MessageCardViewModel(message, clock)));
        ReminderText = NextReminderAt == DateTimeOffset.MaxValue
            ? string.Empty
            : $"下次提醒：{DisplayTimeFormatter.Upcoming(NextReminderAt, clock)}";
        var latest = Messages[0];
        var hasMultipleSenders = _models.Select(message => message.Captured.Sender)
            .Where(sender => !string.IsNullOrWhiteSpace(sender))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Skip(1)
            .Any();
        Subtitle = IsGroup || hasMultipleSenders
            ? $"{latest.Sender}：{PresentationText.Preview(latest.Body)}"
            : PresentationText.Preview(latest.Body);
        MessageCount = _models.Count;
        CountText = MessageCount == 1 ? string.Empty : MessageCount.ToString(CultureInfo.InvariantCulture);
        HasDing = _models.Any(message => message.Captured.Kind == MessageKind.Ding);
        HasMention = !HasDing && _models.Any(message => message.Captured.Kind == MessageKind.Mention);
        ScopeText = $"{(IsGroup ? "群聊" : "私聊")} · {MessageCount} 条消息";
    }

    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string CountText { get; }
    public int MessageCount { get; }
    public bool HasCount => MessageCount > 1;
    public bool IsGroup { get; }
    public bool IsDirect => !IsGroup;
    public string AvatarText { get; }
    public string ScopeText { get; }

    /// <summary>At least one message is a DING; shown with higher urgency than a mention.</summary>
    public bool HasDing { get; }

    /// <summary>At least one message mentions the user and none is a DING.</summary>
    public bool HasMention { get; }
    public DateTimeOffset LatestAt { get; }
    public string LatestAtText { get; }
    public DateTimeOffset NextReminderAt { get; }
    public string ReminderText { get; }
    public bool HasReminder => NextReminderAt != DateTimeOffset.MaxValue;
    public bool HasTags => HasDing || HasMention || HasReminder;
    public ObservableCollection<MessageCardViewModel> Messages { get; }

    internal bool HasSameMessages(IEnumerable<StoredMessage> messages) =>
        _models.SequenceEqual(messages.OrderByDescending(ConversationPresentation.GetMessageTime).ThenBy(message => message.Id));

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || _models.Any(message =>
                   message.Captured.Conversation.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                   || message.Captured.Sender.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                   || message.Captured.VisibleBody.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Screen readers announce list items through ToString, so describe the row in words.</summary>
    public override string ToString()
    {
        var parts = new List<string>(6) { Title, IsGroup ? "群聊" : "私聊", $"{MessageCount} 条消息", LatestAtText };
        if (HasDing)
        {
            parts.Add("含 DING 消息");
        }
        else if (HasMention)
        {
            parts.Add("有人提到我");
        }

        if (HasReminder)
        {
            parts.Add(ReminderText);
        }

        parts.Add(Subtitle);
        return string.Join("，", parts);
    }
}

internal static class ConversationPresentation
{
    internal static string GetKey(StoredMessage message)
    {
        var conversation = message.Captured.Conversation.Trim();
        return conversation.Length > 0
            ? conversation
            : $"sender:{message.Captured.Sender.Trim()}";
    }

    internal static string GetTitle(IEnumerable<StoredMessage> messages)
    {
        var list = messages.OrderByDescending(GetMessageTime).ToList();
        if (list.Count == 0)
        {
            return "未知会话";
        }

        var latest = list[0].Captured;
        var senders = list.Select(message => message.Captured.Sender.Trim())
            .Where(sender => sender.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (IsGroup(list))
        {
            return NonEmpty(latest.Conversation, "未命名群聊");
        }

        if (list.Any(message => message.Captured.ConversationScope == ConversationScope.Direct)
            || IsNumeric(latest.Conversation)
            || senders.Count <= 1)
        {
            return senders.FirstOrDefault() ?? NonEmpty(latest.Conversation, "未知联系人");
        }

        return NonEmpty(latest.Conversation, senders.FirstOrDefault() ?? "未知会话");
    }

    internal static bool IsGroup(IEnumerable<StoredMessage> messages)
    {
        var list = messages.ToList();
        if (list.Any(message => message.Captured.ConversationScope == ConversationScope.Group))
        {
            return true;
        }

        if (list.Any(message => message.Captured.ConversationScope == ConversationScope.Direct))
        {
            return false;
        }

        return list.Select(message => message.Captured.Sender.Trim())
            .Where(sender => sender.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Skip(1)
            .Any();
    }

    internal static DateTimeOffset GetMessageTime(StoredMessage message) =>
        message.Captured.MessageAt ?? message.Captured.CapturedAt;

    private static bool IsNumeric(string value) =>
        value.Trim().Length > 0 && value.Trim().All(char.IsDigit);

    private static string NonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
