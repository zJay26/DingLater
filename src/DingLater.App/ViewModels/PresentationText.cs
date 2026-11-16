using System.Globalization;

namespace DingLater.App.ViewModels;

internal static class PresentationText
{
    internal const string NonTextPlaceholder = "[非文字消息]";

    /// <summary>Collapses line breaks and whitespace runs so a preview fits its allotted lines.</summary>
    internal static string SingleLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Single-line preview that never renders as an empty row.</summary>
    internal static string Preview(string? value)
    {
        var line = SingleLine(value);
        return line.Length == 0 ? NonTextPlaceholder : line;
    }

    /// <summary>First user-perceived character, keeping emoji and combined glyphs intact for avatars.</summary>
    internal static string Initial(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length == 0 ? "?" : StringInfo.GetNextTextElement(text);
    }
}
