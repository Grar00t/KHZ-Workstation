using Microsoft.Data.Sqlite;
using System;
using System.Globalization;
using System.IO;

namespace KHZ.App.Settings;

internal sealed class SqliteAppSettingsStore
    : IAppSettingsStore
{
    internal const string DefaultWorkspaceFolderKey =
        "workspace.default_folder";

    private readonly string _databasePath;

    public SqliteAppSettingsStore(
        string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException(
                "Database path is required.",
                nameof(databasePath));

        _databasePath = databasePath;
    }

    public string? GetDefaultWorkspaceFolder()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT value_text
            FROM app_setting
            WHERE setting_key = $setting_key;
            """;

        command.Parameters.AddWithValue(
            "$setting_key",
            DefaultWorkspaceFolderKey);

        var value =
            command.ExecuteScalar() as string;

        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    public string SaveDefaultWorkspaceFolder(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(
                "Workspace folder is required.",
                nameof(path));

        var normalized =
            Path.GetFullPath(
                path.Trim());

        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException(
                $"Workspace folder does not exist: {normalized}");

        if (normalized.Length > 32767)
            throw new ArgumentOutOfRangeException(
                nameof(path),
                "Workspace folder path is too long.");

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
            INSERT INTO app_setting
            (
                setting_key,
                value_text,
                updated_utc,
                updated_local
            )
            VALUES
            (
                $setting_key,
                $value_text,
                $updated_utc,
                $updated_local
            )
            ON CONFLICT(setting_key)
            DO UPDATE SET
                value_text = excluded.value_text,
                updated_utc = excluded.updated_utc,
                updated_local = excluded.updated_local;
            """;

        command.Parameters.AddWithValue(
            "$setting_key",
            DefaultWorkspaceFolderKey);

        command.Parameters.AddWithValue(
            "$value_text",
            normalized);

        command.Parameters.AddWithValue(
            "$updated_utc",
            updatedUtc);

        command.Parameters.AddWithValue(
            "$updated_local",
            updatedLocal);

        command.ExecuteNonQuery();
        transaction.Commit();

        return normalized;
    }

    public void ClearDefaultWorkspaceFolder()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            DELETE FROM app_setting
            WHERE setting_key = $setting_key;
            """;

        command.Parameters.AddWithValue(
            "$setting_key",
            DefaultWorkspaceFolderKey);

        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var builder =
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            };

        var connection =
            new SqliteConnection(
                builder.ToString());

        connection.Open();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """;

        command.ExecuteNonQuery();

        return connection;
    }
}
