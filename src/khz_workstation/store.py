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


def _like_prefix(prefix: str) -> str:
    return prefix.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"


class WorkspaceStore:
    SCHEMA_VERSION = 1

    def __init__(self, db_path: Path, workspace_id: str) -> None:
        self.db_path = db_path
        self.workspace_id = workspace_id
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._migrate()

    def connect(self) -> sqlite3.Connection:
        con = sqlite3.connect(self.db_path)
        con.row_factory = sqlite3.Row
        con.execute("PRAGMA foreign_keys=ON")
        con.execute("PRAGMA journal_mode=WAL")
        return con

    @contextmanager
    def connection(self) -> Iterator[sqlite3.Connection]:
        con = self.connect()
        try:
            yield con
        finally:
            con.close()

    def _migrate(self) -> None:
        with self.connection() as con:
            con.executescript("""
            CREATE TABLE IF NOT EXISTS schema_meta(version INTEGER NOT NULL);
            INSERT INTO schema_meta(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_meta);
            CREATE TABLE IF NOT EXISTS items(
                item_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                kind TEXT NOT NULL,
                sha256 TEXT,
                classification TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                UNIQUE(workspace_id, relative_path)
            );
            CREATE TABLE IF NOT EXISTS data_catalog(
                table_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                name TEXT NOT NULL,
                sql_name TEXT NOT NULL UNIQUE,
                schema_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(workspace_id, name)
            );
            CREATE TABLE IF NOT EXISTS tasks(
                task_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                state TEXT NOT NULL,
                title TEXT NOT NULL,
                details TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """)
            con.commit()

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

    def create_data_table(self, name: str, columns: list[tuple[str, str]]) -> str:
        if not _IDENT.match(name):
            raise ValueError("Table name must be a simple identifier.")
        if not columns:
            raise ValueError("At least one column is required.")
        normalized: list[tuple[str, str]] = []
        for col, typ in columns:
            typ = typ.upper()
            if not _IDENT.match(col) or typ not in _ALLOWED_TYPES:
                raise ValueError(f"Invalid column definition: {col} {typ}")
            normalized.append((col, typ))
        table_id = str(uuid4())
        sql_name = "data_" + table_id.replace("-", "")
        defs = ", ".join(f'"{c}" {t}' for c, t in normalized)
        with self.transaction() as con:
            con.execute(f'CREATE TABLE "{sql_name}" (row_id TEXT PRIMARY KEY, {defs})')
            con.execute("INSERT INTO data_catalog VALUES(?,?,?,?,?,?)", (table_id, self.workspace_id, name, sql_name, json.dumps(normalized), utc_now()))
        return table_id

    def create_data_table_with_rows(self, name: str, columns: list[tuple[str, str]], rows: list[dict[str, object]]) -> str:
        if not _IDENT.match(name):
            raise ValueError("Table name must be a simple identifier.")
        if not columns:
            raise ValueError("At least one column is required.")
        normalized: list[tuple[str, str]] = []
        for col, typ in columns:
            typ = typ.upper()
            if not _IDENT.match(col) or typ not in _ALLOWED_TYPES:
                raise ValueError(f"Invalid column definition: {col} {typ}")
            normalized.append((col, typ))
        allowed = {c for c, _ in normalized}
        for row in rows:
            unknown = set(row) - allowed
            if unknown:
                raise ValueError(f"Unknown columns: {sorted(unknown)}")
        table_id = str(uuid4())
        sql_name = "data_" + table_id.replace("-", "")
        defs = ", ".join(f'"{c}" {t}' for c, t in normalized)
        with self.transaction() as con:
            con.execute(f'CREATE TABLE "{sql_name}" (row_id TEXT PRIMARY KEY, {defs})')
            con.execute("INSERT INTO data_catalog VALUES(?,?,?,?,?,?)", (table_id, self.workspace_id, name, sql_name, json.dumps(normalized), utc_now()))
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
