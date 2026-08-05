using DingLater.App.Services;
using DingLater.Core.Models;

namespace DingLater.Tests;

[TestClass]
public sealed class TrayIconPresentationTests
{
    [TestMethod]
    public void EmptyInbox_HasNoBadgeAndShowsCaptureState()
    {
        var presentation = TrayIconPresentationBuilder.Build(0, null, includePreview: true, paused: false);

        Assert.AreEqual(string.Empty, presentation.BadgeText);
        StringAssert.Contains(presentation.ToolTipText, "暂无待处理消息");
        StringAssert.Contains(presentation.Body, "捕获中");
    }

    [TestMethod]
    public void PendingInbox_ShowsExactCountAndLatestPreview()
    {
        var message = Stored("研发协作群", "周远", "日志已定位\n请查看");

        var presentation = TrayIconPresentationBuilder.Build(12, message, includePreview: true, paused: false);

        Assert.AreEqual("12", presentation.BadgeText);
        StringAssert.Contains(presentation.Title, "12 条待处理");
        StringAssert.Contains(presentation.Body, "研发协作群");
        StringAssert.Contains(presentation.Body, "周远：日志已定位 请查看");
    }

    [TestMethod]
    public void PreviewDisabled_DoesNotExposeMessageContent()
    {
        var message = Stored("机密项目", "联系人", "不可出现在托盘里");

        var presentation = TrayIconPresentationBuilder.Build(3, message, includePreview: false, paused: true);

        Assert.AreEqual("3", presentation.BadgeText);
        StringAssert.Contains(presentation.Body, "消息预览已关闭");
        Assert.IsFalse(presentation.ToolTipText.Contains("机密项目", StringComparison.Ordinal));
        Assert.IsFalse(presentation.ToolTipText.Contains("不可出现在托盘里", StringComparison.Ordinal));
        StringAssert.Contains(presentation.Footer, "已暂停");
    }

    [TestMethod]
    public void LargeCount_CapsOnlyTheTinyBadge()
    {
        var presentation = TrayIconPresentationBuilder.Build(137, null, includePreview: false, paused: false);

        Assert.AreEqual("99+", presentation.BadgeText);
        StringAssert.Contains(presentation.Title, "137 条待处理");
    }

    private static StoredMessage Stored(string conversation, string sender, string body)
    {
        var now = DateTimeOffset.Parse("2026-08-05T12:00:00+08:00");
        return new StoredMessage(
            Guid.NewGuid(),
            new CapturedMessage(
                CaptureSourceKind.Synthetic,
                now,
                conversation,
                sender,
                body,
                MessageKind.Normal,
                1,
                "test",
                ConversationScope: ConversationScope.Group),
            InboxState.Inbox,
            now.AddDays(7),
            null,
            now);
    }
}
