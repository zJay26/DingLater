using System.Globalization;
using System.Text.Json;
using DingLater.Core.Models;
using DingLater.Core.Security;
using Microsoft.Data.Sqlite;

namespace DingLater.Core.Storage;

public sealed class SqliteMessageStore : IMessageStore
{
    private const int SchemaVersion = 3;
    private readonly string _databasePath;
    private readonly MessageCrypto _crypto;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteMessageStore(string databasePath, MessageCrypto crypto)
    {
        _databasePath = databasePath;
        _crypto = crypto;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (IsCorruption(exception))
            {
                QuarantineCorruptDatabase();
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoreResult> AddAsync(CapturedMessage message, int retentionDays, CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 365);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await AddCoreAsync(
                connection,
                (SqliteTransaction)transaction,
                message,
                retentionDays,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CaptureBatchResult> AppendCaptureBatchAsync(
        CaptureBatch batch,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        retentionDays = Math.Clamp(retentionDays, 1, 365);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var sqliteTransaction = (SqliteTransaction)transaction;
            var inserted = new List<StoredMessage>();
            var duplicates = 0;
            foreach (var message in batch.Messages)
            {
                var result = await AddCoreAsync(
                    connection,
                    sqliteTransaction,
                    message,
                    retentionDays,
                    cancellationToken).ConfigureAwait(false);
                if (result.Inserted)
                {
                    inserted.Add(result.Message);
                }
                else
                {
                    duplicates++;
                }
            }

            foreach (var checkpoint in batch.Checkpoints)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = sqliteTransaction;
                command.CommandText = """
                    INSERT INTO capture_checkpoints(source, account_fingerprint, partition_id, position, updated_at_utc)
                    VALUES($source, $account, $partition, $position, $updated)
                    ON CONFLICT(source, account_fingerprint, partition_id) DO UPDATE SET
                        position = excluded.position,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                command.Parameters.AddWithValue("$source", (int)checkpoint.Source);
                command.Parameters.AddWithValue("$account", checkpoint.AccountFingerprint);
                command.Parameters.AddWithValue("$partition", checkpoint.Partition);
                command.Parameters.AddWithValue("$position", checkpoint.Position);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CaptureBatchResult(inserted, duplicates);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<int, long>> GetCaptureCheckpointsAsync(
        CaptureSourceKind source,
        string accountFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountFingerprint);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT partition_id, position
                FROM capture_checkpoints
                WHERE source = $source AND account_fingerprint = $account;
                """;
            command.Parameters.AddWithValue("$source", (int)source);
            command.Parameters.AddWithValue("$account", accountFingerprint);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<int, long>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result[reader.GetInt32(0)] = reader.GetInt64(1);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<StoredMessage>> ListAsync(CancellationToken cancellationToken = default) =>
        ReadManyAsync("SELECT * FROM messages ORDER BY captured_at_utc DESC;", cancellationToken);

    public async Task<StoredMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM messages WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateStateAsync(Guid id, InboxState state, DateTimeOffset? snoozedUntil, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE messages
                SET state = $state, snoozed_until_utc = $snoozed, updated_at_utc = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$snoozed", snoozedUntil is null ? DBNull.Value : ToUnix(snoozedUntil.Value));
            command.Parameters.AddWithValue("$updated", ToUnix(updatedAt));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Message {id:D} does not exist.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> ReleaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var ids = await SelectIdsAsync(connection,
                "SELECT id FROM messages WHERE state = $state AND snoozed_until_utc <= $now;",
                ("$state", (object)(int)InboxState.Snoozed), ("$now", ToUnix(now)), cancellationToken).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return ids;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE messages
                SET state = $inbox, snoozed_until_utc = NULL, updated_at_utc = $now
                WHERE state = $snoozed AND snoozed_until_utc <= $now;
                """;
            command.Parameters.AddWithValue("$inbox", (int)InboxState.Inbox);
            command.Parameters.AddWithValue("$snoozed", (int)InboxState.Snoozed);
            command.Parameters.AddWithValue("$now", ToUnix(now));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return ids;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var ids = await SelectIdsAsync(connection,
                "SELECT id FROM messages WHERE expires_at_utc <= $now;",
                ("$now", (object)ToUnix(now)), cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM messages WHERE expires_at_utc <= $now;";
            command.Parameters.AddWithValue("$now", ToUnix(now));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return ids;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountExpiringWhenRetentionChangesAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 365);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM messages WHERE captured_at_utc + $days <= $now;";
            command.Parameters.AddWithValue("$days", TimeSpan.FromDays(retentionDays).TotalMilliseconds);
            command.Parameters.AddWithValue("$now", ToUnix(now));
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> ApplyRetentionAsync(int retentionDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 365);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = "UPDATE messages SET expires_at_utc = captured_at_utc + $days;";
                update.Parameters.AddWithValue("$days", (long)TimeSpan.FromDays(retentionDays).TotalMilliseconds);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var ids = await SelectIdsAsync(connection,
                "SELECT id FROM messages WHERE expires_at_utc <= $now;",
                ("$now", (object)ToUnix(now)), cancellationToken, (SqliteTransaction)transaction).ConfigureAwait(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM messages WHERE expires_at_utc <= $now;";
                delete.Parameters.AddWithValue("$now", ToUnix(now));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ids;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = 'app' LIMIT 1;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return new AppSettings();
            }

            try
            {
                return (JsonSerializer.Deserialize<AppSettings>(value) ?? new AppSettings()).Normalize();
            }
            catch (JsonException)
            {
                return new AppSettings();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = settings.Normalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings(key, value) VALUES('app', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM messages; PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            PRAGMA journal_mode=WAL;
            PRAGMA secure_delete=ON;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                captured_at_utc INTEGER NOT NULL,
                message_at_utc INTEGER NULL,
                expires_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL,
                state INTEGER NOT NULL,
                snoozed_until_utc INTEGER NULL,
                source INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                conversation_scope INTEGER NOT NULL DEFAULT 0,
                confidence REAL NOT NULL,
                dingtalk_version TEXT NOT NULL,
                source_identity BLOB NOT NULL,
                conversation BLOB NOT NULL,
                sender BLOB NOT NULL,
                body BLOB NOT NULL,
                fingerprint BLOB NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS ix_messages_state_time ON messages(state, captured_at_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_messages_expiry ON messages(expires_at_utc);
            CREATE INDEX IF NOT EXISTS ix_messages_snooze ON messages(snoozed_until_utc);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS capture_checkpoints (
                source INTEGER NOT NULL,
                account_fingerprint TEXT NOT NULL,
                partition_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL,
                PRIMARY KEY(source, account_fingerprint, partition_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMessageAtColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureConversationScopeColumnAsync(connection, cancellationToken).ConfigureAwait(false);

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = $"PRAGMA user_version={SchemaVersion};";
            await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA quick_check;";
        var result = await checkCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is string check && !string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteException($"SQLite quick_check failed: {check}", 11);
        }
    }

    private async Task<IReadOnlyList<StoredMessage>> ReadManyAsync(string sql, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<StoredMessage>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(ReadMessage(reader));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private StoredMessage ReadMessage(SqliteDataReader reader)
    {
        var capturedAt = FromUnix(reader.GetInt64(reader.GetOrdinal("captured_at_utc")));
        var source = (CaptureSourceKind)reader.GetInt32(reader.GetOrdinal("source"));
        var captured = new CapturedMessage(
            source,
            capturedAt,
            _crypto.Decrypt((byte[])reader["conversation"]),
            _crypto.Decrypt((byte[])reader["sender"]),
            _crypto.Decrypt((byte[])reader["body"]),
            (MessageKind)reader.GetInt32(reader.GetOrdinal("kind")),
            reader.GetDouble(reader.GetOrdinal("confidence")),
            reader.GetString(reader.GetOrdinal("dingtalk_version")),
            _crypto.Decrypt((byte[])reader["source_identity"]),
            ReadNullableUnix(reader, "message_at_utc"),
            (ConversationScope)reader.GetInt32(reader.GetOrdinal("conversation_scope")));

        var snoozedOrdinal = reader.GetOrdinal("snoozed_until_utc");
        return new StoredMessage(
            Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            captured,
            (InboxState)reader.GetInt32(reader.GetOrdinal("state")),
            FromUnix(reader.GetInt64(reader.GetOrdinal("expires_at_utc"))),
            reader.IsDBNull(snoozedOrdinal) ? null : FromUnix(reader.GetInt64(snoozedOrdinal)),
            FromUnix(reader.GetInt64(reader.GetOrdinal("updated_at_utc"))));
    }

    private async Task<StoreResult> AddCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CapturedMessage message,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        var duplicateFingerprints = Fingerprints(message);
        var primaryFingerprint = duplicateFingerprints.Count == 1
            ? duplicateFingerprints[0]
            : duplicateFingerprints[1];
        var duplicate = await FindDuplicateAsync(
            connection,
            duplicateFingerprints,
            cancellationToken,
            transaction).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return new StoreResult(duplicate, false);
        }

        var id = Guid.NewGuid();
        var expiresAt = message.CapturedAt.AddDays(retentionDays);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO messages (
                id, captured_at_utc, message_at_utc, expires_at_utc, updated_at_utc, state, snoozed_until_utc,
                source, kind, conversation_scope, confidence, dingtalk_version, source_identity,
                conversation, sender, body, fingerprint)
            VALUES (
                $id, $captured, $messageAt, $expires, $updated, $state, NULL,
                $source, $kind, $conversationScope, $confidence, $version, $identity,
                $conversation, $sender, $body, $fingerprint);
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$captured", ToUnix(message.CapturedAt));
        command.Parameters.AddWithValue("$messageAt", message.MessageAt is null ? DBNull.Value : ToUnix(message.MessageAt.Value));
        command.Parameters.AddWithValue("$expires", ToUnix(expiresAt));
        command.Parameters.AddWithValue("$updated", ToUnix(message.CapturedAt));
        command.Parameters.AddWithValue("$state", (int)InboxState.Inbox);
        command.Parameters.AddWithValue("$source", (int)message.Source);
        command.Parameters.AddWithValue("$kind", (int)message.Kind);
        command.Parameters.AddWithValue("$conversationScope", (int)message.ConversationScope);
        command.Parameters.AddWithValue("$confidence", message.Confidence);
        command.Parameters.AddWithValue("$version", message.DingTalkVersion ?? string.Empty);
        command.Parameters.AddWithValue("$identity", _crypto.Encrypt(message.SourceIdentity ?? string.Empty));
        command.Parameters.AddWithValue("$conversation", _crypto.Encrypt(message.Conversation));
        command.Parameters.AddWithValue("$sender", _crypto.Encrypt(message.Sender));
        command.Parameters.AddWithValue("$body", _crypto.Encrypt(message.VisibleBody));
        command.Parameters.AddWithValue("$fingerprint", primaryFingerprint);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (inserted)
        {
            return new StoreResult(new StoredMessage(id, message, InboxState.Inbox, expiresAt, null, message.CapturedAt), true);
        }

        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT * FROM messages WHERE fingerprint = $fingerprint LIMIT 1;";
        lookup.Parameters.AddWithValue("$fingerprint", primaryFingerprint);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A duplicate message could not be reloaded.");
        }

        return new StoreResult(ReadMessage(reader), false);
    }

    private static async Task EnsureMessageAtColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA table_info(messages);";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), "message_at_utc", StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        if (!found)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE messages ADD COLUMN message_at_utc INTEGER NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureConversationScopeColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA table_info(messages);";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), "conversation_scope", StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        if (!found)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE messages ADD COLUMN conversation_scope INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset? ReadNullableUnix(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : FromUnix(reader.GetInt64(ordinal));
    }

    private async Task<StoredMessage?> FindDuplicateAsync(
        SqliteConnection connection,
        IReadOnlyList<byte[]> fingerprints,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new List<string>(fingerprints.Count);
        for (var index = 0; index < fingerprints.Count; index++)
        {
            var name = $"$fingerprint{index}";
            parameterNames.Add(name);
            command.Parameters.AddWithValue(name, fingerprints[index]);
        }

        command.CommandText = $"SELECT * FROM messages WHERE fingerprint IN ({string.Join(", ", parameterNames)}) LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON; PRAGMA busy_timeout=3000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            throw;
        }
    }

    private static async Task<List<Guid>> SelectIdsAsync(
        SqliteConnection connection,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        await SelectIdsAsync(connection, sql, [parameter], cancellationToken, transaction).ConfigureAwait(false);

    private static async Task<List<Guid>> SelectIdsAsync(
        SqliteConnection connection,
        string sql,
        (string Name, object Value) first,
        (string Name, object Value) second,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        await SelectIdsAsync(connection, sql, [first, second], cancellationToken, transaction).ConfigureAwait(false);

    private static async Task<List<Guid>> SelectIdsAsync(
        SqliteConnection connection,
        string sql,
        IEnumerable<(string Name, object Value)> parameters,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    private static long ToUnix(DateTimeOffset value) => value.ToUniversalTime().ToUnixTimeMilliseconds();
    private static DateTimeOffset FromUnix(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private IReadOnlyList<byte[]> Fingerprints(CapturedMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.SourceIdentity))
        {
            return
            [
                _crypto.Fingerprint(
                    "source-identity",
                    ((int)message.Source).ToString(CultureInfo.InvariantCulture),
                    message.SourceIdentity)
            ];
        }

        return Enumerable.Range(-1, 3)
            .Select(offset => FingerprintByVisibleContent(message, message.CapturedAt.AddMinutes(offset)))
            .ToList();
    }

    private byte[] FingerprintByVisibleContent(CapturedMessage message, DateTimeOffset bucketTime) =>
        _crypto.Fingerprint(
            message.Conversation,
            message.Sender,
            message.VisibleBody,
            bucketTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));

    private static bool IsCorruption(SqliteException exception) => exception.SqliteErrorCode is 11 or 26;

    private void QuarantineCorruptDatabase()
    {
        SqliteConnection.ClearAllPools();
        var directory = Path.GetDirectoryName(_databasePath)!;
        var quarantine = Path.Combine(directory, "quarantine");
        Directory.CreateDirectory(quarantine);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(quarantine, Path.GetFileName(path) + "." + stamp), overwrite: true);
            }
        }
    }
}
