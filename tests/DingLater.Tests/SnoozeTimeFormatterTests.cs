using DingLater.App.ViewModels;

namespace DingLater.Tests;

[TestClass]
public sealed class SnoozeTimeFormatterTests
{
    [DataTestMethod]
    [DataRow(1, "1 分钟后")]
    [DataRow(10, "10 分钟后")]
    [DataRow(15, "15 分钟后")]
    [DataRow(30, "30 分钟后")]
    [DataRow(60, "1 小时后")]
    [DataRow(74, "1 小时 14 分钟后")]
    [DataRow(1440, "1 天后")]
    public void FormatRelativeMinutes_UsesNaturalMinutePrecision(int minutes, string expected) =>
        Assert.AreEqual(expected, SnoozeTimeFormatter.FormatRelativeMinutes(minutes));

    [TestMethod]
    public void Validation_RequiresReminderStrictlyBeforeExpiry()
    {
        var now = DateTimeOffset.Parse("2026-08-04T10:00:00+08:00");
        var expires = now.AddHours(2);

        Assert.IsFalse(SnoozeTimeFormatter.TryValidate(now, expires, now, out _));
        Assert.IsTrue(SnoozeTimeFormatter.TryValidate(expires.AddMinutes(-1), expires, now, out _));
        Assert.IsFalse(SnoozeTimeFormatter.TryValidate(expires, expires, now, out _));
        Assert.IsFalse(SnoozeTimeFormatter.TryValidate(expires.AddMinutes(1), expires, now, out _));
    }

    [TestMethod]
    public void TomorrowAtNine_IsAlwaysNextNaturalDay()
    {
        var late = new DateTimeOffset(new DateTime(2026, 8, 4, 23, 59, 0, DateTimeKind.Local));
        var early = new DateTimeOffset(new DateTime(2026, 8, 4, 0, 1, 0, DateTimeKind.Local));
        var expected = new DateTimeOffset(new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Local));

        Assert.AreEqual(expected, SnoozeTimeFormatter.TomorrowAtNine(late));
        Assert.AreEqual(expected, SnoozeTimeFormatter.TomorrowAtNine(early));
    }
}
