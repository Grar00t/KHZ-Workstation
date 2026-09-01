using Microsoft.Data.Sqlite;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KHZ.App.Workspaces;

internal sealed class WorkspaceService
{
    internal const string MetadataDirectoryName = ".khz";
    internal const string ManifestFileName = "workspace.json";
    internal const string MetadataDatabaseFileName = "metadata.db";

    private const int ManifestSchemaVersion = 1;
    private const int MetadataSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    public WorkspaceContext Create(
        string root,
        string? name = null)
    {
        var normalizedRoot =
            NormalizeRoot(root);

        Directory.CreateDirectory(
            normalizedRoot);

        RejectReparseRoot(
            normalizedRoot);

        var metadataDirectory =
            Path.Combine(
                normalizedRoot,
                MetadataDirectoryName);

        Directory.CreateDirectory(
            metadataDirectory);

        var manifestPath =
            Path.Combine(
                metadataDirectory,
                ManifestFileName);

        var metadataDatabasePath =
            Path.Combine(
                metadataDirectory,
                MetadataDatabaseFileName);

        if (File.Exists(manifestPath))
            return Open(normalizedRoot);

        if (File.Exists(metadataDatabasePath))
        {
            throw new InvalidDataException(
                "Workspace metadata database exists without workspace.json.");
        }

        var normalizedName =
            NormalizeName(
                name,
                normalizedRoot);

        var manifest =
            new WorkspaceManifest
            {
                WorkspaceId =
                    Guid.NewGuid()
                        .ToString("D"),
                Name = normalizedName,
                CreatedUtc =
                    DateTimeOffset.UtcNow.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                Classification = "INTERNAL",
                SchemaVersion = ManifestSchemaVersion
            };

        var info =
            ToWorkspaceInfo(
                normalizedRoot,
                manifest);

        var context =
            BuildContext(
                info);

        try
        {
            InitializeMetadataDatabase(
                context);

            WriteManifestAtomically(
                manifestPath,
                manifest);

            return context;
        }
        catch
        {
            DeleteDatabaseFiles(
                metadataDatabasePath);

            throw;
        }
    }

    public WorkspaceContext Open(
        string root)
    {
        var normalizedRoot =
            NormalizeRoot(root);

        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(
                $"Workspace folder does not exist: {normalizedRoot}");
        }

        RejectReparseRoot(
            normalizedRoot);

