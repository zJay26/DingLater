using DingLater.App.Services;
using DingLater.Core.Models;

namespace DingLater.Tests;

[TestClass]
public sealed class ReminderSchedulerTests
{
    [TestMethod]
    public async Task ClockJump_ReleasesOverdueButNeverExpiredMessages()
    {
        var clock = new TimerClock();
        using var scheduler = new WindowsReminderScheduler(clock);
        var ready = Message(clock.Now.AddHours(2), clock.Now.AddDays(7));
        var expired = Message(clock.Now.AddHours(1), clock.Now.AddHours(2));
        var events = new List<Guid>();
        scheduler.ReminderDue += (_, args) => events.Add(args.Message.Id);
        await scheduler.ScheduleAsync(ready, ready.SnoozedUntil!.Value, false);
        await scheduler.ScheduleAsync(expired, expired.SnoozedUntil!.Value, false);
        Assert.AreEqual(TimeSpan.FromMinutes(1), clock.Timer.DueTime);

        clock.Now = clock.Now.AddHours(3);
        clock.Timer.Fire();
        clock.Timer.Fire();

        CollectionAssert.AreEqual(new[] { ready.Id }, events);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, clock.Timer.DueTime);
    }

    [TestMethod]
    public async Task RescheduleAndCancellation_SuppressOldDueTimesAndPreview()
    {
        var clock = new TimerClock();
        using var scheduler = new WindowsReminderScheduler(clock);
        var message = Message(clock.Now.AddMinutes(1), clock.Now.AddDays(7));
        var events = new List<ReminderDueEventArgs>();
        scheduler.ReminderDue += (_, args) => events.Add(args);
        await scheduler.ScheduleAsync(message, message.SnoozedUntil!.Value, true);
        await scheduler.ScheduleAsync(message, clock.Now.AddMinutes(3), false);
        clock.Now = clock.Now.AddMinutes(2);
        clock.Timer.Fire();
        Assert.HasCount(0, events);
        clock.Now = clock.Now.AddMinutes(2);
        clock.Timer.Fire();
        Assert.HasCount(1, events);
        Assert.IsFalse(events[0].IncludePreview);

        await scheduler.ScheduleAsync(message, clock.Now.AddMinutes(1), false);
        await scheduler.CancelAsync(message.Id);
        clock.Now = clock.Now.AddHours(1);
        clock.Timer.Fire();
        Assert.HasCount(1, events);
    }

    [TestMethod]
    public async Task ClockMovesBackwards_DoesNotFireEarly()
    {
        var clock = new TimerClock();
        using var scheduler = new WindowsReminderScheduler(clock);
        var message = Message(clock.Now.AddMinutes(1), clock.Now.AddDays(7));
        var count = 0;
        scheduler.ReminderDue += (_, _) => count++;
        await scheduler.ScheduleAsync(message, message.SnoozedUntil!.Value, false);
        clock.Now = clock.Now.AddHours(-1);
        clock.Timer.Fire();
        Assert.AreEqual(0, count);
        Assert.AreEqual(TimeSpan.FromMinutes(1), clock.Timer.DueTime);
    }

    [TestMethod]
    public async Task Dispose_IsIdempotentAndRejectsNewWork()
    {
        var clock = new TimerClock();
        var scheduler = new WindowsReminderScheduler(clock);
        var message = Message(clock.Now.AddMinutes(1), clock.Now.AddDays(7));
        var count = 0;
        scheduler.ReminderDue += (_, _) => count++;
        await scheduler.ScheduleAsync(message, message.SnoozedUntil!.Value, false);
        scheduler.Dispose();
        scheduler.Dispose();
        clock.Now = clock.Now.AddHours(1);
        clock.Timer.Fire();
        Assert.AreEqual(0, count);
        Assert.IsTrue(clock.Timer.Disposed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => scheduler.ScheduleAsync(message, clock.Now.AddMinutes(1), false));
    }

    [TestMethod]
    public async Task CallbackCanCancelAnotherDueReminder()
    {
        var clock = new TimerClock();
        using var scheduler = new WindowsReminderScheduler(clock);
        var first = Message(clock.Now.AddMinutes(1), clock.Now.AddDays(7));
        var second = Message(clock.Now.AddMinutes(2), clock.Now.AddDays(7));
        var count = 0;
        scheduler.ReminderDue += (_, _) => { count++; scheduler.CancelAsync(second.Id).GetAwaiter().GetResult(); };
        await scheduler.ScheduleAsync(first, first.SnoozedUntil!.Value, false);
        await scheduler.ScheduleAsync(second, second.SnoozedUntil!.Value, false);
        clock.Now = clock.Now.AddMinutes(3);
        clock.Timer.Fire();
        Assert.AreEqual(1, count);
    }

    private static StoredMessage Message(DateTimeOffset dueAt, DateTimeOffset expiry) => new(
        Guid.NewGuid(), new CapturedMessage(CaptureSourceKind.Synthetic, dueAt.AddHours(-1), "group", "sender", "body", MessageKind.Normal, 1, "test"),
        InboxState.Snoozed, expiry, dueAt, dueAt.AddHours(-1));

    private sealed class TimerClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-07T12:00:00+08:00");
        public ManualTimer Timer { get; private set; } = null!;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new ManualTimer(callback, state, dueTime);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) { DueTime = dueTime; return !Disposed; }
        public void Fire() => callback(state);
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
