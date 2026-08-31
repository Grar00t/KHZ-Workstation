using Microsoft.Data.Sqlite;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KHZ.App.Trust;

internal sealed class TrustStore
{
    private const int CurrentSchemaVersion = 1;

    public string DatabasePath { get; }

    public string IntegrityStatus { get; private set; } = "UNINITIALIZED";

    public TrustStore()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "state"
        );

        DatabasePath = Path.Combine(
            stateDirectory,
            "khz.db"
        );
    }

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException(
                "KHZ state directory could not be resolved.");

        Directory.CreateDirectory(directory);

        var existed = File.Exists(DatabasePath);
        var previousVersion = 0;

        if (existed)
        {
            using var preflight = OpenConnection();
            ConfigureConnection(preflight);

            previousVersion = Convert.ToInt32(
                ExecuteScalar(
                    preflight,
                    "PRAGMA user_version;"),
                CultureInfo.InvariantCulture
            );

            if (previousVersion < CurrentSchemaVersion)
            {
                ExecuteNonQuery(
                    preflight,
                    "PRAGMA wal_checkpoint(FULL);");

                preflight.Close();

                BackupBeforeMigration(previousVersion);
            }
        }

        using var connection = OpenConnection();

        ConfigureConnection(connection);

        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS activity_event
            (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id                TEXT NOT NULL UNIQUE,

                occurred_utc            TEXT NOT NULL,
                occurred_local          TEXT NOT NULL,
                timezone_id             TEXT NOT NULL,
                timezone_offset_minutes INTEGER NOT NULL,

                unix_time_ms            INTEGER NOT NULL,
                utc_ticks               INTEGER NOT NULL,

                local_year              INTEGER NOT NULL,
                local_month             INTEGER NOT NULL,
                local_day               INTEGER NOT NULL,
                local_day_of_week       TEXT NOT NULL,
                local_day_of_year       INTEGER NOT NULL,
                local_iso_week          INTEGER NOT NULL,
                local_quarter           INTEGER NOT NULL,

                local_hour              INTEGER NOT NULL,
                local_minute            INTEGER NOT NULL,
                local_second            INTEGER NOT NULL,
                local_millisecond       INTEGER NOT NULL,

                actor                   TEXT NOT NULL,
                category                TEXT NOT NULL,
                action                  TEXT NOT NULL,
                target                  TEXT,
                result                  TEXT NOT NULL,
                details_json            TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS
                ix_activity_event_time
            ON activity_event(unix_time_ms);

            CREATE INDEX IF NOT EXISTS
                ix_activity_event_category
            ON activity_event(category, action);
            """
        );

        ExecuteNonQuery(
            connection,
            $"PRAGMA user_version={CurrentSchemaVersion};"
        );

        IntegrityStatus =
            Convert.ToString(
                ExecuteScalar(
                    connection,
                    "PRAGMA integrity_check;"),
                CultureInfo.InvariantCulture
            ) ?? "UNKNOWN";

        if (!string.Equals(
                IntegrityStatus,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"KHZ state database integrity check failed: {IntegrityStatus}"
            );
        }
    }

    public void Record(
        string category,
        string action,
        string? target,
        string result,
        object? details = null,
        string actor = "local-user")
    {
        var now = DateTimeOffset.Now;

        var local = now.DateTime;
        var utc = now.UtcDateTime;

        using var connection = OpenConnection();

        ConfigureConnection(connection);

        using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO activity_event
            (
                event_id,

                occurred_utc,
                occurred_local,
                timezone_id,
                timezone_offset_minutes,

                unix_time_ms,
                utc_ticks,

                local_year,
                local_month,
                local_day,
                local_day_of_week,
                local_day_of_year,
                local_iso_week,
                local_quarter,

                local_hour,
                local_minute,
                local_second,
                local_millisecond,

                actor,
                category,
                action,
                target,
                result,
                details_json
            )
            VALUES
            (
                $event_id,

                $occurred_utc,
                $occurred_local,
                $timezone_id,
                $timezone_offset_minutes,

                $unix_time_ms,
                $utc_ticks,

                $local_year,
                $local_month,
                $local_day,
                $local_day_of_week,
                $local_day_of_year,
                $local_iso_week,
                $local_quarter,

                $local_hour,
                $local_minute,
                $local_second,
                $local_millisecond,

                $actor,
                $category,
                $action,
                $target,
                $result,
                $details_json
            );
            """;

        command.Parameters.AddWithValue(
            "$event_id",
            Guid.NewGuid().ToString("D"));

        command.Parameters.AddWithValue(
            "$occurred_utc",
            utc.ToString("O", CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue(
            "$occurred_local",
            now.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue(
            "$timezone_id",
            TimeZoneInfo.Local.Id);

        command.Parameters.AddWithValue(
            "$timezone_offset_minutes",
            (int)now.Offset.TotalMinutes);

        command.Parameters.AddWithValue(
            "$unix_time_ms",
            now.ToUnixTimeMilliseconds());

        command.Parameters.AddWithValue(
            "$utc_ticks",
            utc.Ticks);

        command.Parameters.AddWithValue(
            "$local_year",
            local.Year);

        command.Parameters.AddWithValue(
            "$local_month",
            local.Month);

        command.Parameters.AddWithValue(
            "$local_day",
            local.Day);

        command.Parameters.AddWithValue(
            "$local_day_of_week",
            local.DayOfWeek.ToString());

        command.Parameters.AddWithValue(
            "$local_day_of_year",
            local.DayOfYear);

        command.Parameters.AddWithValue(
            "$local_iso_week",
            ISOWeek.GetWeekOfYear(local));

        command.Parameters.AddWithValue(
            "$local_quarter",
            ((local.Month - 1) / 3) + 1);

        command.Parameters.AddWithValue(
            "$local_hour",
            local.Hour);

        command.Parameters.AddWithValue(
            "$local_minute",
            local.Minute);

        command.Parameters.AddWithValue(
            "$local_second",
            local.Second);

        command.Parameters.AddWithValue(
            "$local_millisecond",
            local.Millisecond);

        command.Parameters.AddWithValue(
            "$actor",
            actor);

        command.Parameters.AddWithValue(
            "$category",
            category);

        command.Parameters.AddWithValue(
            "$action",
            action);

        command.Parameters.AddWithValue(
            "$target",
            (object?)target ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$result",
            result);

        command.Parameters.AddWithValue(
            "$details_json",
            details is null
                ? "{}"
                : JsonSerializer.Serialize(details));

        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var connection =
            new SqliteConnection(
                builder.ToString());

        connection.Open();

        return connection;
    }

    private static void ConfigureConnection(
        SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            "PRAGMA foreign_keys=ON;");

        ExecuteNonQuery(
            connection,
            "PRAGMA journal_mode=WAL;");

        ExecuteNonQuery(
            connection,
            "PRAGMA synchronous=FULL;");

        ExecuteNonQuery(
            connection,
            "PRAGMA busy_timeout=5000;");
    }

    private void BackupBeforeMigration(
        int previousVersion)
    {
        if (!File.Exists(DatabasePath))
            return;

        var stamp =
            DateTimeOffset.Now.ToString(
                "yyyyMMdd-HHmmss-fff",
                CultureInfo.InvariantCulture);

        var backup =
            $"{DatabasePath}.v{previousVersion}.backup-{stamp}";

        File.Copy(
            DatabasePath,
            backup,
            overwrite: false);
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql)
    {
        using var command =
            connection.CreateCommand();

        command.CommandText = sql;

        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql)
    {
        using var command =
            connection.CreateCommand();

        command.CommandText = sql;

        command.ExecuteNonQuery();
    }
}
