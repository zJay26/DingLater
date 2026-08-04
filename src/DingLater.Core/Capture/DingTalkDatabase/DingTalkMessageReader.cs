using System.Globalization;
using System.Text.Json;
using DingLater.Core.Models;
using Microsoft.Data.Sqlite;

namespace DingLater.Core.Capture.DingTalkDatabase;

internal sealed record DingTalkReadResult(
    IReadOnlyList<CapturedMessage> Messages,
    IReadOnlyList<CaptureCheckpoint> Checkpoints,
    int CandidateCount,
    int EmptyBodyCount,
    bool HasMore);

internal sealed class DingTalkMessageReader
{
    internal const int PartitionCount = 128;
    private const int MaximumRowsPerCycle = 4096;
    private const string CompatibilityVersion = "8.3.45.260720005 / V3";

    internal async Task<IReadOnlyDictionary<int, long>> ValidateAndReadPositionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateTableAsync(
            connection,
            "tbconversation",
            ["cid", "type", "title", "status"],
            cancellationToken).ConfigureAwait(false);
        await ValidateTableAsync(
            connection,
            "tbuser_profile_v2",
            ["uid", "nick", "realName"],
            cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<int, long>(PartitionCount);
        for (var partition = 0; partition < PartitionCount; partition++)
        {
            var table = TableName(partition);
            await ValidateTableAsync(
                connection,
                table,
                ["primaryKey", "cid", "mid", "senderId", "createdAt", "contentType", "content", "recallStatus", "atIds", "attachments"],
                cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COALESCE(MAX(primaryKey), 0) FROM {table};";
            result[partition] = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        return result;
    }

    internal async Task<DingTalkReadResult> ReadNewAsync(
        SqliteConnection connection,
        DingTalkAccount account,
        IReadOnlyDictionary<int, long> positions,
        GroupCaptureMode groupCaptureMode,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var conversations = await ReadConversationsAsync(connection, cancellationToken).ConfigureAwait(false);
        var users = await ReadUsersAsync(connection, cancellationToken).ConfigureAwait(false);
        var messages = new List<CapturedMessage>();
        var checkpoints = new List<CaptureCheckpoint>();
        var candidateCount = 0;
        var emptyBodyCount = 0;
        var remaining = MaximumRowsPerCycle;

        for (var partition = 0; partition < PartitionCount && remaining > 0; partition++)
        {
            var position = positions.GetValueOrDefault(partition);
            var table = TableName(partition);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT primaryKey, cid, mid, senderId, createdAt, contentType,
                       content, recallStatus, atIds, attachments
                FROM {table}
                WHERE primaryKey > $position
                ORDER BY primaryKey
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$limit", remaining);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var lastPosition = position;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                remaining--;
                lastPosition = reader.GetInt64(0);
                var senderId = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                if (senderId == account.SelfUserId)
                {
                    continue;
                }

                candidateCount++;
                if (!reader.IsDBNull(7) && reader.GetInt32(7) != 0)
                {
                    continue;
                }

                var conversationId = reader.GetString(1);
                conversations.TryGetValue(conversationId, out var conversation);
                var atIds = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
                var contentType = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                var content = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                var attachments = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
                var parsed = DingTalkMessageContentParser.Parse(contentType, content, attachments);
                var isMention = ContainsUserId(atIds, account.SelfUserId)
                                || ContainsBroadcastMention(atIds, content, parsed.Body);
                var isDirect = conversation?.Type == 1
                               || conversationId.Contains(':', StringComparison.Ordinal);
                if (!isDirect && groupCaptureMode == GroupCaptureMode.MentionsOnly && !isMention)
                {
                    continue;
                }

                if (parsed.Body.StartsWith("[空", StringComparison.Ordinal))
                {
                    emptyBodyCount++;
                }

                var sender = users.GetValueOrDefault(senderId);
                var senderName = FirstNonEmpty(sender?.RealName, sender?.Nick, "未知发送者");
                var conversationName = FirstNonEmpty(conversation?.Title, isDirect ? senderName : null, "未命名会话");
                var messageAt = ReadTimestamp(reader, 4);
                var kind = isMention ? MessageKind.Mention : parsed.Kind;
                var messageId = reader.GetInt64(2);
                var sourceIdentity = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{account.AccountFingerprint}:{partition}:{lastPosition}:{conversationId}:{messageId}");
                messages.Add(new CapturedMessage(
                    CaptureSourceKind.DingTalkDatabase,
                    capturedAt,
                    conversationName,
                    senderName,
                    parsed.Body,
                    kind,
                    1,
                    CompatibilityVersion,
                    sourceIdentity,
                    messageAt,
                    isDirect ? ConversationScope.Direct : ConversationScope.Group));
            }

            if (lastPosition != position)
            {
                checkpoints.Add(new CaptureCheckpoint(
                    CaptureSourceKind.DingTalkDatabase,
                    account.AccountFingerprint,
                    partition,
                    lastPosition));
            }
        }

        messages.Sort((left, right) =>
        {
            var byTime = Nullable.Compare(left.MessageAt, right.MessageAt);
            return byTime != 0
                ? byTime
                : string.Compare(left.SourceIdentity, right.SourceIdentity, StringComparison.Ordinal);
        });
        return new DingTalkReadResult(messages, checkpoints, candidateCount, emptyBodyCount, remaining == 0);
    }

    private static async Task<Dictionary<string, ConversationInfo>> ReadConversationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cid, type, title FROM tbconversation WHERE status = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, ConversationInfo>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = new ConversationInfo(
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
        }

        return result;
    }

