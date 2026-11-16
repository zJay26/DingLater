using DingLater.Core.Models;
using DingLater.Core.Storage;

namespace DingLater.App.Services;

/// <summary>
/// Fictional messages for the isolated package smoke test. They cover every inbox template: direct and group
/// conversations, mentions, DING, multi-line text, attachments, upcoming reminders and handled items.
/// </summary>
internal static class InterfacePreviewData
{
    internal static async Task SeedAsync(IMessageStore store, DateTimeOffset now)
    {
        var today = now.LocalDateTime.Date;
        var yesterday = new DateTimeOffset(today.AddDays(-1).AddHours(16).AddMinutes(20));
        var earlierThisWeek = new DateTimeOffset(today.AddDays(-3).AddHours(10).AddMinutes(5));
        var tomorrowMorning = new DateTimeOffset(today.AddDays(1).AddHours(9));
        var messages = new PreviewMessage[]
        {
            new("direct-linzhou", "林舟", "明早的评审改到 10 点，材料我已经放到共享盘了，你看完给我回个话。",
                MessageKind.Normal, ConversationScope.Direct, now.AddMinutes(-3)),
            new("研发协作群", "周远", "@我 日志里最后一个超时已经定位，晚点一起看下修复方案。",
                MessageKind.Mention, ConversationScope.Group, now.AddMinutes(-18)),
            new("研发协作群", "小林", "测试环境已经恢复，晚一点再看也可以。",
                MessageKind.Normal, ConversationScope.Group, now.AddMinutes(-26)),
            new("direct-xiaolin", "小林", "明天下午的评审可以延后半小时，我把新版流程图也整理好了。\n另外两个问题：\n1. 导出格式是否需要兼容旧版？\n2. 截止日期能否顺延到周五？",
                MessageKind.Normal, ConversationScope.Direct, now.AddMinutes(-52)),
            new("产品周会", "王磊", "周五上线前还需要再跑一轮回归，请确认负责人。",
                MessageKind.Ding, ConversationScope.Group, yesterday),
            new("direct-aqing", "阿青", "[文件] DingLater-界面走查.fig",
                MessageKind.Attachment, ConversationScope.Direct, earlierThisWeek),
            new("direct-liming", "李明", "更新后的报价单已发你，请确认。",
                MessageKind.Normal, ConversationScope.Direct, now.AddHours(-2), InboxState.Snoozed, now.AddMinutes(30)),
            new("设计评审群", "陈雨", "这版界面我整理了几点建议，晚点聊。",
                MessageKind.Normal, ConversationScope.Group, now.AddHours(-5), InboxState.Snoozed, tomorrowMorning),
            new("direct-zhaoning", "赵宁", "会议纪要已经同步到文档了。",
                MessageKind.Normal, ConversationScope.Direct, yesterday.AddHours(-3), InboxState.Handled),
            new("研发协作群", "小林", "部署脚本已合并，今晚可以发布。",
                MessageKind.Normal, ConversationScope.Group, earlierThisWeek.AddHours(2), InboxState.Handled)
        };

        for (var index = 0; index < messages.Length; index++)
        {
            var preview = messages[index];
            var result = await store.AddAsync(new CapturedMessage(
                CaptureSourceKind.Synthetic,
                preview.At,
                preview.Conversation,
                preview.Sender,
                preview.Body,
                preview.Kind,
                1,
                "interface-preview",
                SourceIdentity: $"preview:{index}",
                ConversationScope: preview.Scope), 7);
            if (preview.State != InboxState.Inbox)
            {
                await store.UpdateStateAsync(result.Message.Id, preview.State, preview.Due, now);
            }
        }
    }

    private sealed record PreviewMessage(
        string Conversation,
        string Sender,
        string Body,
        MessageKind Kind,
        ConversationScope Scope,
        DateTimeOffset At,
        InboxState State = InboxState.Inbox,
        DateTimeOffset? Due = null);
}
