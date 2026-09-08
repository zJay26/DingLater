namespace DingLater.Core.Models;

public sealed record AppSettings(
    int SchemaVersion = 3,
    int RetentionDays = 7,
    bool OnboardingCompleted = false,
    bool CapturePaused = false,
    bool StartupChoiceMade = false,
    bool StartWithWindows = false,
    bool ShowReminderPreview = false,
    int CaptureConsentVersion = 0,
    GroupCaptureMode GroupCaptureMode = GroupCaptureMode.MentionsOnly,
    UiFontScale UiFontScale = global::DingLater.Core.Models.UiFontScale.Standard,
    int QuickSnoozeMinutes = 30,
    bool AutomaticallyCheckUpdates = true,
    string UpdateDownloadDirectory = "",
    DateTimeOffset? LastUpdateCheckUtc = null,
    string SkippedUpdateVersion = "")
{
    public AppSettings Normalize() => this with
    {
        SchemaVersion = 3,
        RetentionDays = Math.Clamp(RetentionDays, 1, 365),
        CaptureConsentVersion = Math.Clamp(CaptureConsentVersion, 0, 1),
        GroupCaptureMode = Enum.IsDefined(GroupCaptureMode)
            ? GroupCaptureMode
            : GroupCaptureMode.MentionsOnly,
        UiFontScale = Enum.IsDefined(UiFontScale)
            ? UiFontScale
            : global::DingLater.Core.Models.UiFontScale.Standard,
        QuickSnoozeMinutes = Math.Clamp(QuickSnoozeMinutes, 1, 1440),
        UpdateDownloadDirectory = NormalizeDownloadDirectory(UpdateDownloadDirectory),
        SkippedUpdateVersion = SkippedUpdateVersion?.Trim() ?? string.Empty
    };

    private static string NormalizeDownloadDirectory(string? directory)
    {
        try
        {
            return string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim())
                ? string.Empty : Path.GetFullPath(directory.Trim());
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
