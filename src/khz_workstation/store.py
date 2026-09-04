from __future__ import annotations

import json
import re
import sqlite3
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator
from uuid import uuid4

from .models import Classification, utc_now

_IDENT = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,62}$")
_ALLOWED_TYPES = {"TEXT", "INTEGER", "REAL", "BLOB"}
_LIKE_ESCAPE = "\\"

# windows/KHZ.App/Workspaces/WorkspaceService.cs is the reference
# implementation of the .khz/metadata.db contract, and
# windows/KHZ.App/StructuredData/SqliteWorkspaceDataStore.cs refuses to open a
# workspace that does not satisfy it. The DDL below is a copy of the host
# statements, so a database created by this package satisfies the contract on
# its own instead of depending on a C# process having opened it first.
METADATA_SCHEMA_VERSION = 2

_WORKSPACE_IDENTITY_DDL = (
    "CREATE TABLE IF NOT EXISTS workspace_identity("
    "workspace_id TEXT PRIMARY KEY NOT NULL, "
    "manifest_schema_version INTEGER NOT NULL, "
    "created_utc TEXT NOT NULL)"
)

_DATA_CATALOG_DDL = (
    "CREATE TABLE {table}("
    "table_id TEXT PRIMARY KEY NOT NULL, "
    "workspace_id TEXT NOT NULL, "
    "name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 160), "
    "sql_name TEXT NOT NULL UNIQUE CHECK(length(trim(sql_name)) BETWEEN 1 AND 80), "
    "schema_json TEXT NOT NULL, "
    "created_utc TEXT NOT NULL, "
    "UNIQUE(workspace_id, name), "
    "FOREIGN KEY(workspace_id) REFERENCES workspace_identity(workspace_id) "
    "ON DELETE CASCADE)"
)

_CATALOG_COLUMNS = "table_id, workspace_id, name, sql_name, schema_json, created_utc"

_BASE_TABLES = (
    "CREATE TABLE IF NOT EXISTS schema_meta(version INTEGER NOT NULL)",
    "INSERT INTO schema_meta(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_meta)",
    "CREATE TABLE IF NOT EXISTS items("
    "item_id TEXT PRIMARY KEY, "
    "workspace_id TEXT NOT NULL, "
    "relative_path TEXT NOT NULL, "
    "kind TEXT NOT NULL, "
    "sha256 TEXT, "
    "classification TEXT NOT NULL, "
    "created_utc TEXT NOT NULL, "
    "updated_utc TEXT NOT NULL, "
    "UNIQUE(workspace_id, relative_path))",
    "CREATE TABLE IF NOT EXISTS tasks("
    "task_id TEXT PRIMARY KEY, "
    "workspace_id TEXT NOT NULL, "
    "state TEXT NOT NULL, "
    "title TEXT NOT NULL, "
    "details TEXT NOT NULL, "
    "created_utc TEXT NOT NULL, "
    "updated_utc TEXT NOT NULL)",
)


def _like_prefix(prefix: str) -> str:
    escaped = prefix
    for char in (_LIKE_ESCAPE, "%", "_"):
        escaped = escaped.replace(char, _LIKE_ESCAPE + char)
    return escaped + "%"


def _has_host_constraints(sql: str) -> bool:
    normalized = " ".join(sql.split()).lower()
    return (
        "check(length(trim(name))" in normalized
        and "check(length(trim(sql_name))" in normalized
        and "references workspace_identity" in normalized
    )


