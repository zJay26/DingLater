using DingLater.App.ViewModels;
using DingLater.Core.Models;

namespace DingLater.Tests;

[TestClass]
public sealed class ConversationPresentationTests
{
    [TestMethod]
    public void DirectNumericConversation_UsesSenderAsTitle()
    {
        var message = Stored("460140151", "毛俊翔", "1", ConversationScope.Direct);

        Assert.AreEqual("毛俊翔", ConversationPresentation.GetTitle([message]));
    }

    [TestMethod]
    public void UnknownLegacyConversation_UsesSenderForNumericOrSingleSender()
    {
        var numeric = Stored("460140151", "毛俊翔", "1", ConversationScope.Unknown);
        var named = Stored("内部会话标识", "毛俊翔", "收到", ConversationScope.Unknown);

        Assert.AreEqual("毛俊翔", ConversationPresentation.GetTitle([numeric]));
        Assert.AreEqual("毛俊翔", ConversationPresentation.GetTitle([named]));
    }

    [TestMethod]
    public void GroupConversation_UsesGroupTitle_AndSenderInSubtitle()
    {
        var now = DateTimeOffset.Parse("2026-08-04T16:00:00+08:00");
        var first = Stored("研发协作群", "周远", "日志已定位", ConversationScope.Group, now);
        var second = Stored("研发协作群", "小林", "明天评审", ConversationScope.Group, now.AddMinutes(-2));

        var thread = new ConversationThreadViewModel([second, first]);

        Assert.AreEqual("研发协作群", thread.Title);
        Assert.AreEqual("周远：日志已定位", thread.Subtitle);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, thread.Messages.Select(item => item.Id).ToArray());
        Assert.IsTrue(thread.Matches("小林"));
        Assert.IsTrue(thread.Matches("日志"));
        Assert.IsFalse(thread.Matches("不存在"));
    }

    [TestMethod]
    public void UnknownMultipleSenders_UsesConversationTitle()
    {
        var first = Stored("项目群", "甲", "A", ConversationScope.Unknown);
        var second = Stored("项目群", "乙", "B", ConversationScope.Unknown);

        Assert.AreEqual("项目群", ConversationPresentation.GetTitle([first, second]));
    }

    [TestMethod]
    public void SnoozedMessage_ExposesExplicitReminderTime()
    {
        var dueAt = DateTimeOffset.Parse("2026-08-06T09:30:00+08:00");
        var message = Stored("项目群", "甲", "正文", ConversationScope.Group) with
        {
            State = InboxState.Snoozed,
            SnoozedUntil = dueAt
        };

        var card = new MessageCardViewModel(message);

        Assert.IsTrue(card.HasSnoozedReminder);
        Assert.AreEqual($"提醒时间：{dueAt.LocalDateTime:yyyy年M月d日 HH:mm}", card.SnoozedUntilText);
    }

    [TestMethod]
    public void Thread_ExposesAvatarScopeCountAndSingleLinePreview()
    {
        var now = DateTimeOffset.Parse("2026-08-04T16:00:00+08:00");
        var direct = new ConversationThreadViewModel(
            [Stored("460140151", "  毛俊翔", "第一行\r\n第二行   第三行", ConversationScope.Direct, now)], now);
        var group = new ConversationThreadViewModel([
            Stored("研发协作群", "周远", "A", ConversationScope.Group, now),
            Stored("研发协作群", "小林", "B", ConversationScope.Group, now.AddMinutes(-1))], now);

        Assert.AreEqual("毛", direct.AvatarText);
        Assert.IsTrue(direct.IsDirect);
        Assert.AreEqual("私聊 · 1 条消息", direct.ScopeText);
        Assert.AreEqual("第一行 第二行 第三行", direct.Subtitle);
        Assert.IsFalse(direct.HasCount);
        Assert.AreEqual(string.Empty, direct.CountText);
        Assert.IsTrue(group.IsGroup);
        Assert.AreEqual("群聊 · 2 条消息", group.ScopeText);
        Assert.AreEqual("2", group.CountText);
    }

    [TestMethod]
    public void Thread_DingOutranksMention()
    {
        var mention = Stored("项目群", "甲", "@我", ConversationScope.Group) with
        {
            Captured = Stored("项目群", "甲", "@我", ConversationScope.Group).Captured with { Kind = MessageKind.Mention }
        };
        var ding = Stored("项目群", "乙", "DING", ConversationScope.Group) with
        {
            Captured = Stored("项目群", "乙", "DING", ConversationScope.Group).Captured with { Kind = MessageKind.Ding }
        };

        var mentionOnly = new ConversationThreadViewModel([mention]);
        var both = new ConversationThreadViewModel([mention, ding]);

        Assert.IsTrue(mentionOnly.HasMention);
        Assert.IsFalse(mentionOnly.HasDing);
        Assert.IsTrue(both.HasDing);
        Assert.IsFalse(both.HasMention);
    }

    [TestMethod]
    public void EmptyBody_ShowsPlaceholderButCopiesNothing()
    {
        var message = Stored("项目群", "甲", "  ", ConversationScope.Group);

        var card = new MessageCardViewModel(message);
        var thread = new ConversationThreadViewModel([message]);

        Assert.AreEqual("[非文字消息]", card.Body);
        Assert.IsFalse(card.CanCopy);
        Assert.AreEqual("甲：[非文字消息]", thread.Subtitle);
    }

    [TestMethod]
    public void ListItems_DescribeThemselvesForScreenReaders()
    {
        var now = DateTimeOffset.Parse("2026-08-04T16:00:00+08:00");
        var message = Stored("研发协作群", "周远", "日志已定位", ConversationScope.Group, now) with
        {
            Captured = Stored("研发协作群", "周远", "日志已定位", ConversationScope.Group, now).Captured with { Kind = MessageKind.Mention }
        };

        var thread = new ConversationThreadViewModel([message], now);
        var card = thread.Messages.Single();

        StringAssert.StartsWith(thread.ToString(), "研发协作群，群聊，1 条消息");
        StringAssert.Contains(thread.ToString(), "有人提到我");
        StringAssert.Contains(card.ToString(), "周远");
        StringAssert.Contains(card.ToString(), "提到我");
        StringAssert.Contains(card.ToString(), "日志已定位");
        Assert.IsFalse(thread.ToString().Contains(nameof(ConversationThreadViewModel), StringComparison.Ordinal));
        Assert.IsFalse(card.ToString().Contains(nameof(MessageCardViewModel), StringComparison.Ordinal));
    }

    private static StoredMessage Stored(
        string conversation,
        string sender,
        string body,
        ConversationScope scope,
        DateTimeOffset? capturedAt = null)
    {
        var time = capturedAt ?? DateTimeOffset.Parse("2026-08-04T16:00:00+08:00");
        return new StoredMessage(
            Guid.NewGuid(),
            new CapturedMessage(
                CaptureSourceKind.Synthetic,
                time,
                conversation,
                sender,
                body,
                MessageKind.Normal,
                1,
                "test",
                ConversationScope: scope),
            InboxState.Inbox,
            time.AddDays(7),
            null,
            time);
    }
}
