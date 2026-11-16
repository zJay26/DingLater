using System.Globalization;

namespace DingLater.App.ViewModels;

/// <summary>
/// Chat-style timestamps: precise for today, relative names for nearby days and full dates further out.
/// Callers pass "now" so labels stay testable and can be rebuilt when the local day changes.
/// </summary>
internal static class DisplayTimeFormatter
{
    private static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>Compact label for list rows: 16:41, 昨天, 周三, 8月8日 or 2025/8/8.</summary>
    internal static string Compact(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime;
        var days = (now.LocalDateTime.Date - local.Date).Days;
        if (days == 0)
        {
            return Clock(local);
        }

        if (days == 1)
        {
            return "昨天";
        }

        if (days is > 1 and < 7)
        {
            return Weekdays[(int)local.DayOfWeek];
        }

        return local.Year == now.LocalDateTime.Year
            ? $"{local.Month}月{local.Day}日"
            : $"{local.Year}/{local.Month}/{local.Day}";
    }

    /// <summary>Precise label for message rows: 16:41, 昨天 16:41, 8月8日 16:41 or 2025年8月8日 16:41.</summary>
    internal static string Detailed(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime;
        var today = now.LocalDateTime.Date;
        if (local.Date == today)
        {
            return Clock(local);
        }

        return local.Date == today.AddDays(-1)
            ? $"昨天 {Clock(local)}"
            : $"{Date(local, today)} {Clock(local)}";
    }

    /// <summary>Label for reminders: 今天 22:43, 明天 09:00, 后天 09:00 or 8月18日 09:00.</summary>
    internal static string Upcoming(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime;
        var today = now.LocalDateTime.Date;
        return (local.Date - today).Days switch
        {
            -1 => $"昨天 {Clock(local)}",
            0 => $"今天 {Clock(local)}",
            1 => $"明天 {Clock(local)}",
            2 => $"后天 {Clock(local)}",
            _ => $"{Date(local, today)} {Clock(local)}"
        };
    }

    private static string Clock(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Date(DateTime local, DateTime today) => local.Year == today.Year
        ? $"{local.Month}月{local.Day}日"
        : $"{local.Year}年{local.Month}月{local.Day}日";
}
