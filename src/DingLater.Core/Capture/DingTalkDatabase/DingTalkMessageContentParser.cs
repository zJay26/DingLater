using System.Text.Json;
using DingLater.Core.Models;

namespace DingLater.Core.Capture.DingTalkDatabase;

public sealed record ParsedDingTalkContent(string Body, MessageKind Kind);

public static class DingTalkMessageContentParser
{
    private static readonly HashSet<int> RichTextTypes = [1200, 1201, 1202];
    private static readonly HashSet<int> CardTypes = [1400, 2900, 2950, 3100];
    private static readonly string[] PreferredTextKeys =
    [
        "text", "markdown", "desc", "description", "title", "searchDesc",
        "interactiveCardLastMessage", "LastMessageI18n", "zh_CN", "f_name", "filename"
    ];

    public static ParsedDingTalkContent Parse(int contentType, string content, string attachments)
    {
        return contentType switch
        {
            1 => new ParsedDingTalkContent(ParsePlainText(content), MessageKind.Normal),
            2 => new ParsedDingTalkContent("[图片]", MessageKind.Attachment),
            300 => new ParsedDingTalkContent("[语音]", MessageKind.Attachment),
            501 => new ParsedDingTalkContent(ParseFileName(content, attachments), MessageKind.Attachment),
            1101 => new ParsedDingTalkContent("[通话记录]", MessageKind.Normal),
            _ when RichTextTypes.Contains(contentType) =>
                new ParsedDingTalkContent(ParseStructuredText(content, attachments, "[富文本消息]"), MessageKind.Normal),
            _ when CardTypes.Contains(contentType) =>
                new ParsedDingTalkContent(ParseStructuredText(content, attachments, $"[卡片消息 {contentType}]"), MessageKind.Normal),
            _ => new ParsedDingTalkContent(
                ParseStructuredText(content, attachments, $"[暂不支持的消息类型 {contentType}]"),
                MessageKind.Unknown)
        };
    }

    private static string ParsePlainText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "[空文字消息]";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return Normalize(text.GetString()) ?? "[空文字消息]";
            }
        }
        catch (JsonException)
        {
            return Normalize(content) ?? "[空文字消息]";
        }

        return Normalize(content) ?? "[空文字消息]";
    }

    private static string ParseFileName(string content, string attachments)
    {
        var values = ExtractPreferredText(attachments)
            .Concat(ExtractPreferredText(content))
            .ToList();
        var name = values.FirstOrDefault(value =>
            value.Length <= 512
            && (value.Contains('.', StringComparison.Ordinal)
                || value.Contains("文件", StringComparison.Ordinal)));
        return string.IsNullOrWhiteSpace(name) ? "[文件]" : $"[文件] {name}";
    }

    private static string ParseStructuredText(string content, string attachments, string fallback)
    {
        var values = ExtractPreferredText(content)
            .Concat(ExtractPreferredText(attachments))
            .Select(Normalize)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return values.Count == 0 ? fallback : string.Join(Environment.NewLine, values);
    }

    private static IEnumerable<string> ExtractPreferredText(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var value in Walk(document.RootElement))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> Walk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in PreferredTextKeys)
            {
                if (!element.TryGetProperty(key, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
                {
                    if (LooksLikeJson(text))
                    {
                        foreach (var nested in ExtractPreferredText(text))
                        {
                            yield return nested;
                        }
                    }
                    else
                    {
                        yield return text;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    foreach (var nested in Walk(property.Value))
                    {
                        yield return nested;
                    }
                }
                else if (property.Value.ValueKind == JsonValueKind.String
                         && property.Name is "extension" or "content" or "data"
                         && property.Value.GetString() is { } nestedJson
                         && LooksLikeJson(nestedJson))
                {
                    foreach (var nested in ExtractPreferredText(nestedJson))
                    {
                        yield return nested;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in Walk(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return !trimmed.IsEmpty && trimmed[0] is '{' or '[';
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
