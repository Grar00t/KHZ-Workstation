using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KHZ.App.Chat;

internal sealed class SqliteLocalAiStore
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;

    internal SqliteLocalAiStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException(
                "Database path is required.",
                nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    internal string DatabasePath => _databasePath;

    internal void Initialize()
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException(
                "Local AI state directory could not be resolved.");

        Directory.CreateDirectory(directory);

        using var connection = OpenConnection();

        var current = Convert.ToInt32(
            ExecuteScalar(connection, "PRAGMA user_version;"),
            CultureInfo.InvariantCulture);

        if (current > SchemaVersion)
        {
            throw new InvalidDataException(
                $"Local AI database schema {current} is newer than supported schema {SchemaVersion}.");
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS local_ai_config
            (
                id          INTEGER PRIMARY KEY CHECK(id = 1),
                config_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chat_conversation
            (
                conversation_id TEXT PRIMARY KEY NOT NULL,
                context_id      TEXT NOT NULL,
                title           TEXT NOT NULL
                                CHECK(length(trim(title)) BETWEEN 1 AND 160),
                created_utc     TEXT NOT NULL,
                updated_utc     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_chat_conversation_context_updated
            ON chat_conversation(context_id, updated_utc DESC);

            CREATE TABLE IF NOT EXISTS chat_message
            (
                sequence            INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id          TEXT NOT NULL UNIQUE,
                conversation_id     TEXT NOT NULL,
                role                TEXT NOT NULL
                                    CHECK(role IN ('user', 'assistant', 'tool')),
                content             TEXT NOT NULL,
                tool_name           TEXT,
                tool_call_id        TEXT,
                tool_arguments_json TEXT,
                created_utc         TEXT NOT NULL,

                FOREIGN KEY(conversation_id)
                    REFERENCES chat_conversation(conversation_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_chat_message_conversation_sequence
            ON chat_message(conversation_id, sequence);
            """;
        command.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = $"PRAGMA user_version={SchemaVersion};";
        version.ExecuteNonQuery();

        transaction.Commit();

        var integrity = Convert.ToString(
            ExecuteScalar(connection, "PRAGMA integrity_check;"),
            CultureInfo.InvariantCulture);

        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Local AI database integrity check failed: {integrity ?? "UNKNOWN"}");
        }
    }

    internal LocalAiSettings GetSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT config_json
            FROM local_ai_config
            WHERE id = 1;
            """;

        var json = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(json))
            return LocalAiSettings.Default();

        try
        {
            return JsonSerializer.Deserialize<LocalAiSettings>(json)
                ?? LocalAiSettings.Default();
        }
        catch (JsonException)
        {
            return LocalAiSettings.Default();
        }
    }

    internal LocalAiSettings SaveSettings(LocalAiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.ValidateForUse();
        var now = DateTimeOffset.UtcNow.ToString(
            "O",
            CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO local_ai_config(id, config_json, updated_utc)
            VALUES(1, $config_json, $updated_utc)
            ON CONFLICT(id)
            DO UPDATE SET
                config_json = excluded.config_json,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue(
            "$config_json",
            JsonSerializer.Serialize(normalized));
        command.Parameters.AddWithValue("$updated_utc", now);
        command.ExecuteNonQuery();
        transaction.Commit();
        return normalized;
    }

    internal void ClearSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_ai_config WHERE id = 1;";
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<ChatConversation> ListConversations(
        string contextId,
        int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(contextId))
            throw new ArgumentException("Context ID is required.", nameof(contextId));

        limit = Math.Clamp(limit, 1, 500);
        var result = new List<ChatConversation>();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                conversation_id,
                context_id,
                title,
                created_utc,
                updated_utc
            FROM chat_conversation
            WHERE context_id = $context_id
            ORDER BY updated_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$context_id", contextId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ChatConversation(
                ConversationId: reader.GetString(0),
                ContextId: reader.GetString(1),
                Title: reader.GetString(2),
                CreatedAt: ParseTimestamp(reader.GetString(3)),
                UpdatedAt: ParseTimestamp(reader.GetString(4))));
        }

        return result;
    }

    internal ChatConversation CreateConversation(
        string contextId,
        string title = "New chat")
    {
        if (string.IsNullOrWhiteSpace(contextId))
            throw new ArgumentException("Context ID is required.", nameof(contextId));

        title = NormalizeTitle(title);
        var id = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        var stamp = now.ToString("O", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chat_conversation
            (
                conversation_id,
                context_id,
                title,
                created_utc,
                updated_utc
            )
            VALUES
            (
                $conversation_id,
                $context_id,
                $title,
                $created_utc,
                $updated_utc
            );
            """;
        command.Parameters.AddWithValue("$conversation_id", id);
        command.Parameters.AddWithValue("$context_id", contextId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$created_utc", stamp);
        command.Parameters.AddWithValue("$updated_utc", stamp);
        command.ExecuteNonQuery();

        return new ChatConversation(id, contextId, title, now, now);
    }

    internal ChatConversation? GetConversation(
        string contextId,
        string conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                conversation_id,
                context_id,
                title,
                created_utc,
                updated_utc
            FROM chat_conversation
            WHERE context_id = $context_id
              AND conversation_id = $conversation_id;
            """;
        command.Parameters.AddWithValue("$context_id", contextId);
        command.Parameters.AddWithValue("$conversation_id", conversationId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ChatConversation(
            ConversationId: reader.GetString(0),
            ContextId: reader.GetString(1),
            Title: reader.GetString(2),
            CreatedAt: ParseTimestamp(reader.GetString(3)),
            UpdatedAt: ParseTimestamp(reader.GetString(4)));
    }

    internal IReadOnlyList<ChatMessage> GetMessages(
        string contextId,
        string conversationId)
    {
        var result = new List<ChatMessage>();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                m.sequence,
                m.conversation_id,
                m.role,
                m.content,
                m.tool_name,
                m.tool_call_id,
                m.tool_arguments_json,
                m.created_utc
            FROM chat_message AS m
            INNER JOIN chat_conversation AS c
                ON c.conversation_id = m.conversation_id
            WHERE c.context_id = $context_id
              AND c.conversation_id = $conversation_id
            ORDER BY m.sequence;
            """;
        command.Parameters.AddWithValue("$context_id", contextId);
        command.Parameters.AddWithValue("$conversation_id", conversationId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ChatMessage(
                Sequence: reader.GetInt64(0),
                ConversationId: reader.GetString(1),
                Role: reader.GetString(2),
                Content: reader.GetString(3),
                ToolName: reader.IsDBNull(4) ? null : reader.GetString(4),
                ToolCallId: reader.IsDBNull(5) ? null : reader.GetString(5),
                ToolArgumentsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt: ParseTimestamp(reader.GetString(7))));
        }

        return result;
    }

    internal void AppendMessage(
        string contextId,
        string conversationId,
        string role,
        string content,
        string? toolName = null,
        string? toolCallId = null,
        string? toolArgumentsJson = null)
    {
        if (role is not ("user" or "assistant" or "tool"))
            throw new ArgumentException("Unsupported chat role.", nameof(role));

        content ??= string.Empty;
        if (content.Length > 500_000)
            throw new ArgumentOutOfRangeException(nameof(content), "Message is too large.");

        if (toolArgumentsJson?.Length > 100_000)
            throw new ArgumentOutOfRangeException(nameof(toolArgumentsJson), "Tool arguments are too large.");

        var stamp = DateTimeOffset.UtcNow.ToString(
            "O",
            CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText =
                """
                SELECT COUNT(*)
                FROM chat_conversation
                WHERE conversation_id = $conversation_id
                  AND context_id = $context_id;
                """;
            ownership.Parameters.AddWithValue("$conversation_id", conversationId);
            ownership.Parameters.AddWithValue("$context_id", contextId);

            if (Convert.ToInt32(
                    ownership.ExecuteScalar(),
                    CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "Conversation does not belong to the active chat context.");
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO chat_message
                (
                    message_id,
                    conversation_id,
                    role,
                    content,
                    tool_name,
                    tool_call_id,
                    tool_arguments_json,
                    created_utc
                )
                VALUES
                (
                    $message_id,
                    $conversation_id,
                    $role,
                    $content,
                    $tool_name,
                    $tool_call_id,
                    $tool_arguments_json,
                    $created_utc
                );
                """;
            insert.Parameters.AddWithValue("$message_id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$conversation_id", conversationId);
            insert.Parameters.AddWithValue("$role", role);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$tool_name", (object?)toolName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tool_call_id", (object?)toolCallId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tool_arguments_json", (object?)toolArgumentsJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created_utc", stamp);
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE chat_conversation
                SET updated_utc = $updated_utc
                WHERE conversation_id = $conversation_id
                  AND context_id = $context_id;
                """;
            update.Parameters.AddWithValue("$updated_utc", stamp);
            update.Parameters.AddWithValue("$conversation_id", conversationId);
            update.Parameters.AddWithValue("$context_id", contextId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal void RenameConversation(
        string contextId,
        string conversationId,
        string title)
    {
        title = NormalizeTitle(title);
        var stamp = DateTimeOffset.UtcNow.ToString(
            "O",
            CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE chat_conversation
            SET title = $title,
                updated_utc = $updated_utc
            WHERE conversation_id = $conversation_id
              AND context_id = $context_id;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$updated_utc", stamp);
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$context_id", contextId);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Conversation was not found in the active context.");
    }

    internal void DeleteConversation(
        string contextId,
        string conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM chat_conversation
            WHERE conversation_id = $conversation_id
              AND context_id = $context_id;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$context_id", contextId);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string NormalizeTitle(string title)
    {
        title = (title ?? string.Empty).Trim();
        if (title.Length == 0)
            title = "New chat";
        if (title.Length > 160)
            title = title[..160];
        return title;
    }
}
