from __future__ import annotations

import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

from khz_workstation.store import METADATA_SCHEMA_VERSION, WorkspaceStore
from khz_workstation.workspace import Workspace

# The DDL the Python store emitted before the host contract was adopted.
_LEGACY_SCRIPT = """
CREATE TABLE IF NOT EXISTS schema_meta(version INTEGER NOT NULL);
INSERT INTO schema_meta(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM schema_meta);
CREATE TABLE IF NOT EXISTS data_catalog(
    table_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL,
    name TEXT NOT NULL,
    sql_name TEXT NOT NULL UNIQUE,
    schema_json TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    UNIQUE(workspace_id, name)
);
"""

_CATALOG_COLUMNS = "table_id, workspace_id, name, sql_name, schema_json, created_utc"


def _table_sql(db_path: Path, table: str) -> str:
    con = sqlite3.connect(db_path)
    try:
        row = con.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=?", (table,)
        ).fetchone()
    finally:
        con.close()
    if row is None:
        return ""
    return " ".join((row[0] or "").split())


def _user_version(db_path: Path) -> int:
    con = sqlite3.connect(db_path)
    try:
        return int(con.execute("PRAGMA user_version").fetchone()[0])
    finally:
        con.close()


def _identity_rows(db_path: Path) -> list[tuple[object, ...]]:
    con = sqlite3.connect(db_path)
    try:
        return con.execute(
            "SELECT workspace_id, manifest_schema_version, created_utc FROM workspace_identity"
        ).fetchall()
    finally:
        con.close()


class MetadataContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.ws = Workspace.create(self.tmp / "ws")
        self.db = self.ws.root / ".khz" / "metadata.db"

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_metadata_schema_version_matches_host_constant(self) -> None:
        self.assertEqual(METADATA_SCHEMA_VERSION, 2)

    def test_user_version_is_stamped(self) -> None:
        self.assertEqual(_user_version(self.db), METADATA_SCHEMA_VERSION)

    def test_identity_row_matches_manifest(self) -> None:
        manifest = json.loads(
            (self.ws.root / ".khz" / "workspace.json").read_text(encoding="utf-8")
        )
        rows = _identity_rows(self.db)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0][0], manifest["workspace_id"])
        self.assertEqual(int(rows[0][1]), int(manifest["schema_version"]))
        self.assertEqual(rows[0][2], manifest["created_utc"])

    def test_data_catalog_carries_host_constraints(self) -> None:
        sql = _table_sql(self.db, "data_catalog").lower()
        self.assertIn("check(length(trim(name)) between 1 and 160)", sql)
        self.assertIn("check(length(trim(sql_name)) between 1 and 80)", sql)
        self.assertIn("references workspace_identity(workspace_id)", sql)
        self.assertIn("on delete cascade", sql)
        self.assertEqual(sql.count("not null"), 6)

    def test_blank_catalog_name_is_rejected(self) -> None:
        with self.ws.store.connection() as con:
            with self.assertRaises(sqlite3.IntegrityError):
                con.execute(
                    "INSERT INTO data_catalog(" + _CATALOG_COLUMNS + ") VALUES(?,?,?,?,?,?)",
                    (
                        "t-blank",
                        self.ws.info.workspace_id,
                        "   ",
                        "data_blank",
                        "[]",
                        "2026-01-01T00:00:00Z",
                    ),
                )

    def test_overlong_sql_name_is_rejected(self) -> None:
        with self.ws.store.connection() as con:
            with self.assertRaises(sqlite3.IntegrityError):
                con.execute(
                    "INSERT INTO data_catalog(" + _CATALOG_COLUMNS + ") VALUES(?,?,?,?,?,?)",
                    (
                        "t-long",
                        self.ws.info.workspace_id,
                        "Long",
                        "d" * 81,
                        "[]",
                        "2026-01-01T00:00:00Z",
                    ),
                )

    def test_identity_delete_cascades_the_catalog(self) -> None:
        self.ws.store.create_data_table("Cascade", [("Alpha", "TEXT")])
        self.assertEqual(len(self.ws.store.list_data_tables()), 1)
        with self.ws.store.connection() as con:
            con.execute(
                "DELETE FROM workspace_identity WHERE workspace_id=?",
                (self.ws.info.workspace_id,),
            )
            con.commit()
            remaining = con.execute("SELECT COUNT(*) FROM data_catalog").fetchone()[0]
        self.assertEqual(remaining, 0)

    def test_data_table_row_id_is_not_null(self) -> None:
        self.ws.store.create_data_table("Rows", [("Alpha", "TEXT")])
        sql_name = self.ws.store.list_data_tables()[0]["sql_name"]
        self.assertIn(
            "row_id TEXT PRIMARY KEY NOT NULL", _table_sql(self.db, sql_name)
        )

    def test_connections_use_full_synchronous(self) -> None:
        with self.ws.store.connection() as con:
            self.assertEqual(int(con.execute("PRAGMA synchronous").fetchone()[0]), 2)

    def test_legacy_database_is_upgraded_in_place(self) -> None:
        db = self.tmp / "legacy.db"
        con = sqlite3.connect(db)
        try:
            con.executescript(_LEGACY_SCRIPT)
            con.execute(
                "INSERT INTO data_catalog(" + _CATALOG_COLUMNS + ") VALUES(?,?,?,?,?,?)",
                (
                    "t-legacy",
                    "ws-legacy",
                    "Legacy",
                    "data_legacy",
                    "[]",
                    "2026-01-01T00:00:00Z",
                ),
            )
            con.commit()
        finally:
            con.close()
        self.assertNotIn("check(", _table_sql(db, "data_catalog").lower())
        self.assertEqual(_user_version(db), 0)

        store = WorkspaceStore(
            db,
            "ws-legacy",
            manifest_schema_version=1,
            created_utc="2026-01-01T00:00:00Z",
        )

        self.assertEqual(_user_version(db), METADATA_SCHEMA_VERSION)
        upgraded = _table_sql(db, "data_catalog").lower()
        self.assertIn("references workspace_identity(workspace_id)", upgraded)
        self.assertIn("check(length(trim(name)) between 1 and 160)", upgraded)
        self.assertEqual(_table_sql(db, "data_catalog_upgraded"), "")
        self.assertEqual(len(_identity_rows(db)), 1)
        self.assertEqual(
            [row["name"] for row in store.list_data_tables()], ["Legacy"]
        )

    def test_migration_is_idempotent(self) -> None:
        db = self.tmp / "idempotent.db"
        WorkspaceStore(db, "ws-1", manifest_schema_version=1, created_utc="2026-01-01T00:00:00Z")
        first = _table_sql(db, "data_catalog")
        WorkspaceStore(db, "ws-1", manifest_schema_version=1, created_utc="2026-01-01T00:00:00Z")
        self.assertEqual(_table_sql(db, "data_catalog"), first)
        self.assertEqual(len(_identity_rows(db)), 1)
        self.assertEqual(_user_version(db), METADATA_SCHEMA_VERSION)

    def test_newer_metadata_schema_is_rejected(self) -> None:
        db = self.tmp / "newer.db"
        WorkspaceStore(db, "ws-1")
        con = sqlite3.connect(db)
        try:
            con.execute("PRAGMA user_version=99")
            con.commit()
        finally:
            con.close()
        with self.assertRaises(RuntimeError):
            WorkspaceStore(db, "ws-1")

    def test_foreign_workspace_identity_is_rejected(self) -> None:
        db = self.tmp / "foreign.db"
        WorkspaceStore(db, "ws-a")
        with self.assertRaises(RuntimeError):
            WorkspaceStore(db, "ws-b")

    def test_manifest_schema_mismatch_is_rejected(self) -> None:
        db = self.tmp / "mismatch.db"
        WorkspaceStore(db, "ws-a", manifest_schema_version=1)
        with self.assertRaises(RuntimeError):
            WorkspaceStore(db, "ws-a", manifest_schema_version=2)


if __name__ == "__main__":
    unittest.main()
