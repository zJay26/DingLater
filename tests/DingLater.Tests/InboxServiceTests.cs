using System.Security.Cryptography;
using DingLater.Core.Capture;
using DingLater.Core.Models;
using DingLater.Core.Security;
using DingLater.Core.Services;
using DingLater.Core.Storage;

namespace DingLater.Tests;

[TestClass]
public sealed class InboxServiceTests
{
    private string _directory = null!;

    [TestInitialize]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "DingLater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Snooze_RejectsTimeAfterExpiry_AndSchedulesValidTime()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00+08:00");
        var clock = new ManualTimeProvider(now);
        var reminders = new FakeReminders();
        var source = new FakeCaptureSource();
        await using var service = CreateService(reminders, source, clock);
        await service.InitializeAsync();
        await service.StartCaptureAsync();
        await source.EmitAsync(new CapturedMessage(CaptureSourceKind.Synthetic, now, "群", "人", "正文", MessageKind.Normal, 1, "test"));
        await WaitUntilAsync(async () => (await service.ListAsync()).Count == 1);
        var message = (await service.ListAsync()).Single();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.SnoozeAsync(message.Id, message.ExpiresAt.AddMinutes(1)));
        var due = now.AddHours(2);
        await service.SnoozeAsync(message.Id, due);

        Assert.AreEqual(due, reminders.Scheduled[message.Id]);
        Assert.AreEqual(InboxState.Snoozed, (await service.GetAsync(message.Id))?.State);
    }

    [TestMethod]
    public async Task MarkHandled_CancelsReminder_WithoutCallingCaptureSource()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00+08:00");
        var clock = new ManualTimeProvider(now);
        var reminders = new FakeReminders();
        var source = new FakeCaptureSource();
        await using var service = CreateService(reminders, source, clock);
        await service.InitializeAsync();
        var storeMessage = await AddDirectAsync(service, source, now);

        await service.MarkHandledAsync(storeMessage.Id);

        CollectionAssert.Contains(reminders.Cancelled, storeMessage.Id);
        Assert.AreEqual(InboxState.Handled, (await service.GetAsync(storeMessage.Id))?.State);
        Assert.AreEqual(1, source.StartCount);
        Assert.AreEqual(0, source.StopCount);
    }

    [TestMethod]
    public async Task Delete_RemovesOnlySelectedMessage_CancelsReminder_AndRaisesChange()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00+08:00");
        var clock = new ManualTimeProvider(now);
        var reminders = new FakeReminders();
        var source = new FakeCaptureSource();
        await using var service = CreateService(reminders, source, clock);
        await service.InitializeAsync();
        var first = await AddDirectAsync(service, source, now);
        await source.EmitAsync(new CapturedMessage(
            CaptureSourceKind.Synthetic,
            now.AddMinutes(1),
            "另一个会话",
            "另一个人",
            "保留正文",
            MessageKind.Normal,
            1,
            "test",
            SourceIdentity: "delete-test-2"));
        await WaitUntilAsync(async () => (await service.ListAsync()).Count == 2);
        var changes = 0;
        service.InboxChanged += (_, _) => changes++;

        await service.DeleteAsync(first.Id);

        CollectionAssert.Contains(reminders.Cancelled, first.Id);
        Assert.IsNull(await service.GetAsync(first.Id));
        Assert.HasCount(1, await service.ListAsync());
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task Maintenance_ReleasesDueMessage_AndRaisesInboxChanged()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00+08:00");
        var clock = new ManualTimeProvider(now);
        var reminders = new FakeReminders();
        var source = new FakeCaptureSource();
        await using var service = CreateService(reminders, source, clock);
        await service.InitializeAsync();
        var message = await AddDirectAsync(service, source, now);
        await service.SnoozeAsync(message.Id, now.AddMinutes(10));
        var changes = 0;
        service.InboxChanged += (_, _) => changes++;

        clock.Value = now.AddMinutes(11);
        await service.RunMaintenanceAsync();

        Assert.AreEqual(InboxState.Inbox, (await service.GetAsync(message.Id))?.State);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task PreviewPreference_ReschedulesExistingSnoozes()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00+08:00");
        var clock = new ManualTimeProvider(now);
        var reminders = new FakeReminders();
        var source = new FakeCaptureSource();
        await using var service = CreateService(reminders, source, clock);
        await service.InitializeAsync();
        var message = await AddDirectAsync(service, source, now);
        await service.SnoozeAsync(message.Id, now.AddHours(1));
        Assert.IsFalse(reminders.Preview[message.Id]);

        await service.SaveSettingsAsync(service.Settings with { ShowReminderPreview = true }, applyRetention: false);

        Assert.IsTrue(reminders.Preview[message.Id]);
        Assert.AreEqual(2, reminders.ScheduleCount[message.Id]);
    }

    [TestMethod]
    public async Task ShorterRetention_ReleasesSnoozeThatWouldOutliveExpiry()
    {
        var now = DateTimeOffset.Parse("2026-08-03T10:00:00+08:00");
        var clock = new ManualTimeProvider(now);
        var reminders = new FakeReminders();
        var source = new FakeCaptureSource();
        await using var service = CreateService(reminders, source, clock);
        await service.InitializeAsync();
        var message = await AddDirectAsync(service, source, now);
        await service.SnoozeAsync(message.Id, now.AddDays(3));

        await service.SaveSettingsAsync(service.Settings with { RetentionDays = 1 }, applyRetention: true);

        var updated = await service.GetAsync(message.Id);
        Assert.IsNotNull(updated);
        Assert.AreEqual(InboxState.Inbox, updated.State);
        Assert.IsNull(updated.SnoozedUntil);
        CollectionAssert.Contains(reminders.Cancelled, message.Id);
    }

    private InboxService CreateService(FakeReminders reminders, FakeCaptureSource source, TimeProvider clock)
    {
        var store = new SqliteMessageStore(
            Path.Combine(_directory, "messages.db"),
            new MessageCrypto(RandomNumberGenerator.GetBytes(32)));
        return new InboxService(store, reminders, [source], clock);
    }

    private static async Task<StoredMessage> AddDirectAsync(InboxService service, FakeCaptureSource source, DateTimeOffset now)
    {
        await service.StartCaptureAsync();
        await source.EmitAsync(new CapturedMessage(CaptureSourceKind.Synthetic, now, "群", "人", "正文", MessageKind.Normal, 1, "test"));
        await WaitUntilAsync(async () => (await service.ListAsync()).Count == 1);
        return (await service.ListAsync()).Single();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Timed out waiting for asynchronous capture.");
    }

    private sealed class FakeReminders : IReminderScheduler
    {
        internal Dictionary<Guid, DateTimeOffset> Scheduled { get; } = [];
        internal Dictionary<Guid, bool> Preview { get; } = [];
        internal Dictionary<Guid, int> ScheduleCount { get; } = [];
        internal List<Guid> Cancelled { get; } = [];
        public Task ScheduleAsync(StoredMessage message, DateTimeOffset dueAt, bool includePreview, CancellationToken cancellationToken = default)
        {
            Scheduled[message.Id] = dueAt;
            Preview[message.Id] = includePreview;
            ScheduleCount[message.Id] = ScheduleCount.GetValueOrDefault(message.Id) + 1;
            return Task.CompletedTask;
        }

        public Task CancelAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            Cancelled.Add(messageId);
            Scheduled.Remove(messageId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCaptureSource : ICaptureSource
    {
        public string Name => "fake";
        public CaptureHealth Health { get; private set; } = new("fake", CaptureHealthState.Stopped, "", DateTimeOffset.Now);
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public event CaptureBatchHandler? BatchCaptured;
        public event EventHandler<CaptureHealth>? HealthChanged;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            Health = Health with { State = CaptureHealthState.Healthy };
            HealthChanged?.Invoke(this, Health);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task EmitAsync(CapturedMessage message) => BatchCaptured is { } handler
            ? handler(this, CaptureBatch.Single(message), CancellationToken.None)
            : Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => Value.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    }
}