    private static async Task<Dictionary<long, UserInfo>> ReadUsersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid, nick, realName FROM tbuser_profile_v2;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<long, UserInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetInt64(0)] = new UserInfo(
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
        }

        return result;
    }

    private static async Task ValidateTableAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> requiredColumns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        if (requiredColumns.Any(column => !columns.Contains(column)))
        {
            throw new DingTalkCaptureException("schema_incompatible", "当前钉钉数据库结构尚未通过兼容性验证。");
        }
    }

    private static bool ContainsUserId(string json, long userId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsUserId(document.RootElement, userId.ToString(CultureInfo.InvariantCulture));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsUserId(JsonElement element, string userId)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), userId, StringComparison.Ordinal),
            JsonValueKind.Number => element.TryGetInt64(out var numeric)
                                    && string.Equals(numeric.ToString(CultureInfo.InvariantCulture), userId, StringComparison.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsUserId(item, userId)),
            JsonValueKind.Object => element.EnumerateObject().Any(property =>
                string.Equals(property.Name, userId, StringComparison.Ordinal)
                || ContainsUserId(property.Value, userId)),
            _ => false
        };
    }

    private static bool ContainsBroadcastMention(string atIdsJson, string contentJson, string body) =>
        ContainsBroadcastMarker(atIdsJson, allowStandaloneTarget: true)
        || ContainsBroadcastMarker(contentJson, allowStandaloneTarget: false)
        || body.Contains("@所有人", StringComparison.Ordinal)
        || body.Contains("@全体成员", StringComparison.Ordinal)
        || body.Contains("@全员", StringComparison.Ordinal);

    private static bool ContainsBroadcastMarker(string json, bool allowStandaloneTarget)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsBroadcastMarker(document.RootElement, allowStandaloneTarget);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsBroadcastMarker(JsonElement element, bool allowStandaloneTarget)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var isFlag = IsBroadcastFlagName(property.Name)
                                 || (allowStandaloneTarget && IsBroadcastTarget(property.Name));
                    if (isFlag)
                    {
                        if (IsTruthy(property.Value)
                            || (allowStandaloneTarget
                                && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array))
                        {
                            return true;
                        }

                        continue;
                    }

                    if (ContainsBroadcastMarker(property.Value, allowStandaloneTarget))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(item => ContainsBroadcastMarker(item, allowStandaloneTarget));
            case JsonValueKind.String:
                return allowStandaloneTarget && IsBroadcastTarget(element.GetString());
            case JsonValueKind.Number:
                return allowStandaloneTarget
                       && element.TryGetInt64(out var numeric)
                       && numeric is 0 or -1;
            default:
                return false;
        }
    }

    private static bool IsBroadcastFlagName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "isatall" or "atall" or "ismentionall" or "mentionall";
    }

    private static bool IsTruthy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => element.TryGetInt64(out var numeric) && numeric != 0,
        JsonValueKind.String => string.Equals(element.GetString(), "true", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(element.GetString(), "yes", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(element.GetString(), "1", StringComparison.Ordinal),
        _ => false
    };

    private static bool IsBroadcastTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed is "0" or "-1")
        {
            return true;
        }

        var normalized = trimmed.TrimStart('@')
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "all" or "everyone" or "atall" or "所有人" or "全体成员" or "全员";
    }

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var milliseconds = reader.GetInt64(ordinal);
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(string? first, string? second, string fallback) =>
        !string.IsNullOrWhiteSpace(first)
            ? first.Trim()
            : !string.IsNullOrWhiteSpace(second)
                ? second.Trim()
                : fallback;

    private static string TableName(int partition) => $"tbmsg_{partition:000}";

    private sealed record ConversationInfo(int Type, string Title);
    private sealed record UserInfo(string Nick, string RealName);
}
