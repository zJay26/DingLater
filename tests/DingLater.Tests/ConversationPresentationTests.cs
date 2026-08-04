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
