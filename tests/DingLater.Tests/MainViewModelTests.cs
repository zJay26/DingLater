using DingLater.App.Services;
using DingLater.App.ViewModels;
using DingLater.Core.Models;
using DingLater.Core.Services;
using DingLater.Core.Storage;

namespace DingLater.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public async Task Refresh_GroupsSortsSearchesAndPreservesSelection()
    {
        var now = DateTimeOffset.Parse("2026-08-04T16:00:00+08:00");
        var store = new MutableStore();
        var direct = Stored("460140151", "毛俊翔", "1", ConversationScope.Direct, now);
        var groupOld = Stored("研发协作群", "小林", "评审已更新", ConversationScope.Group, now.AddMinutes(-5));
        var groupNew = Stored("研发协作群", "周远", "日志已定位", ConversationScope.Group, now.AddMinutes(2));
        store.Messages.AddRange([direct, groupOld, groupNew]);
        await using var inbox = new InboxService(store, new FakeReminders(), []);
        await inbox.InitializeAsync();
        using var viewModel = new MainViewModel(inbox, new StartupService());

        await viewModel.RefreshAsync();

        Assert.HasCount(2, viewModel.Conversations);
        Assert.AreEqual("研发协作群", viewModel.Conversations[0].Title);
        Assert.AreEqual("毛俊翔", viewModel.Conversations[1].Title);
        Assert.AreEqual(groupNew.Id, viewModel.LatestInboxMessage?.Id);
        viewModel.SelectedConversation = viewModel.Conversations[1];
        viewModel.SelectedMessage = viewModel.SelectedConversation.Messages[0];
        var selectedId = viewModel.SelectedMessage.Id;

        store.Messages.Add(Stored("其他会话", "阿青", "更新", ConversationScope.Direct, now.AddMinutes(3)));
        await viewModel.RefreshAsync();
        Assert.AreEqual("毛俊翔", viewModel.SelectedConversation?.Title);
        Assert.AreEqual(selectedId, viewModel.SelectedMessage?.Id);

        viewModel.SearchText = "日志";
        Assert.HasCount(1, viewModel.Conversations);
        Assert.AreEqual("研发协作群", viewModel.Conversations[0].Title);
        viewModel.SearchText = "毛俊翔";
        Assert.HasCount(1, viewModel.Conversations);
        Assert.AreEqual("毛俊翔", viewModel.Conversations[0].Title);
    }

    [TestMethod]
    public async Task Activation_SelectsMessageAndSection()
    {
        var store = new MutableStore();
        var snoozed = Stored(
            "研发协作群",
            "周远",
            "日志已定位",
            ConversationScope.Group,
            DateTimeOffset.Parse("2026-08-04T16:00:00+08:00")) with
        {
            State = InboxState.Snoozed,
            SnoozedUntil = DateTimeOffset.Parse("2026-08-04T17:00:00+08:00")
        };
        store.Messages.Add(snoozed);
        await using var inbox = new InboxService(store, new FakeReminders(), []);
        await inbox.InitializeAsync();
        using var viewModel = new MainViewModel(inbox, new StartupService());
        await viewModel.RefreshAsync();

        viewModel.SelectFromActivation(snoozed.Id);

        Assert.AreEqual(InboxSection.Snoozed, viewModel.Section);
        Assert.AreEqual(snoozed.Id, viewModel.SelectedMessage?.Id);
    }

    [TestMethod]
    public async Task FailedImmediateSave_LeavesSettingsUnchanged()
    {
        var store = new MutableStore();
        await using var inbox = new InboxService(store, new FakeReminders(), []);
        await inbox.InitializeAsync();
        using var viewModel = new MainViewModel(inbox, new StartupService());
        store.FailSettingsSave = true;

        var saved = await viewModel.SaveSettingAsync(settings => settings with
        {
            UiFontScale = UiFontScale.ExtraLarge,
            QuickSnoozeMinutes = 74
        });

        Assert.IsFalse(saved);
        Assert.AreEqual(UiFontScale.Standard, viewModel.Settings.UiFontScale);
        Assert.AreEqual(30, viewModel.Settings.QuickSnoozeMinutes);
    }

    [TestMethod]
    public async Task BulkActions_HandlePendingAndSnoozedThenDeleteHandled()
    {
        var now = DateTimeOffset.Parse("2026-08-04T16:00:00+08:00");
        var store = new MutableStore();
        store.Messages.Add(Stored("待处理", "甲", "正文", ConversationScope.Direct, now));
        store.Messages.Add(Stored("稍后", "乙", "正文", ConversationScope.Direct, now) with
        {
            State = InboxState.Snoozed,
            SnoozedUntil = now.AddHours(1)
        });
        store.Messages.Add(Stored("已处理", "丙", "正文", ConversationScope.Direct, now) with
        {
            State = InboxState.Handled
        });
        await using var inbox = new InboxService(store, new FakeReminders(), []);
        await inbox.InitializeAsync();
        using var viewModel = new MainViewModel(inbox, new StartupService());
        await viewModel.RefreshAsync();

        Assert.AreEqual(1, await viewModel.MarkAllHandledAsync());
        Assert.AreEqual(0, viewModel.InboxCount);
        Assert.AreEqual(2, viewModel.HandledCount);
        Assert.AreEqual(1, viewModel.SnoozedCount);

        Assert.AreEqual(1, await viewModel.MarkAllHandledAsync(InboxSection.Snoozed));
        Assert.AreEqual(0, viewModel.SnoozedCount);
        Assert.AreEqual(3, viewModel.HandledCount);

        Assert.AreEqual(3, await viewModel.DeleteAllHandledAsync());
        Assert.AreEqual(0, viewModel.HandledCount);
    }

    private static StoredMessage Stored(
        string conversation,
        string sender,
        string body,
        ConversationScope scope,
        DateTimeOffset capturedAt)
    {
        return new StoredMessage(
            Guid.NewGuid(),
            new CapturedMessage(
                CaptureSourceKind.Synthetic,
                capturedAt,
                conversation,
                sender,
                body,
                MessageKind.Normal,
                1,
                "test",
                ConversationScope: scope),
            InboxState.Inbox,
            capturedAt.AddDays(7),
            null,
            capturedAt);
    }

    private sealed class FakeReminders : IReminderScheduler
    {
        public Task ScheduleAsync(StoredMessage message, DateTimeOffset dueAt, bool includePreview, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelAsync(Guid messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MutableStore : IMessageStore
    {
        public List<StoredMessage> Messages { get; } = [];
        public AppSettings Settings { get; private set; } = new();
        public bool FailSettingsSave { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StoreResult> AddAsync(CapturedMessage message, int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaptureBatchResult> AppendCaptureBatchAsync(CaptureBatch batch, int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, long>> GetCaptureCheckpointsAsync(CaptureSourceKind source, string accountFingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(Messages.ToList());

        public Task<StoredMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Messages.FirstOrDefault(message => message.Id == id));

        public Task UpdateStateAsync(Guid id, InboxState state, DateTimeOffset? snoozedUntil, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            var index = Messages.FindIndex(message => message.Id == id);
            Messages[index] = Messages[index] with { State = state, SnoozedUntil = snoozedUntil, UpdatedAt = updatedAt };
            return Task.CompletedTask;
        }

        public Task<int> UpdateStateByStateAsync(InboxState currentState, InboxState state, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            var updated = 0;
            for (var index = 0; index < Messages.Count; index++)
            {
                if (Messages[index].State != currentState)
                {
                    continue;
                }

                Messages[index] = Messages[index] with { State = state, SnoozedUntil = null, UpdatedAt = updatedAt };
                updated++;
            }

            return Task.FromResult(updated);
        }

        public Task<IReadOnlyList<Guid>> ReleaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<Guid>> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<int> CountExpiringWhenRetentionChangesAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<Guid>> ApplyRetentionAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailSettingsSave)
            {
                throw new IOException("simulated");
            }

            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Messages.RemoveAll(message => message.Id == id) == 1);

        public Task<int> DeleteByStateAsync(InboxState state, CancellationToken cancellationToken = default) =>
            Task.FromResult(Messages.RemoveAll(message => message.State == state));

        public Task DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            Messages.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
