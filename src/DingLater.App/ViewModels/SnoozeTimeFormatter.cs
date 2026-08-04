namespace DingLater.App.ViewModels;

internal static class SnoozeTimeFormatter
{
    internal static string FormatRelativeMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 1440);
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var remainingMinutes = minutes % 60;
        var parts = new List<string>(3);
        if (days > 0)
        {
            parts.Add($"{days} 天");
        }

        if (hours > 0)
        {
            parts.Add($"{hours} 小时");
        }

        if (remainingMinutes > 0)
        {
            parts.Add($"{remainingMinutes} 分钟");
        }

        return string.Join(" ", parts) + "后";
    }

    internal static DateTimeOffset TomorrowAtNine(DateTimeOffset now)
    {
        var local = now.LocalDateTime.Date.AddDays(1).AddHours(9);
        return new DateTimeOffset(local);
    }

    internal static bool TryValidate(
        DateTimeOffset dueAt,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        out string error)
    {
        if (dueAt <= now)
        {
            error = "稍后时间必须晚于当前时间。";
            return false;
        }

        if (dueAt > expiresAt)
        {
            error = $"这条消息会在 {expiresAt.LocalDateTime:M月d日 HH:mm} 自动清理，请选择更早时间。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
