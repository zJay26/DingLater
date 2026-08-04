using DingLater.App.Services;
using DingLater.Core.Models;

namespace DingLater.Tests;

[TestClass]
public sealed class PortableServicesTests
{
    [TestMethod]
    public async Task ReminderScheduler_FiresLocallyAndCancellationSuppressesEvent()
    {
        using var scheduler = new WindowsReminderScheduler();
        var fired = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.ReminderDue += (_, args) => fired.TrySetResult(args.Message.Id);
        var now = DateTimeOffset.Now;
        var captured = new CapturedMessage(
            CaptureSourceKind.Synthetic,
            now,
            "会话",
            "发送者",
            "正文",
            MessageKind.Normal,
            1,
            "test");
        var first = new StoredMessage(Guid.NewGuid(), captured, InboxState.Snoozed, now.AddDays(7), now.AddMilliseconds(80), now);
        var cancelled = first with { Id = Guid.NewGuid(), SnoozedUntil = now.AddMilliseconds(100) };

        await scheduler.ScheduleAsync(first, first.SnoozedUntil!.Value, includePreview: true);
        await scheduler.ScheduleAsync(cancelled, cancelled.SnoozedUntil!.Value, includePreview: false);
        await scheduler.CancelAsync(cancelled.Id);

        Assert.AreEqual(first.Id, await fired.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(150);
        Assert.AreEqual(first.Id, fired.Task.Result);
    }

    [TestMethod]
    public void Diagnostics_OmitFreeFormDetailThatCouldContainMessageText()
    {
        const string marker = "DINGLATER_DIAGNOSTIC_PRIVACY_MARKER";
        var diagnostics = DiagnosticsService.Build(
        [
            new CaptureHealth(
                "source",
                CaptureHealthState.Faulted,
                marker,
                DateTimeOffset.Now,
                ErrorCode: "safe_code")
        ]);

        Assert.IsFalse(diagnostics.Contains(marker, StringComparison.Ordinal));
        StringAssert.Contains(diagnostics, "safe_code");
    }
}
