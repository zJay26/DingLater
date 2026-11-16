namespace DingLater.App.ViewModels;

public sealed record TrayContactViewModel(
    string Key,
    string Name,
    string AvatarText,
    int Count,
    string Preview,
    string TimeText,
    Guid LatestMessageId)
{
    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string AccessibleText => $"{Name}，{Count} 条待处理，{TimeText}，{Preview}";

    /// <summary>List items are announced through ToString; never expose the record's internal key.</summary>
    public override string ToString() => AccessibleText;
}
