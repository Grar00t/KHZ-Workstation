using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace KHZ.AssetRegister;

public sealed class AssetStore
{
    public AssetStore(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KHZ",
            "AssetRegister",
            "assets.sqlite");

        DatabaseDirectory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("Database directory could not be resolved.");

        Directory.CreateDirectory(DatabaseDirectory);
        Initialize();
    }

    public string DatabasePath { get; }

    public string DatabaseDirectory { get; }

    public IReadOnlyList<AssetRecord> LoadAssets()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                AssetId,
                AssetTag,
                SerialNumber,
                Barcode,
                Category,
                Description,
                Manufacturer,
                Model,
                Location,
                Department,
                Custodian,
                Status,
                PurchaseDate,
                PurchaseCost,
                WarrantyExpiry,
                Condition,
                Notes,
                CreatedAt
            FROM assets
            ORDER BY AssetId DESC;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<AssetRecord>();

        while (reader.Read())
        {
            var asset = new AssetRecord
            {
                AssetId = reader.GetInt64(0),
                AssetTag = GetString(reader, 1),
                SerialNumber = GetString(reader, 2),
                Barcode = GetString(reader, 3),
                Category = GetString(reader, 4),
                Description = GetString(reader, 5),
                Manufacturer = GetString(reader, 6),
                Model = GetString(reader, 7),
                Location = GetString(reader, 8),
                Department = GetString(reader, 9),
                Custodian = GetString(reader, 10),
                Status = GetString(reader, 11),
                PurchaseDate = GetString(reader, 12),
                PurchaseCost = GetDecimal(reader, 13),
                WarrantyExpiry = GetString(reader, 14),
                Condition = GetString(reader, 15),
                Notes = GetString(reader, 16),
                CreatedAt = GetString(reader, 17)
            };

            asset.MarkClean();
            items.Add(asset);
        }

        return items;
    }

    public void SaveChanges(IEnumerable<AssetRecord> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var items = assets as IReadOnlyList<AssetRecord> ?? assets.ToArray();
        var dirtyItems = items
            .Where(x => x.IsDirty || x.AssetId <= 0)
            .ToArray();

        if (dirtyItems.Length == 0)
            return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var inserted = new List<PendingInsert>();

        try
        {
            foreach (var asset in dirtyItems)
            {
                Validate(asset);
                EnsureAssetTagAvailable(connection, transaction, asset);

                if (asset.AssetId <= 0)
                {
                    var result = Insert(connection, transaction, asset);
                    inserted.Add(new PendingInsert(asset, result.AssetId, result.CreatedAt));
                }
                else
                {
                    Update(connection, transaction, asset);
                }
            }

            transaction.Commit();
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

        foreach (var pending in inserted)
        {
            pending.Asset.AssetId = pending.AssetId;
            pending.Asset.CreatedAt = pending.CreatedAt;
        }

        foreach (var asset in items)
            asset.MarkClean();
    }

    public void Delete(long assetId)
        => DeleteMany(new[] { assetId });

    public void DeleteMany(IEnumerable<long> assetIds)
    {
        ArgumentNullException.ThrowIfNull(assetIds);

        var ids = assetIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
            return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = "DELETE FROM assets WHERE AssetId = @id;";

        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "@id";
        command.Parameters.Add(idParameter);

        try
        {
            foreach (var id in ids)
            {
                idParameter.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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

    public IReadOnlyList<AuditRecord> LoadAudit(int limit = 1000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                AuditId,
                AssetId,
                AssetTag,
                Action,
                ChangedAt,
                OldStatus,
                NewStatus,
                OldCustodian,
                NewCustodian,
                OldLocation,
                NewLocation
            FROM audit_log
            ORDER BY AuditId DESC
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 10000));

        using var reader = command.ExecuteReader();
        var rows = new List<AuditRecord>();

        while (reader.Read())
        {
            rows.Add(new AuditRecord(
                AuditId: reader.GetInt64(0),
                AssetId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                AssetTag: GetString(reader, 2),
                Action: GetString(reader, 3),
                ChangedAt: GetString(reader, 4),
                OldStatus: GetString(reader, 5),
                NewStatus: GetString(reader, 6),
                OldCustodian: GetString(reader, 7),
                NewCustodian: GetString(reader, 8),
                OldLocation: GetString(reader, 9),
                NewLocation: GetString(reader, 10)));
        }

        return rows;
    }

    public string IntegrityCheck()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
    }

    public long CountAssets()
        => ExecuteCount("SELECT COUNT(*) FROM assets;");

    public long CountAuditRows()
        => ExecuteCount("SELECT COUNT(*) FROM audit_log;");

    public string CreateBackup()
    {
        var backupDirectory = Path.Combine(DatabaseDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);

        var backupPath = Path.Combine(
            backupDirectory,
            $"assets-{DateTime.Now:yyyyMMdd-HHmmss}.sqlite");

        using var source = OpenConnection();
        using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());

        destination.Open();
        source.BackupDatabase(destination);

        return backupPath;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS assets
            (
                AssetId INTEGER PRIMARY KEY AUTOINCREMENT,
                AssetTag TEXT NOT NULL UNIQUE,
                SerialNumber TEXT NOT NULL DEFAULT '',
                Barcode TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                Manufacturer TEXT NOT NULL DEFAULT '',
                Model TEXT NOT NULL DEFAULT '',
                Location TEXT NOT NULL DEFAULT '',
                Department TEXT NOT NULL DEFAULT '',
                Custodian TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT 'In Service',
                PurchaseDate TEXT NOT NULL DEFAULT '',
                PurchaseCost REAL NOT NULL DEFAULT 0,
                WarrantyExpiry TEXT NOT NULL DEFAULT '',
                Condition TEXT NOT NULL DEFAULT '',
                Notes TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS audit_log
            (
                AuditId INTEGER PRIMARY KEY AUTOINCREMENT,
                AssetId INTEGER,
                AssetTag TEXT NOT NULL DEFAULT '',
                Action TEXT NOT NULL,
                ChangedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                OldStatus TEXT NOT NULL DEFAULT '',
                NewStatus TEXT NOT NULL DEFAULT '',
                OldCustodian TEXT NOT NULL DEFAULT '',
                NewCustodian TEXT NOT NULL DEFAULT '',
                OldLocation TEXT NOT NULL DEFAULT '',
                NewLocation TEXT NOT NULL DEFAULT ''
            );
            """);

        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_tag ON assets(AssetTag);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_serial ON assets(SerialNumber);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_barcode ON assets(Barcode);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_category ON assets(Category);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_location ON assets(Location);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_department ON assets(Department);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_custodian ON assets(Custodian);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_assets_status ON assets(Status);");

        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS trg_assets_insert
            AFTER INSERT ON assets
            BEGIN
                INSERT INTO audit_log
                (
                    AssetId,
                    AssetTag,
                    Action,
                    NewStatus,
                    NewCustodian,
                    NewLocation
                )
                VALUES
                (
                    NEW.AssetId,
                    NEW.AssetTag,
                    'INSERT',
                    NEW.Status,
                    NEW.Custodian,
                    NEW.Location
                );
            END;
            """);

        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS trg_assets_update
            AFTER UPDATE ON assets
            BEGIN
                INSERT INTO audit_log
                (
                    AssetId,
                    AssetTag,
                    Action,
                    OldStatus,
                    NewStatus,
                    OldCustodian,
                    NewCustodian,
                    OldLocation,
                    NewLocation
                )
                VALUES
                (
                    NEW.AssetId,
                    NEW.AssetTag,
                    'UPDATE',
                    OLD.Status,
                    NEW.Status,
                    OLD.Custodian,
                    NEW.Custodian,
                    OLD.Location,
                    NEW.Location
                );
            END;
            """);

        Execute(connection, """
            CREATE TRIGGER IF NOT EXISTS trg_assets_delete
            AFTER DELETE ON assets
            BEGIN
                INSERT INTO audit_log
                (
                    AssetId,
                    AssetTag,
                    Action,
                    OldStatus,
                    OldCustodian,
                    OldLocation
                )
                VALUES
                (
                    OLD.AssetId,
                    OLD.AssetTag,
                    'DELETE',
                    OLD.Status,
                    OLD.Custodian,
                    OLD.Location
                );
            END;
            """);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;
        command.ExecuteNonQuery();

        return connection;
    }

    private static InsertResult Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetRecord asset)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO assets
                (
                    AssetTag,
                    SerialNumber,
                    Barcode,
                    Category,
                    Description,
                    Manufacturer,
                    Model,
                    Location,
                    Department,
                    Custodian,
                    Status,
                    PurchaseDate,
                    PurchaseCost,
                    WarrantyExpiry,
                    Condition,
                    Notes
                )
                VALUES
                (
                    @assetTag,
                    @serialNumber,
                    @barcode,
                    @category,
                    @description,
                    @manufacturer,
                    @model,
                    @location,
                    @department,
                    @custodian,
                    @status,
                    @purchaseDate,
                    @purchaseCost,
                    @warrantyExpiry,
                    @condition,
                    @notes
                );
                """;

            Bind(command, asset);
            command.ExecuteNonQuery();
        }

        long assetId;

        using (var idCommand = connection.CreateCommand())
        {
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT last_insert_rowid();";
            assetId = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var createdAt = ReadCreatedAt(connection, transaction, assetId);
        return new InsertResult(assetId, createdAt);
    }

    private static void Update(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetRecord asset)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE assets SET
                AssetTag = @assetTag,
                SerialNumber = @serialNumber,
                Barcode = @barcode,
                Category = @category,
                Description = @description,
                Manufacturer = @manufacturer,
                Model = @model,
                Location = @location,
                Department = @department,
                Custodian = @custodian,
                Status = @status,
                PurchaseDate = @purchaseDate,
                PurchaseCost = @purchaseCost,
                WarrantyExpiry = @warrantyExpiry,
                Condition = @condition,
                Notes = @notes
            WHERE AssetId = @assetId;
            """;

        Bind(command, asset);
        command.Parameters.AddWithValue("@assetId", asset.AssetId);

        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Asset {asset.AssetId} no longer exists.");
    }

    private static void EnsureAssetTagAvailable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetRecord asset)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT AssetId
            FROM assets
            WHERE AssetTag = @assetTag COLLATE NOCASE
              AND AssetId <> @assetId
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("@assetTag", asset.AssetTag.Trim());
        command.Parameters.AddWithValue("@assetId", asset.AssetId);

        var existing = command.ExecuteScalar();
        if (existing is null || existing is DBNull)
            return;

        throw new InvalidOperationException(
            $"AssetTag '{asset.AssetTag.Trim()}' already exists in the local database.");
    }

    private static void Bind(SqliteCommand command, AssetRecord asset)
    {
        command.Parameters.AddWithValue("@assetTag", asset.AssetTag.Trim());
        command.Parameters.AddWithValue("@serialNumber", asset.SerialNumber.Trim());
        command.Parameters.AddWithValue("@barcode", asset.Barcode.Trim());
        command.Parameters.AddWithValue("@category", asset.Category.Trim());
        command.Parameters.AddWithValue("@description", asset.Description.Trim());
        command.Parameters.AddWithValue("@manufacturer", asset.Manufacturer.Trim());
        command.Parameters.AddWithValue("@model", asset.Model.Trim());
        command.Parameters.AddWithValue("@location", asset.Location.Trim());
        command.Parameters.AddWithValue("@department", asset.Department.Trim());
        command.Parameters.AddWithValue("@custodian", asset.Custodian.Trim());
        command.Parameters.AddWithValue("@status", asset.Status.Trim());
        command.Parameters.AddWithValue("@purchaseDate", asset.PurchaseDate.Trim());
        command.Parameters.AddWithValue("@purchaseCost", asset.PurchaseCost);
        command.Parameters.AddWithValue("@warrantyExpiry", asset.WarrantyExpiry.Trim());
        command.Parameters.AddWithValue("@condition", asset.Condition.Trim());
        command.Parameters.AddWithValue("@notes", asset.Notes.Trim());
    }

    private static string ReadCreatedAt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long assetId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CreatedAt FROM assets WHERE AssetId = @id;";
        command.Parameters.AddWithValue("@id", assetId);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Validate(AssetRecord asset)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetTag))
            throw new InvalidOperationException("AssetTag is required before saving.");

        if (asset.PurchaseCost < 0)
            throw new InvalidOperationException($"Asset {asset.AssetTag}: PurchaseCost cannot be negative.");
    }

    private long ExecuteCount(string sql)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string GetString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal GetDecimal(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? 0m
            : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private sealed record InsertResult(long AssetId, string CreatedAt);

    private sealed record PendingInsert(
        AssetRecord Asset,
        long AssetId,
        string CreatedAt);
}

public sealed record AuditRecord(
    long AuditId,
    long? AssetId,
    string AssetTag,
    string Action,
    string ChangedAt,
    string OldStatus,
    string NewStatus,
    string OldCustodian,
    string NewCustodian,
    string OldLocation,
    string NewLocation);
