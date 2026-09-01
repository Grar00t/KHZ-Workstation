using KHZ.App.Workspaces;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KHZ.App.StructuredData;

internal sealed class SqliteWorkspaceDataStore
    : IWorkspaceDataStore
{
    private const int RequiredMetadataSchemaVersion = 2;

    private static readonly Regex IdentifierPattern =
        new(
            "^[A-Za-z][A-Za-z0-9_]{0,62}$",
            RegexOptions.Compiled
            | RegexOptions.CultureInvariant);

    private readonly string _databasePath;
    private readonly string _workspaceId;
    private readonly int _manifestSchemaVersion;

    public SqliteWorkspaceDataStore(
        WorkspaceContext context)
    {
        ArgumentNullException.ThrowIfNull(
            context);

        if (string.IsNullOrWhiteSpace(
                context.MetadataDatabasePath))
        {
            throw new ArgumentException(
                "Workspace metadata database path is required.",
                nameof(context));
        }

        if (!Guid.TryParseExact(
                context.Info.WorkspaceId,
                "D",
                out _))
        {
            throw new ArgumentException(
                "Workspace ID must be a canonical GUID.",
                nameof(context));
        }

        _databasePath =
            Path.GetFullPath(
                context.MetadataDatabasePath);

        _workspaceId =
            context.Info.WorkspaceId;

        _manifestSchemaVersion =
            context.Info.SchemaVersion;

        using var connection =
            OpenConnection(
                readOnly: true);

        // OpenConnection verifies ownership and schema.
    }

    public string CreateTable(
        string name,
        IReadOnlyList<DataColumnDefinition> columns)
        => CreateTableWithRows(
            name,
            columns,
            Array.Empty<
                IReadOnlyDictionary<string, object?>>());

    public string CreateTableWithRows(
        string name,
        IReadOnlyList<DataColumnDefinition> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(
            rows);

        var normalizedName =
            NormalizeIdentifier(
                name,
                "Table name");

        var normalizedColumns =
            NormalizeColumns(
                columns);

        var tableId =
            Guid.NewGuid()
                .ToString("D");

        var sqlName =
            "data_"
            + Guid.NewGuid()
                .ToString("N");

        var createdUtc =
            DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);

        var schemaJson =
            JsonSerializer.Serialize(
                normalizedColumns
                    .Select(
                        x => new[]
                        {
                            x.Name,
                            ToSqlType(x.Type)
                        })
                    .ToArray());

        var definitions =
            string.Join(
                ", ",
                normalizedColumns.Select(
                    x =>
                        $"{QuoteIdentifier(x.Name)} {ToSqlType(x.Type)}"));

        using var connection =
            OpenConnection(
                readOnly: false);

        using var transaction =
            connection.BeginTransaction();

        try
        {
            using (var create =
                   connection.CreateCommand())
            {
                create.Transaction =
                    transaction;

                create.CommandText =
                    $"""
                    CREATE TABLE {QuoteIdentifier(sqlName)}
                    (
                        row_id TEXT PRIMARY KEY NOT NULL,
                        {definitions}
                    );
                    """;

                create.ExecuteNonQuery();
            }

            using (var catalog =
                   connection.CreateCommand())
            {
                catalog.Transaction =
                    transaction;

                catalog.CommandText =
                    """
                    INSERT INTO data_catalog
                    (
                        table_id,
                        workspace_id,
                        name,
                        sql_name,
                        schema_json,
                        created_utc
                    )
                    VALUES
                    (
                        $table_id,
                        $workspace_id,
                        $name,
                        $sql_name,
                        $schema_json,
                        $created_utc
                    );
                    """;

                catalog.Parameters.AddWithValue(
                    "$table_id",
                    tableId);

                catalog.Parameters.AddWithValue(
                    "$workspace_id",
                    _workspaceId);

                catalog.Parameters.AddWithValue(
                    "$name",
                    normalizedName);

                catalog.Parameters.AddWithValue(
                    "$sql_name",
                    sqlName);

                catalog.Parameters.AddWithValue(
                    "$schema_json",
                    schemaJson);

                catalog.Parameters.AddWithValue(
                    "$created_utc",
                    createdUtc);

                catalog.ExecuteNonQuery();
            }

            var table =
                new DataTableInfo(
                    TableId: tableId,
                    WorkspaceId: _workspaceId,
                    Name: normalizedName,
                    SqlName: sqlName,
                    Columns: normalizedColumns,
                    CreatedUtc: createdUtc);

            foreach (var values in rows)
            {
                InsertRowInTransaction(
                    connection,
                    transaction,
                    table,
                    values);
            }

            transaction.Commit();

            return tableId;
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
            }

            throw;
        }
    }

    public IReadOnlyList<DataTableInfo> ListTables()
    {
        using var connection =
            OpenConnection(
                readOnly: true);

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                table_id,
                workspace_id,
                name,
                sql_name,
                schema_json,
                created_utc
            FROM data_catalog
            WHERE workspace_id = $workspace_id
            ORDER BY name COLLATE NOCASE;
            """;

        command.Parameters.AddWithValue(
            "$workspace_id",
            _workspaceId);

        using var reader =
            command.ExecuteReader();

        var result =
            new List<DataTableInfo>();

        while (reader.Read())
        {
            result.Add(
                ReadTableInfo(
                    reader));
        }

        return result;
    }

    public string AddRow(
        string tableId,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(
            values);

        var normalizedTableId =
            NormalizeTableId(
                tableId);

        using var connection =
            OpenConnection(
                readOnly: false);

        using var transaction =
            connection.BeginTransaction();

        try
        {
            var table =
                GetTable(
                    connection,
                    transaction,
                    normalizedTableId);

            var rowId =
                InsertRowInTransaction(
                    connection,
                    transaction,
                    table,
                    values);

            transaction.Commit();

            return rowId;
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
            }

            throw;
        }
    }

    public DataQueryResult Query(
        string tableId,
        int limit = 500,
        IReadOnlyDictionary<string, object?>? filters = null,
        string? sortBy = null,
        bool descending = false)
    {
        var normalizedTableId =
            NormalizeTableId(
                tableId);

        limit =
            Math.Clamp(
                limit,
                1,
                5000);

        using var connection =
            OpenConnection(
                readOnly: true);

        var table =
            GetTable(
                connection,
                transaction: null,
                normalizedTableId);

        var columns =
            new[]
            {
                "row_id"
            }
            .Concat(
                table.Columns.Select(
                    x => x.Name))
            .ToArray();

        var dataColumns =
            table.Columns.ToDictionary(
                x => x.Name,
                StringComparer.OrdinalIgnoreCase);

        var clauses =
            new List<string>();

        var parameters =
            new List<(
                string Name,
                object Value)>();

        if (filters is not null)
        {
            var filterIndex = 0;

            foreach (var pair in filters)
            {
                var canonicalName =
                    ResolveQueryColumn(
                        pair.Key,
                        dataColumns);

                if (pair.Value is null)
                {
                    clauses.Add(
                        $"{QuoteIdentifier(canonicalName)} IS NULL");

                    continue;
                }

                var value =
                    string.Equals(
                        canonicalName,
                        "row_id",
                        StringComparison.OrdinalIgnoreCase)
                        ? NormalizeRowIdValue(
                            pair.Value)
                        : ToSqliteValue(
                            dataColumns[canonicalName].Type,
                            pair.Value);

                var parameter =
                    $"$filter_{filterIndex++}";

                clauses.Add(
                    $"{QuoteIdentifier(canonicalName)} = {parameter}");

                parameters.Add(
                    (
                        parameter,
                        value
                    ));
            }
        }

        string? canonicalSort = null;

        if (!string.IsNullOrWhiteSpace(
                sortBy))
        {
            canonicalSort =
                ResolveQueryColumn(
                    sortBy,
                    dataColumns);
        }

        var selectColumns =
            string.Join(
                ", ",
                columns.Select(
                    QuoteIdentifier));

        var sql =
            $"SELECT {selectColumns} FROM {QuoteIdentifier(table.SqlName)}";

        if (clauses.Count > 0)
        {
            sql +=
                " WHERE "
                + string.Join(
                    " AND ",
                    clauses);
        }

        if (canonicalSort is not null)
        {
            sql +=
                $" ORDER BY {QuoteIdentifier(canonicalSort)} {(descending ? "DESC" : "ASC")}";
        }

        sql +=
            " LIMIT $limit;";

        using var command =
            connection.CreateCommand();

        command.CommandText =
            sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        command.Parameters.AddWithValue(
            "$limit",
            limit);

        using var reader =
            command.ExecuteReader();

        var rows =
            new List<
                IReadOnlyDictionary<string, object?>>();

        while (reader.Read())
        {
            var row =
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase);

            for (var i = 0;
                 i < columns.Length;
                 i++)
            {
                row[columns[i]] =
                    reader.IsDBNull(i)
                        ? null
                        : reader.GetValue(i);
            }

            rows.Add(
                row);
        }

        return new DataQueryResult(
            Columns: columns,
            Rows: rows);
    }

    private static string InsertRowInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DataTableInfo table,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(
            values);

        var columns =
            table.Columns.ToDictionary(
                x => x.Name,
                StringComparer.OrdinalIgnoreCase);

        var seenColumns =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        var normalizedValues =
            new List<(
                string Name,
                object Value)>();

        foreach (var pair in values)
        {
            if (!columns.TryGetValue(
                    pair.Key,
                    out var column))
            {
                throw new ArgumentException(
                    $"Unknown data column: {pair.Key}",
                    nameof(values));
            }

            if (!seenColumns.Add(
                    column.Name))
            {
                throw new ArgumentException(
                    $"Duplicate data column value: {column.Name}",
                    nameof(values));
            }

            normalizedValues.Add(
                (
                    column.Name,
                    ToSqliteValue(
                        column.Type,
                        pair.Value)
                ));
        }

        var rowId =
            Guid.NewGuid()
                .ToString("D");

        using var insert =
            connection.CreateCommand();

        insert.Transaction =
            transaction;

        if (normalizedValues.Count == 0)
        {
            insert.CommandText =
                $"""
                INSERT INTO {QuoteIdentifier(table.SqlName)}
                (
                    row_id
                )
                VALUES
                (
                    $row_id
                );
                """;
        }
        else
        {
            var columnSql =
                string.Join(
                    ", ",
                    normalizedValues.Select(
                        x =>
                            QuoteIdentifier(
                                x.Name)));

            var parameterSql =
                string.Join(
                    ", ",
                    normalizedValues.Select(
                        (_, index) =>
                            $"$value_{index}"));

            insert.CommandText =
                $"""
                INSERT INTO {QuoteIdentifier(table.SqlName)}
                (
                    row_id,
                    {columnSql}
                )
                VALUES
                (
                    $row_id,
                    {parameterSql}
                );
                """;
        }

        insert.Parameters.AddWithValue(
            "$row_id",
            rowId);

        for (var i = 0;
             i < normalizedValues.Count;
             i++)
        {
            insert.Parameters.AddWithValue(
                $"$value_{i}",
                normalizedValues[i].Value);
        }

        insert.ExecuteNonQuery();

        return rowId;
    }

    private SqliteConnection OpenConnection(
        bool readOnly)
    {
        if (!File.Exists(
                _databasePath))
        {
            throw new FileNotFoundException(
                "Workspace metadata database was not found.",
                _databasePath);
        }

        var builder =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    _databasePath,
                Mode =
                    readOnly
                        ? SqliteOpenMode.ReadOnly
                        : SqliteOpenMode.ReadWrite,
                Cache =
                    SqliteCacheMode.Shared,
                Pooling =
                    false
            };

        var connection =
            new SqliteConnection(
                builder.ToString());

        try
        {
            connection.Open();

            ExecuteNonQuery(
                connection,
                "PRAGMA foreign_keys=ON;");

            ExecuteNonQuery(
                connection,
                "PRAGMA busy_timeout=5000;");

            VerifyMetadataContract(
                connection);

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void VerifyMetadataContract(
        SqliteConnection connection)
    {
        var metadataVersion =
            Convert.ToInt32(
                ExecuteScalar(
                    connection,
                    "PRAGMA user_version;"),
                CultureInfo.InvariantCulture);

        if (metadataVersion
            != RequiredMetadataSchemaVersion)
        {
            throw new InvalidDataException(
                $"Structured Data requires workspace metadata schema {RequiredMetadataSchemaVersion}; found {metadataVersion}.");
        }

        using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                workspace_id,
                manifest_schema_version
            FROM workspace_identity;
            """;

        using var reader =
            command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidDataException(
                "Workspace metadata identity is missing.");
        }

        var storedWorkspaceId =
            reader.GetString(0);

        var storedManifestVersion =
            reader.GetInt32(1);

        if (!string.Equals(
                storedWorkspaceId,
                _workspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Workspace metadata belongs to a different workspace_id.");
        }

        if (storedManifestVersion
            != _manifestSchemaVersion)
        {
            throw new InvalidDataException(
                "Workspace manifest schema does not match metadata identity.");
        }

        if (reader.Read())
        {
            throw new InvalidDataException(
                "Workspace metadata contains multiple identity rows.");
        }
    }

    private DataTableInfo GetTable(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableId)
    {
        using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            """
            SELECT
                table_id,
                workspace_id,
                name,
                sql_name,
                schema_json,
                created_utc
            FROM data_catalog
            WHERE table_id = $table_id
              AND workspace_id = $workspace_id;
            """;

        command.Parameters.AddWithValue(
            "$table_id",
            tableId);

        command.Parameters.AddWithValue(
            "$workspace_id",
            _workspaceId);

        using var reader =
            command.ExecuteReader();

        if (!reader.Read())
        {
            throw new KeyNotFoundException(
                "Unknown data table for this workspace.");
        }

        var table =
            ReadTableInfo(
                reader);

        if (reader.Read())
        {
            throw new InvalidDataException(
                "Duplicate data catalog identity detected.");
        }

        return table;
    }

    private DataTableInfo ReadTableInfo(
        SqliteDataReader reader)
    {
        var tableId =
            reader.GetString(0);

        var workspaceId =
            reader.GetString(1);

        var name =
            reader.GetString(2);

        var sqlName =
            reader.GetString(3);

        var schemaJson =
            reader.GetString(4);

        var createdUtc =
            reader.GetString(5);

        if (!string.Equals(
                workspaceId,
                _workspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Data catalog row belongs to a different workspace.");
        }

        if (!Guid.TryParseExact(
                tableId,
                "D",
                out _))
        {
            throw new InvalidDataException(
                "Data catalog table_id is invalid.");
        }

        if (!IdentifierPattern.IsMatch(
                sqlName)
            || !sqlName.StartsWith(
                "data_",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Data catalog sql_name is invalid.");
        }

        var rawSchema =
            JsonSerializer.Deserialize<string[][]>(
                schemaJson)
            ?? throw new InvalidDataException(
                "Data catalog schema_json is invalid.");

        var columns =
            new List<DataColumnDefinition>();

        foreach (var pair in rawSchema)
        {
            if (pair.Length != 2)
            {
                throw new InvalidDataException(
                    "Data catalog schema_json contains an invalid column definition.");
            }

            columns.Add(
                new DataColumnDefinition(
                    NormalizeIdentifier(
                        pair[0],
                        "Column name"),
                    ParseSqlType(
                        pair[1])));
        }

        var normalizedColumns =
            NormalizeColumns(
                columns);

        return new DataTableInfo(
            TableId: tableId,
            WorkspaceId: workspaceId,
            Name: name,
            SqlName: sqlName,
            Columns: normalizedColumns,
            CreatedUtc: createdUtc);
    }

    private static IReadOnlyList<DataColumnDefinition> NormalizeColumns(
        IReadOnlyList<DataColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(
            columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "At least one data column is required.",
                nameof(columns));
        }

        if (columns.Count > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columns),
                "A data table cannot contain more than 256 user columns.");
        }

        var result =
            new List<DataColumnDefinition>(
                columns.Count);

        var names =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            if (column is null)
            {
                throw new ArgumentException(
                    "Column definitions cannot contain null entries.",
                    nameof(columns));
            }

            var name =
                NormalizeIdentifier(
                    column.Name,
                    "Column name");

            if (string.Equals(
                    name,
                    "row_id",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "row_id is reserved for KHZ row identity.",
                    nameof(columns));
            }

            if (!names.Add(
                    name))
            {
                throw new ArgumentException(
                    $"Duplicate data column: {name}",
                    nameof(columns));
            }

            if (!Enum.IsDefined(
                    column.Type))
            {
                throw new ArgumentException(
                    $"Unsupported data type for column {name}.",
                    nameof(columns));
            }

            result.Add(
                new DataColumnDefinition(
                    name,
                    column.Type));
        }

        return result;
    }

    private static string NormalizeIdentifier(
        string value,
        string label)
    {
        if (string.IsNullOrWhiteSpace(
                value))
        {
            throw new ArgumentException(
                $"{label} is required.");
        }

        var normalized =
            value.Trim();

        if (!IdentifierPattern.IsMatch(
                normalized))
        {
            throw new ArgumentException(
                $"{label} must start with a letter and contain only letters, digits, or underscore; maximum length is 63.");
        }

        return normalized;
    }

    private static string NormalizeTableId(
        string tableId)
    {
        if (!Guid.TryParseExact(
                tableId,
                "D",
                out var parsed))
        {
            throw new ArgumentException(
                "Data table ID must be a canonical GUID.",
                nameof(tableId));
        }

        return parsed.ToString("D");
    }

    private static string ResolveQueryColumn(
        string name,
        IReadOnlyDictionary<string, DataColumnDefinition> dataColumns)
    {
        if (string.IsNullOrWhiteSpace(
                name))
        {
            throw new ArgumentException(
                "Query column is required.",
                nameof(name));
        }

        if (string.Equals(
                name,
                "row_id",
                StringComparison.OrdinalIgnoreCase))
        {
            return "row_id";
        }

        if (!dataColumns.TryGetValue(
                name,
                out var column))
        {
            throw new ArgumentException(
                $"Unknown query column: {name}",
                nameof(name));
        }

        return column.Name;
    }

    private static object NormalizeRowIdValue(
        object value)
    {
        if (value is not string text
            || !Guid.TryParseExact(
                text,
                "D",
                out var parsed))
        {
            throw new ArgumentException(
                "row_id filters require a canonical GUID value.");
        }

        return parsed.ToString("D");
    }

    private static object ToSqliteValue(
        StructuredDataType type,
        object? value)
    {
        if (value is null)
            return DBNull.Value;

        return type switch
        {
            StructuredDataType.Text =>
                value is string text
                    ? text
                    : throw InvalidType(
                        type,
                        value),

            StructuredDataType.Integer =>
                NormalizeInteger(
                    value),

            StructuredDataType.Real =>
                NormalizeReal(
                    value),

            StructuredDataType.Blob =>
                value is byte[] bytes
                    ? bytes
                    : throw InvalidType(
                        type,
                        value),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(type))
        };
    }

    private static object NormalizeInteger(
        object value)
        => value switch
        {
            byte x => (long)x,
            sbyte x => (long)x,
            short x => (long)x,
            ushort x => (long)x,
            int x => (long)x,
            uint x => (long)x,
            long x => x,
            ulong x when x <= long.MaxValue =>
                (long)x,
            _ =>
                throw InvalidType(
                    StructuredDataType.Integer,
                    value)
        };

    private static object NormalizeReal(
        object value)
        => value switch
        {
            byte x => (double)x,
            sbyte x => (double)x,
            short x => (double)x,
            ushort x => (double)x,
            int x => (double)x,
            uint x => (double)x,
            long x => (double)x,
            ulong x => (double)x,
            float x => (double)x,
            double x => x,
            decimal x => (double)x,
            _ =>
                throw InvalidType(
                    StructuredDataType.Real,
                    value)
        };

    private static ArgumentException InvalidType(
        StructuredDataType type,
        object value)
        => new(
            $"Value of CLR type {value.GetType().Name} is not valid for Structured Data type {type}.");

    private static string ToSqlType(
        StructuredDataType type)
        => type switch
        {
            StructuredDataType.Text =>
                "TEXT",

            StructuredDataType.Integer =>
                "INTEGER",

            StructuredDataType.Real =>
                "REAL",

            StructuredDataType.Blob =>
                "BLOB",

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(type))
        };

    private static StructuredDataType ParseSqlType(
        string type)
        => type.ToUpperInvariant() switch
        {
            "TEXT" =>
                StructuredDataType.Text,

            "INTEGER" =>
                StructuredDataType.Integer,

            "REAL" =>
                StructuredDataType.Real,

            "BLOB" =>
                StructuredDataType.Blob,

            _ =>
                throw new InvalidDataException(
                    $"Unsupported Structured Data type in schema: {type}")
        };

    private static string QuoteIdentifier(
        string identifier)
        => "\""
           + identifier.Replace(
               "\"",
               "\"\"",
               StringComparison.Ordinal)
           + "\"";

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql)
    {
        using var command =
            connection.CreateCommand();

        command.CommandText =
            sql;

        return command.ExecuteScalar();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql)
    {
        using var command =
            connection.CreateCommand();

        command.CommandText =
            sql;

        command.ExecuteNonQuery();
    }
}
