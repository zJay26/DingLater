using System.Drawing;
using DingLater.App.Services;
using DingLater.Core.Models;

namespace DingLater.Tests;

[TestClass]
public sealed class TrayInboxPresentationTests
{
    [TestMethod]
    public void GroupsPeopleAcrossConversations_UsesLatestPendingMessageTime()
    {
        var now = DateTimeOffset.Now;
        var old = Message("个人会话", " 张晓 ", "旧正文", now.AddMinutes(-10));
        var latest = Message("项目群", "张晓", "最新待处理\n第二行", now.AddMinutes(-5)) with
        {
            Captured = Message("项目群", "张晓", "最新待处理\n第二行", now.AddHours(-1)).Captured with { MessageAt = now.AddMinutes(-5) }
        };
        var other = Message("其他会话", "李明", "报价单", now.AddMinutes(-8));
        var handled = Message("项目群", "张晓", "已经处理的较新正文", now) with { State = InboxState.Handled };
        var snoozed = Message("项目群", "张晓", "稍后提醒的较新正文", now) with { State = InboxState.Snoozed };

        var result = TrayInboxPresentationBuilder.Build([other, old, handled, latest, snoozed], true);

        Assert.AreEqual(3, result.PendingCount);
        Assert.AreEqual(2, result.Contacts.Count);
        Assert.AreEqual("张晓", result.Contacts[0].Name);
        Assert.AreEqual(2, result.Contacts[0].Count);
        Assert.AreEqual(latest.Id, result.Contacts[0].LatestMessageId);
        Assert.AreEqual("最新待处理 第二行", result.Contacts[0].Preview);
        Assert.AreEqual("张", result.Contacts[0].AvatarText);
        Assert.AreEqual("李明", result.Contacts[1].Name);
    }

    [TestMethod]
    public void PreviewDisabled_ExposesNoContactRowsOrAccessibleMessageText()
    {
        var result = TrayInboxPresentationBuilder.Build([Message("私密群", "私密联系人", "私密正文", DateTimeOffset.Now)], false);
        Assert.AreEqual(1, result.PendingCount);
        Assert.AreEqual(0, result.Contacts.Count);
        Assert.IsFalse(result.IncludePreview);
    }

    [TestMethod]
    public void UnknownPeopleInDifferentConversations_AreNotCombined()
    {
        var result = TrayInboxPresentationBuilder.Build([
            Message("甲会话", "", "", DateTimeOffset.Now),
            Message("乙会话", "未知发送者", "附件", DateTimeOffset.Now)], true);
        Assert.AreEqual(2, result.Contacts.Count);
        Assert.IsTrue(result.Contacts.All(contact => contact.Name == "未知发送者" && contact.Count == 1));
        Assert.IsTrue(result.Contacts.Any(contact => contact.Preview == "[非文字消息]"));
    }

    [TestMethod]
    public void EmojiAvatar_PreservesCompleteGrapheme()
    {
        var result = TrayInboxPresentationBuilder.Build([Message("会话", "👩‍💻开发者", "内容", DateTimeOffset.Now)], true);
        Assert.AreEqual("👩‍💻", result.Contacts.Single().AvatarText);
    }

    [DataTestMethod]
    [DataRow(0, 0, 1920, 1040, 1800, 1050, 392, 490)]
    [DataRow(-1920, 0, 1920, 1040, -150, 1050, 588, 735)]
    [DataRow(1920, 0, 2560, 1360, 4320, 1370, 784, 980)]
    [DataRow(0, 48, 1280, 752, 1200, 0, 392, 490)]
    public void Placement_StaysInMonitorWorkArea_IncludingScalingAndTopTaskbar(
        int x, int y, int width, int height, int iconX, int iconY, int popupWidth, int popupHeight)
    {
        var work = new Rectangle(x, y, width, height);
        var anchor = new Rectangle(iconX, iconY, 24, 24);
        var result = TrayHoverPlacement.Calculate(work, anchor, new Size(popupWidth, popupHeight), 8);
        Assert.IsTrue(work.Contains(result));
        Assert.IsFalse(result.IntersectsWith(anchor));
    }

    private static StoredMessage Message(string conversation, string sender, string body, DateTimeOffset time) =>
        new(Guid.NewGuid(), new CapturedMessage(CaptureSourceKind.Synthetic, time, conversation, sender, body,
            MessageKind.Normal, 1, "test"), InboxState.Inbox, time.AddDays(7), null, time);
}
