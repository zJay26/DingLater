using DingLater.App.ViewModels;

namespace DingLater.Tests;

[TestClass]
public sealed class DisplayTimeFormatterTests
{
    // Wednesday 2026-08-05 10:00 local; built from local DateTime so results do not depend on the runner's zone.
    private static readonly DateTimeOffset Now = Local(2026, 8, 5, 10, 0);

    [TestMethod]
    public void Compact_UsesClockTodayAndRelativeNamesNearby()
    {
        Assert.AreEqual("09:05", DisplayTimeFormatter.Compact(Local(2026, 8, 5, 9, 5), Now));
        Assert.AreEqual("昨天", DisplayTimeFormatter.Compact(Local(2026, 8, 4, 23, 59), Now));
        Assert.AreEqual("周一", DisplayTimeFormatter.Compact(Local(2026, 8, 3, 8, 0), Now));
        Assert.AreEqual("周四", DisplayTimeFormatter.Compact(Local(2026, 7, 30, 8, 0), Now));
        Assert.AreEqual("7月29日", DisplayTimeFormatter.Compact(Local(2026, 7, 29, 8, 0), Now));
        Assert.AreEqual("2025/12/31", DisplayTimeFormatter.Compact(Local(2025, 12, 31, 8, 0), Now));
    }

    [TestMethod]
    public void Compact_FutureDayFromClockSkew_ShowsDateInsteadOfMisleadingClock()
    {
        Assert.AreEqual("8月6日", DisplayTimeFormatter.Compact(Local(2026, 8, 6, 1, 0), Now));
    }

    [TestMethod]
    public void Detailed_AlwaysKeepsMinutePrecision()
    {
        Assert.AreEqual("09:05", DisplayTimeFormatter.Detailed(Local(2026, 8, 5, 9, 5), Now));
        Assert.AreEqual("昨天 23:59", DisplayTimeFormatter.Detailed(Local(2026, 8, 4, 23, 59), Now));
        Assert.AreEqual("8月1日 07:30", DisplayTimeFormatter.Detailed(Local(2026, 8, 1, 7, 30), Now));
        Assert.AreEqual("2025年12月31日 18:00", DisplayTimeFormatter.Detailed(Local(2025, 12, 31, 18, 0), Now));
    }

    [TestMethod]
    public void Upcoming_NamesTodayTomorrowAndDayAfter()
    {
        Assert.AreEqual("今天 22:43", DisplayTimeFormatter.Upcoming(Local(2026, 8, 5, 22, 43), Now));
        Assert.AreEqual("明天 09:00", DisplayTimeFormatter.Upcoming(Local(2026, 8, 6, 9, 0), Now));
        Assert.AreEqual("后天 09:00", DisplayTimeFormatter.Upcoming(Local(2026, 8, 7, 9, 0), Now));
        Assert.AreEqual("8月18日 09:00", DisplayTimeFormatter.Upcoming(Local(2026, 8, 18, 9, 0), Now));
        Assert.AreEqual("2027年1月2日 09:00", DisplayTimeFormatter.Upcoming(Local(2027, 1, 2, 9, 0), Now));
        Assert.AreEqual("昨天 21:00", DisplayTimeFormatter.Upcoming(Local(2026, 8, 4, 21, 0), Now));
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));
}
