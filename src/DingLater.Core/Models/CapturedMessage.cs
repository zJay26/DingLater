namespace DingLater.Core.Models;

public sealed record CapturedMessage(
    CaptureSourceKind Source,
    DateTimeOffset CapturedAt,
    string Conversation,
    string Sender,
    string VisibleBody,
    MessageKind Kind,
    double Confidence,
    string DingTalkVersion,
    string SourceIdentity = "",
    DateTimeOffset? MessageAt = null,
    ConversationScope ConversationScope = ConversationScope.Unknown);
