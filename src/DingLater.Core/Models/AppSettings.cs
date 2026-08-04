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
    int QuickSnoozeMinutes = 30)
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
        QuickSnoozeMinutes = Math.Clamp(QuickSnoozeMinutes, 1, 1440)
    };
}
