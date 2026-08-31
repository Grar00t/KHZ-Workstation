using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KHZ.App.Integrations;

internal sealed class SqliteIntegrationConfigStore
    : IIntegrationConfigStore
{
    private readonly string _databasePath;

    public SqliteIntegrationConfigStore(
        string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException(
                "Database path is required.",
                nameof(databasePath));

        _databasePath = databasePath;
    }

    public IntegrationConfig? Get(
        string providerId)
    {
        ValidateProviderId(providerId);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                provider_id,
                display_name,
                enabled,
                endpoint,
                port,
                database_name,
                auth_mode,
                updated_utc,
                updated_local
            FROM integration_config
            WHERE provider_id = $provider_id;
            """;

        command.Parameters.AddWithValue(
            "$provider_id",
            providerId);

        using var reader = command.ExecuteReader();

        if (!reader.Read())
            return null;

        return ReadConfig(reader);
    }

    public IReadOnlyList<IntegrationConfig> List()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                provider_id,
                display_name,
                enabled,
                endpoint,
                port,
                database_name,
                auth_mode,
                updated_utc,
                updated_local
            FROM integration_config
            ORDER BY display_name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();

        var rows =
            new List<IntegrationConfig>();

        while (reader.Read())
            rows.Add(ReadConfig(reader));

        return rows;
    }

    public IntegrationConfig Save(
        string providerId,
        string displayName,
        bool enabled,
        string? endpoint,
        int? port,
        string? databaseName,
        string authMode)
    {
        providerId = NormalizeRequired(
            providerId,
            nameof(providerId),
            64);

        displayName = NormalizeRequired(
            displayName,
            nameof(displayName),
            160);

        endpoint = NormalizeOptional(
            endpoint,
            2048);

        databaseName = NormalizeOptional(
            databaseName,
            256);

        authMode = NormalizeRequired(
            authMode,
            nameof(authMode),
            64);

        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "Port must be between 1 and 65535.");

        var now = DateTimeOffset.Now;

        var updatedUtc =
            now.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture);

        var updatedLocal =
            now.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO integration_config
            (
                provider_id,
                display_name,
                enabled,
                endpoint,
                port,
                database_name,
                auth_mode,
                updated_utc,
                updated_local
            )
            VALUES
            (
                $provider_id,
                $display_name,
                $enabled,
                $endpoint,
                $port,
                $database_name,
                $auth_mode,
                $updated_utc,
                $updated_local
            )
            ON CONFLICT(provider_id)
            DO UPDATE SET
                display_name  = excluded.display_name,
                enabled       = excluded.enabled,
                endpoint      = excluded.endpoint,
                port          = excluded.port,
                database_name = excluded.database_name,
                auth_mode     = excluded.auth_mode,
                updated_utc   = excluded.updated_utc,
                updated_local = excluded.updated_local;
            """;

        command.Parameters.AddWithValue(
            "$provider_id",
            providerId);

        command.Parameters.AddWithValue(
            "$display_name",
            displayName);

        command.Parameters.AddWithValue(
            "$enabled",
            enabled ? 1 : 0);

        command.Parameters.AddWithValue(
            "$endpoint",
            (object?)endpoint ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$port",
            (object?)port ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$database_name",
            (object?)databaseName ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$auth_mode",
            authMode);

        command.Parameters.AddWithValue(
            "$updated_utc",
            updatedUtc);

        command.Parameters.AddWithValue(
            "$updated_local",
            updatedLocal);

        command.ExecuteNonQuery();
        transaction.Commit();

        return new IntegrationConfig(
            ProviderId: providerId,
            DisplayName: displayName,
            Enabled: enabled,
            Endpoint: endpoint,
            Port: port,
            DatabaseName: databaseName,
            AuthMode: authMode,
            UpdatedUtc: updatedUtc,
            UpdatedLocal: updatedLocal);
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        };

        var connection =
            new SqliteConnection(
                builder.ToString());

        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """;

        command.ExecuteNonQuery();

        return connection;
    }

    private static IntegrationConfig ReadConfig(
        SqliteDataReader reader)
        => new(
            ProviderId: reader.GetString(0),
            DisplayName: reader.GetString(1),
            Enabled: reader.GetInt64(2) != 0,
            Endpoint:
                reader.IsDBNull(3)
                    ? null
                    : reader.GetString(3),
            Port:
                reader.IsDBNull(4)
                    ? null
                    : reader.GetInt32(4),
            DatabaseName:
                reader.IsDBNull(5)
                    ? null
                    : reader.GetString(5),
            AuthMode: reader.GetString(6),
            UpdatedUtc: reader.GetString(7),
            UpdatedLocal: reader.GetString(8));

    private static void ValidateProviderId(
        string providerId)
        => _ = NormalizeRequired(
            providerId,
            nameof(providerId),
            64);

    private static string NormalizeRequired(
        string value,
        string parameterName,
        int maxLength)
    {
        var normalized =
            value?.Trim()
            ?? string.Empty;

        if (normalized.Length == 0)
            throw new ArgumentException(
                "Value is required.",
                parameterName);

        if (normalized.Length > maxLength)
            throw new ArgumentException(
                $"Value exceeds {maxLength} characters.",
                parameterName);

        return normalized;
    }

    private static string? NormalizeOptional(
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();

        if (normalized.Length > maxLength)
            throw new ArgumentException(
                $"Value exceeds {maxLength} characters.");

        return normalized;
    }
}