        var manifestPath =
            Path.Combine(
                normalizedRoot,
                MetadataDirectoryName,
                ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Not a KHZ workspace. workspace.json was not found.",
                manifestPath);
        }

        var manifest =
            ReadManifest(
                manifestPath);

        ValidateManifest(
            manifest);

        var info =
            ToWorkspaceInfo(
                normalizedRoot,
                manifest);

        var context =
            BuildContext(
                info);

        InitializeMetadataDatabase(
            context);

        return context;
    }

    public bool IsWorkspace(
        string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            var normalizedRoot =
                NormalizeRoot(
                    root);

            return File.Exists(
                Path.Combine(
                    normalizedRoot,
                    MetadataDirectoryName,
                    ManifestFileName));
        }
        catch
        {
            return false;
        }
    }

    private static WorkspaceContext BuildContext(
        WorkspaceInfo info)
    {
        var metadataDirectory =
            Path.Combine(
                info.Root,
                MetadataDirectoryName);

        return new WorkspaceContext(
            Info: info,
            MetadataDirectory: metadataDirectory,
            ManifestPath:
                Path.Combine(
                    metadataDirectory,
                    ManifestFileName),
            MetadataDatabasePath:
                Path.Combine(
                    metadataDirectory,
                    MetadataDatabaseFileName));
    }

    private static WorkspaceInfo ToWorkspaceInfo(
        string root,
        WorkspaceManifest manifest)
        => new(
            WorkspaceId: manifest.WorkspaceId,
            Name: manifest.Name,
            Root: root,
            CreatedUtc: manifest.CreatedUtc,
            Classification: manifest.Classification,
            SchemaVersion: manifest.SchemaVersion);

    private static WorkspaceManifest ReadManifest(
        string manifestPath)
    {
        try
        {
            var json =
                File.ReadAllText(
                    manifestPath);

            return
                JsonSerializer.Deserialize<WorkspaceManifest>(
                    json)
                ?? throw new InvalidDataException(
                    "workspace.json is empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "workspace.json is not valid JSON.",
                ex);
        }
    }

    private static void ValidateManifest(
        WorkspaceManifest manifest)
    {
        if (!Guid.TryParseExact(
                manifest.WorkspaceId,
                "D",
                out _))
        {
            throw new InvalidDataException(
                "workspace_id must be a canonical GUID.");
        }

        if (string.IsNullOrWhiteSpace(
                manifest.Name))
        {
            throw new InvalidDataException(
                "Workspace name is required.");
        }

        if (!DateTimeOffset.TryParse(
                manifest.CreatedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new InvalidDataException(
                "created_utc is invalid.");
        }

        if (string.IsNullOrWhiteSpace(
                manifest.Classification))
        {
            throw new InvalidDataException(
                "Workspace classification is required.");
        }

        if (manifest.SchemaVersion
            != ManifestSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported workspace manifest schema {manifest.SchemaVersion}.");
        }
    }

    private static void InitializeMetadataDatabase(
        WorkspaceContext context)
    {
        Directory.CreateDirectory(
            context.MetadataDirectory);

        var builder =
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    context.MetadataDatabasePath,
                Mode =
                    SqliteOpenMode.ReadWriteCreate,
                Cache =
                    SqliteCacheMode.Shared,
                Pooling = false
            };

        using var connection =
            new SqliteConnection(
                builder.ToString());

        connection.Open();

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

        using var transaction =
            connection.BeginTransaction();

        using (var schema =
               connection.CreateCommand())
        {
            schema.Transaction =
                transaction;

            schema.CommandText =
                """
                CREATE TABLE IF NOT EXISTS workspace_identity
                (
                    workspace_id            TEXT PRIMARY KEY NOT NULL,
                    manifest_schema_version INTEGER NOT NULL,
                    created_utc             TEXT NOT NULL
                );
                """;

            schema.ExecuteNonQuery();
        }

        using (var read =
               connection.CreateCommand())
        {
            read.Transaction =
                transaction;

            read.CommandText =
                """
                SELECT
                    workspace_id,
                    manifest_schema_version,
                    created_utc
                FROM workspace_identity;
                """;

            using var reader =
                read.ExecuteReader();

            if (reader.Read())
            {
                var existingWorkspaceId =
                    reader.GetString(0);

                var existingSchemaVersion =
                    reader.GetInt32(1);

                if (!string.Equals(
                        existingWorkspaceId,
                        context.Info.WorkspaceId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "metadata.db belongs to a different workspace_id.");
                }

                if (existingSchemaVersion
                    != context.Info.SchemaVersion)
                {
                    throw new InvalidDataException(
                        "metadata.db workspace schema does not match workspace.json.");
                }

                if (reader.Read())
                {
                    throw new InvalidDataException(
                        "metadata.db contains multiple workspace identities.");
                }
            }
            else
            {
                reader.Close();

                using var insert =
                    connection.CreateCommand();

                insert.Transaction =
                    transaction;

                insert.CommandText =
                    """
                    INSERT INTO workspace_identity
                    (
                        workspace_id,
                        manifest_schema_version,
                        created_utc
                    )
                    VALUES
                    (
                        $workspace_id,
                        $manifest_schema_version,
                        $created_utc
                    );
                    """;

                insert.Parameters.AddWithValue(
                    "$workspace_id",
                    context.Info.WorkspaceId);

                insert.Parameters.AddWithValue(
                    "$manifest_schema_version",
                    context.Info.SchemaVersion);

                insert.Parameters.AddWithValue(
                    "$created_utc",
                    context.Info.CreatedUtc);

                insert.ExecuteNonQuery();
            }
        }

        using (var version =
               connection.CreateCommand())
        {
            version.Transaction =
                transaction;

            version.CommandText =
                $"PRAGMA user_version={MetadataSchemaVersion};";

            version.ExecuteNonQuery();
        }

        transaction.Commit();

        var integrity =
            Convert.ToString(
                ExecuteScalar(
                    connection,
                    "PRAGMA integrity_check;"),
                CultureInfo.InvariantCulture);

        if (!string.Equals(
                integrity,
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Workspace metadata database integrity check failed: {integrity}");
        }
    }

    private static void WriteManifestAtomically(
        string manifestPath,
        WorkspaceManifest manifest)
    {
        var temporaryPath =
            manifestPath
            + ".tmp-"
            + Guid.NewGuid().ToString("N");

        try
        {
            var bytes =
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    JsonOptions);

            using (var stream =
                   new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                stream.Write(
                    bytes);

                stream.Flush(
                    flushToDisk: true);
            }

            File.Move(
                temporaryPath,
                manifestPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(
                    temporaryPath);
            }
        }
    }

    private static string NormalizeRoot(
        string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException(
                "Workspace root is required.",
                nameof(root));
        }

        var normalized =
            Path.GetFullPath(
                root.Trim());

        RejectReservedMetadataPath(
            normalized);

        return normalized;
    }

    private static void RejectReservedMetadataPath(
        string root)
    {
        DirectoryInfo? current =
            new(root);

        while (current is not null)
        {
            if (string.Equals(
                    current.Name,
                    MetadataDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The reserved .khz metadata directory cannot be used as or contained within a KHZ workspace root.");
            }

            current = current.Parent;
        }
    }

    private static string NormalizeName(
        string? name,
        string root)
    {
        var normalized =
            string.IsNullOrWhiteSpace(name)
                ? new DirectoryInfo(root).Name
                : name.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = root;

        if (normalized.Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                "Workspace name is too long.");
        }

        return normalized;
    }

    private static void RejectReparseRoot(
        string root)
    {
        var attributes =
            File.GetAttributes(
                root);

        if ((attributes
             & FileAttributes.ReparsePoint)
            != 0)
        {
            throw new InvalidDataException(
                "Workspace root cannot be a symlink or reparse point.");
        }
    }

    private static void DeleteDatabaseFiles(
        string databasePath)
    {
        foreach (var path in new[]
                 {
                     databasePath,
                     databasePath + "-wal",
                     databasePath + "-shm"
                 })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
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

    private sealed class WorkspaceManifest
    {
        [JsonPropertyName("workspace_id")]
        public string WorkspaceId { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("created_utc")]
        public string CreatedUtc { get; init; } = "";

        [JsonPropertyName("classification")]
        public string Classification { get; init; } = "";

        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }
    }
}