class WorkspaceStore:
    SCHEMA_VERSION = 1
    METADATA_SCHEMA_VERSION = METADATA_SCHEMA_VERSION

    def __init__(
        self,
        db_path: Path,
        workspace_id: str,
        *,
        manifest_schema_version: int = 1,
        created_utc: str | None = None,
    ) -> None:
        self.db_path = db_path
        self.workspace_id = workspace_id
        self.manifest_schema_version = int(manifest_schema_version)
        self.created_utc = created_utc or utc_now()
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._migrate()

    def connect(self) -> sqlite3.Connection:
        con = sqlite3.connect(self.db_path)
        con.row_factory = sqlite3.Row
        con.execute("PRAGMA foreign_keys=ON")
        con.execute("PRAGMA journal_mode=WAL")
        con.execute("PRAGMA synchronous=FULL")
        con.execute("PRAGMA busy_timeout=5000")
        return con

    @contextmanager
    def connection(self) -> Iterator[sqlite3.Connection]:
        con = self.connect()
        try:
            yield con
        finally:
            con.close()

    def _migrate(self) -> None:
        con = sqlite3.connect(self.db_path)
        con.isolation_level = None
        try:
            con.execute("PRAGMA journal_mode=WAL")
            con.execute("PRAGMA synchronous=FULL")
            con.execute("PRAGMA busy_timeout=5000")
            existing = int(con.execute("PRAGMA user_version").fetchone()[0])
            if existing > METADATA_SCHEMA_VERSION:
                raise RuntimeError(
                    "Workspace metadata schema "
                    + str(existing)
                    + " is newer than supported schema "
                    + str(METADATA_SCHEMA_VERSION)
                    + "."
                )
            con.execute("BEGIN IMMEDIATE")
            try:
                for statement in _BASE_TABLES:
                    con.execute(statement)
                con.execute(_WORKSPACE_IDENTITY_DDL)
                self._ensure_identity(con)
                self._ensure_data_catalog(con)
                con.execute("PRAGMA user_version=" + str(METADATA_SCHEMA_VERSION))
                con.execute("COMMIT")
            except Exception:
                con.execute("ROLLBACK")
                raise
            if con.execute("PRAGMA foreign_key_check").fetchall():
                raise RuntimeError(
                    "Workspace metadata database failed foreign key validation."
                )
            integrity = con.execute("PRAGMA integrity_check").fetchone()[0]
            if integrity != "ok":
                raise RuntimeError(
                    "Workspace metadata database integrity check failed: " + str(integrity)
                )
        finally:
            con.close()

    def _ensure_identity(self, con: sqlite3.Connection) -> None:
        rows = con.execute(
            "SELECT workspace_id, manifest_schema_version FROM workspace_identity"
        ).fetchall()
        if len(rows) > 1:
            raise RuntimeError("metadata.db contains multiple workspace identities.")
        if rows:
            if rows[0][0] != self.workspace_id:
                raise RuntimeError("metadata.db belongs to a different workspace_id.")
            if int(rows[0][1]) != self.manifest_schema_version:
                raise RuntimeError(
                    "metadata.db workspace schema does not match workspace.json."
                )
            return
        con.execute(
            "INSERT INTO workspace_identity(workspace_id, manifest_schema_version, created_utc) "
            "VALUES(?,?,?)",
            (self.workspace_id, self.manifest_schema_version, self.created_utc),
        )

    def _ensure_data_catalog(self, con: sqlite3.Connection) -> None:
        row = con.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='data_catalog'"
        ).fetchone()
        if row is None:
            con.execute(_DATA_CATALOG_DDL.format(table='"data_catalog"'))
            return
        if _has_host_constraints(row[0] or ""):
            return
        # SQLite cannot add a CHECK or a foreign key to an existing table, so the
        # weak catalog is rebuilt. The whole rebuild runs inside the migration
        # transaction, so an interrupted upgrade leaves the original table.
        con.execute(_DATA_CATALOG_DDL.format(table='"data_catalog_upgraded"'))
        try:
            con.execute(
                'INSERT INTO "data_catalog_upgraded"('
                + _CATALOG_COLUMNS
                + ") SELECT "
                + _CATALOG_COLUMNS
                + ' FROM "data_catalog"'
            )
        except sqlite3.IntegrityError as exc:
            raise RuntimeError(
                "Existing data_catalog rows violate the host metadata contract "
                "and cannot be upgraded automatically: " + str(exc)
            ) from exc
        con.execute('DROP TABLE "data_catalog"')
        con.execute('ALTER TABLE "data_catalog_upgraded" RENAME TO "data_catalog"')

    @contextmanager
    def transaction(self) -> Iterator[sqlite3.Connection]:
        con = self.connect()
        try:
            con.execute("BEGIN IMMEDIATE")
            yield con
            con.commit()
        except Exception:
            con.rollback()
            raise
        finally:
            con.close()

    def upsert_item(self, relative_path: str, kind: str, sha256: str | None = None, classification: Classification = Classification.INTERNAL) -> str:
        now = utc_now()
        with self.transaction() as con:
            row = con.execute("SELECT item_id FROM items WHERE workspace_id=? AND relative_path=?", (self.workspace_id, relative_path)).fetchone()
            if row:
                con.execute("UPDATE items SET kind=?, sha256=?, classification=?, updated_utc=? WHERE item_id=?", (kind, sha256, classification.value, now, row["item_id"]))
                return row["item_id"]
            item_id = str(uuid4())
            con.execute("INSERT INTO items VALUES(?,?,?,?,?,?,?,?)", (item_id, self.workspace_id, relative_path, kind, sha256, classification.value, now, now))
            return item_id

    def remove_item(self, relative_path: str, descendants: bool = False) -> int:
        with self.transaction() as con:
            cur = con.execute("DELETE FROM items WHERE workspace_id=? AND relative_path=?", (self.workspace_id, relative_path))
            removed = cur.rowcount or 0
            if descendants:
                prefix = relative_path.rstrip("/") + "/"
                cur = con.execute(
                    "DELETE FROM items WHERE workspace_id=? AND relative_path LIKE ? ESCAPE '\\'",
                    (self.workspace_id, _like_prefix(prefix)),
                )
                removed += cur.rowcount or 0
            return removed

    def list_items(self, kind: str | None = None) -> list[sqlite3.Row]:
        with self.connection() as con:
            if kind:
                return con.execute("SELECT * FROM items WHERE workspace_id=? AND kind=? ORDER BY relative_path", (self.workspace_id, kind)).fetchall()
            return con.execute("SELECT * FROM items WHERE workspace_id=? ORDER BY relative_path", (self.workspace_id,)).fetchall()

    def _normalize_columns(self, columns: list[tuple[str, str]]) -> list[tuple[str, str]]:
        if not columns:
            raise ValueError("At least one column is required.")
        normalized: list[tuple[str, str]] = []
        for col, typ in columns:
            typ = typ.upper()
            if not _IDENT.match(col) or typ not in _ALLOWED_TYPES:
                raise ValueError(f"Invalid column definition: {col} {typ}")
            normalized.append((col, typ))
        return normalized

    def _create_table(self, con: sqlite3.Connection, name: str, normalized: list[tuple[str, str]]) -> tuple[str, str]:
        table_id = str(uuid4())
        sql_name = "data_" + table_id.replace("-", "")
        defs = ", ".join(f'"{c}" {t}' for c, t in normalized)
        con.execute(f'CREATE TABLE "{sql_name}" (row_id TEXT PRIMARY KEY NOT NULL, {defs})')
        con.execute(
            "INSERT INTO data_catalog(" + _CATALOG_COLUMNS + ") VALUES(?,?,?,?,?,?)",
            (table_id, self.workspace_id, name, sql_name, json.dumps(normalized), utc_now()),
        )
        return table_id, sql_name

    def create_data_table(self, name: str, columns: list[tuple[str, str]]) -> str:
        if not _IDENT.match(name):
            raise ValueError("Table name must be a simple identifier.")
        normalized = self._normalize_columns(columns)
        with self.transaction() as con:
            table_id, _ = self._create_table(con, name, normalized)
        return table_id

    def create_data_table_with_rows(self, name: str, columns: list[tuple[str, str]], rows: list[dict[str, object]]) -> str:
        if not _IDENT.match(name):
            raise ValueError("Table name must be a simple identifier.")
        normalized = self._normalize_columns(columns)
        allowed = {c for c, _ in normalized}
        for row in rows:
            unknown = set(row) - allowed
            if unknown:
                raise ValueError(f"Unknown columns: {sorted(unknown)}")
        with self.transaction() as con:
            table_id, sql_name = self._create_table(con, name, normalized)
            for values in rows:
                row_id = str(uuid4())
                cols = list(values)
                placeholders = ",".join("?" for _ in cols)
                col_sql = ",".join('"' + c + '"' for c in cols)
                con.execute(f'INSERT INTO "{sql_name}" (row_id{"," if cols else ""}{col_sql}) VALUES (?{"," if cols else ""}{placeholders})', [row_id, *[values[c] for c in cols]])
        return table_id

    def list_data_tables(self) -> list[sqlite3.Row]:
        with self.connection() as con:
            return con.execute("SELECT * FROM data_catalog WHERE workspace_id=? ORDER BY name", (self.workspace_id,)).fetchall()

    def add_data_row(self, table_id: str, values: dict[str, object]) -> str:
        with self.transaction() as con:
            meta = con.execute("SELECT * FROM data_catalog WHERE table_id=? AND workspace_id=?", (table_id, self.workspace_id)).fetchone()
            if not meta:
                raise KeyError("Unknown data table")
            schema = dict(json.loads(meta["schema_json"]))
            unknown = set(values) - set(schema)
            if unknown:
                raise ValueError(f"Unknown columns: {sorted(unknown)}")
            row_id = str(uuid4())
            cols = list(values)
            placeholders = ",".join("?" for _ in cols)
            col_sql = ",".join('"' + c + '"' for c in cols)
            con.execute(f'INSERT INTO "{meta["sql_name"]}" (row_id{"," if cols else ""}{col_sql}) VALUES (?{"," if cols else ""}{placeholders})', [row_id, *[values[c] for c in cols]])
            return row_id

    def query_data(self, table_id: str, limit: int = 500, *, filters: dict[str, object] | None = None, sort_by: str | None = None, descending: bool = False) -> tuple[list[str], list[sqlite3.Row]]:
        limit = max(1, min(limit, 5000))
        with self.connection() as con:
            meta = con.execute("SELECT * FROM data_catalog WHERE table_id=? AND workspace_id=?", (table_id, self.workspace_id)).fetchone()
            if not meta:
                raise KeyError("Unknown data table")
            cols = ["row_id", *[x[0] for x in json.loads(meta["schema_json"])]]
            filters = filters or {}
            unknown = set(filters) - set(cols)
            if unknown:
                raise ValueError(f"Unknown filter columns: {sorted(unknown)}")
            if sort_by is not None and sort_by not in cols:
                raise ValueError(f"Unknown sort column: {sort_by}")
            sql = f'SELECT * FROM "{meta["sql_name"]}"'
            params: list[object] = []
            if filters:
                clauses = []
                for name, value in filters.items():
                    clauses.append(f'"{name}" = ?')
                    params.append(value)
                sql += " WHERE " + " AND ".join(clauses)
            if sort_by:
                sql += f' ORDER BY "{sort_by}" {"DESC" if descending else "ASC"}'
            sql += " LIMIT ?"
            params.append(limit)
            rows = con.execute(sql, params).fetchall()
            return cols, rows
