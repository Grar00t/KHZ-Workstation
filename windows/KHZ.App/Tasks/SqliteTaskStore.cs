using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KHZ.App.Tasks;

internal sealed class SqliteTaskStore : ITaskStore
{
    private readonly string _databasePath;

    public SqliteTaskStore(
        string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException(
                "Database path is required.",
                nameof(databasePath));
        }

        _databasePath = databasePath;
    }

    public TaskItem Create(
        string title,
        DateOnly? dueDate)
    {
        title = NormalizeTitle(title);

        var taskId =
            Guid.NewGuid().ToString("D");

        var now =
            DateTimeOffset.Now;

        var createdUtc =
            now.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture);

        var createdLocal =
            now.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                CultureInfo.InvariantCulture);

        using var connection =
            OpenConnection();

        using var transaction =
            connection.BeginTransaction();

        using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            INSERT INTO local_task
            (
                task_id,
                title,
                due_local_date,
                is_completed,
                created_utc,
                created_local,
                updated_utc,
                updated_local
            )
            VALUES
            (
                $task_id,
                $title,
                $due_local_date,
                0,
                $created_utc,
                $created_local,
                $updated_utc,
                $updated_local
            );
            """;

        command.Parameters.AddWithValue(
            "$task_id",
            taskId);

        command.Parameters.AddWithValue(
            "$title",
            title);

        command.Parameters.AddWithValue(
            "$due_local_date",
            dueDate is null
                ? DBNull.Value
                : dueDate.Value.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture));

        command.Parameters.AddWithValue(
            "$created_utc",
            createdUtc);

        command.Parameters.AddWithValue(
            "$created_local",
            createdLocal);

        command.Parameters.AddWithValue(
            "$updated_utc",
            createdUtc);

        command.Parameters.AddWithValue(
            "$updated_local",
            createdLocal);

        command.ExecuteNonQuery();
        transaction.Commit();

        return new TaskItem(
            TaskId: taskId,
            Title: title,
            DueDate: dueDate,
            IsCompleted: false,
            CreatedUtc: createdUtc,
            CreatedLocal: createdLocal,
            UpdatedUtc: createdUtc,
            UpdatedLocal: createdLocal);
    }

    public IReadOnlyList<TaskItem> List(
        bool includeCompleted = true)
    {
        using var connection =
            OpenConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            includeCompleted
                ? """
                  SELECT
                      task_id,
                      title,
                      due_local_date,
                      is_completed,
                      created_utc,
                      created_local,
                      updated_utc,
                      updated_local
                  FROM local_task
                  ORDER BY
                      is_completed ASC,
                      CASE
                          WHEN due_local_date IS NULL THEN 1
                          ELSE 0
                      END ASC,
                      due_local_date ASC,
                      created_utc DESC;
                  """
                : """
                  SELECT
                      task_id,
                      title,
                      due_local_date,
                      is_completed,
                      created_utc,
                      created_local,
                      updated_utc,
                      updated_local
                  FROM local_task
                  WHERE is_completed = 0
                  ORDER BY
                      CASE
                          WHEN due_local_date IS NULL THEN 1
                          ELSE 0
                      END ASC,
                      due_local_date ASC,
                      created_utc DESC;
                  """;

        using var reader =
            command.ExecuteReader();

        var rows =
            new List<TaskItem>();

        while (reader.Read())
        {
            rows.Add(
                ReadTask(reader));
        }

        return rows;
    }

    public TaskItem SetCompleted(
        string taskId,
        bool completed)
    {
        taskId =
            NormalizeTaskId(taskId);

        var now =
            DateTimeOffset.Now;

        var updatedUtc =
            now.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture);

        var updatedLocal =
            now.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                CultureInfo.InvariantCulture);

        using var connection =
            OpenConnection();

        using var transaction =
            connection.BeginTransaction();

        using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            UPDATE local_task
            SET
                is_completed = $is_completed,
                updated_utc = $updated_utc,
                updated_local = $updated_local
            WHERE task_id = $task_id;
            """;

        command.Parameters.AddWithValue(
            "$is_completed",
            completed ? 1 : 0);

        command.Parameters.AddWithValue(
            "$updated_utc",
            updatedUtc);

        command.Parameters.AddWithValue(
            "$updated_local",
            updatedLocal);

        command.Parameters.AddWithValue(
            "$task_id",
            taskId);

        var changed =
            command.ExecuteNonQuery();

        if (changed != 1)
        {
            transaction.Rollback();

            throw new InvalidOperationException(
                "Task was not found.");
        }

        using var read =
            connection.CreateCommand();

        read.Transaction =
            transaction;

        read.CommandText =
            """
            SELECT
                task_id,
                title,
                due_local_date,
                is_completed,
                created_utc,
                created_local,
                updated_utc,
                updated_local
            FROM local_task
            WHERE task_id = $task_id;
            """;

        read.Parameters.AddWithValue(
            "$task_id",
            taskId);

        using var reader =
            read.ExecuteReader();

        if (!reader.Read())
        {
            transaction.Rollback();

            throw new InvalidOperationException(
                "Updated task could not be read back.");
        }

        var task =
            ReadTask(reader);

        reader.Close();
        transaction.Commit();

        return task;
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

    private static TaskItem ReadTask(
        SqliteDataReader reader)
    {
        DateOnly? dueDate = null;

        if (!reader.IsDBNull(2))
        {
            if (!DateOnly.TryParseExact(
                    reader.GetString(2),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                throw new InvalidOperationException(
                    "Stored task due date is invalid.");
            }

            dueDate = parsed;
        }

        return new TaskItem(
            TaskId: reader.GetString(0),
            Title: reader.GetString(1),
            DueDate: dueDate,
            IsCompleted: reader.GetInt64(3) != 0,
            CreatedUtc: reader.GetString(4),
            CreatedLocal: reader.GetString(5),
            UpdatedUtc: reader.GetString(6),
            UpdatedLocal: reader.GetString(7));
    }

    private static string NormalizeTitle(
        string title)
    {
        var normalized =
            title?.Trim()
            ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Task title is required.",
                nameof(title));
        }

        if (normalized.Length > 300)
        {
            throw new ArgumentException(
                "Task title exceeds 300 characters.",
                nameof(title));
        }

        return normalized;
    }

    private static string NormalizeTaskId(
        string taskId)
    {
        var normalized =
            taskId?.Trim()
            ?? string.Empty;

        if (!Guid.TryParse(
                normalized,
                out _))
        {
            throw new ArgumentException(
                "Task ID is invalid.",
                nameof(taskId));
        }

        return normalized;
    }
}
