from __future__ import annotations

import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from khz_workstation.ai.policy import AIDisabledError, AIPolicy, ActionValidationError
from khz_workstation.audit import AuditLog, AuditWriteError
from khz_workstation.backup import BackupError, BackupService
from khz_workstation.data_service import DataWorkspaceService
from khz_workstation.fileops import FileService
from khz_workstation.i18n import Localizer
from khz_workstation.models import Classification, ContextManifest, NetworkMode
from khz_workstation.security.network import NetworkDenied, NetworkPolicy
from khz_workstation.security.paths import WorkspaceBoundaryError, WorkspacePathResolver
from khz_workstation.security.session import SessionLockService
from khz_workstation.settings import AppSettings
from khz_workstation.store import WorkspaceStore
from khz_workstation.terminal import TerminalService
from khz_workstation.workspace import Workspace


class WorkspaceSecurityTests(unittest.TestCase):
    def test_path_traversal_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "ws"
            root.mkdir()
            resolver = WorkspacePathResolver(root)
            with self.assertRaises(WorkspaceBoundaryError):
                resolver.resolve("../escape.txt")

    def test_absolute_path_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "ws"
            root.mkdir()
            resolver = WorkspacePathResolver(root)
            with self.assertRaises(WorkspaceBoundaryError):
                resolver.resolve(Path(td) / "outside.txt")

    def test_symlink_escape_rejected_when_supported(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            root = base / "ws"
            outside = base / "outside"
            root.mkdir(); outside.mkdir()
            link = root / "link"
            try:
                link.symlink_to(outside, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("Symlinks unavailable")
            resolver = WorkspacePathResolver(root)
            with self.assertRaises(WorkspaceBoundaryError):
                resolver.resolve("link/file.txt")

    def test_healthcare_profile_forces_security_defaults(self):
        s = AppSettings(ai_enabled=True, remote_ai_enabled=True, telemetry_enabled=True, git_network_enabled=True, macros_enabled=True, plugins_enabled=True, updates_enabled=True)
        s.apply_healthcare_hardened()
        self.assertFalse(s.ai_enabled)
        self.assertFalse(s.remote_ai_enabled)
        self.assertFalse(s.embeddings_enabled)
        self.assertFalse(s.telemetry_enabled)
        self.assertFalse(s.git_network_enabled)
        self.assertFalse(s.macros_enabled)
        self.assertFalse(s.plugins_enabled)
        self.assertFalse(s.updates_enabled)
        self.assertFalse(s.terminal_enabled)
        self.assertEqual(s.network_mode, NetworkMode.LOOPBACK_ONLY.value)

    def test_all_workspace_profiles_are_supported(self):
        for profile in AppSettings.PROFILES:
            s = AppSettings(ai_enabled=True)
            s.apply_profile(profile)
            self.assertEqual(s.profile, profile)
        office = AppSettings(); office.apply_profile("Office"); self.assertFalse(office.terminal_enabled)
        developer = AppSettings(); developer.apply_profile("Developer"); self.assertTrue(developer.terminal_enabled)
        with self.assertRaises(ValueError):
            AppSettings().apply_profile("Unknown")


class LocalizationTests(unittest.TestCase):
    def test_english_is_canonical_and_arabic_is_rtl_metadata(self):
        en = Localizer("en-US")
        ar = Localizer("ar-SA")
        self.assertTrue(en.info.canonical)
        self.assertFalse(en.info.rtl)
        self.assertTrue(ar.info.rtl)
        self.assertEqual(ar.text("surface.sheets"), "Sheets")  # English fallback until Arabic catalog exists.


class NetworkTests(unittest.TestCase):
    def test_deny_policy_rejects_network(self):
        p = NetworkPolicy(NetworkMode.DENY)
        with self.assertRaises(NetworkDenied):
            p.authorize_url("https://example.com")

    def test_loopback_only(self):
        p = NetworkPolicy(NetworkMode.LOOPBACK_ONLY)
        p.authorize_url("http://127.0.0.1:8080")
        with self.assertRaises(NetworkDenied):
            p.authorize_url("https://8.8.8.8")


class AITests(unittest.TestCase):
    def test_ai_off_blocks_context_release(self):
        p = AIPolicy(enabled=False)
        m = ContextManifest("w", "a", "A1:B2", 4, Classification.INTERNAL)
        with self.assertRaises(AIDisabledError):
            p.release_context(m, {"cells": []})

    def test_health_data_denied_by_default(self):
        p = AIPolicy(enabled=True, allow_health_data=False)
        m = ContextManifest("w", "Patient-0001.docx", "selection", 1, Classification.HEALTH_DATA)
        with self.assertRaises(PermissionError):
            p.release_context(m, "SYNTHETIC TEST DATA")

    def test_model_action_schema_and_workspace_enforced(self):
        p = AIPolicy(enabled=True)
        proposal = p.validate_action({"action": "SetFormula", "workspace_id": "w", "target": "Summary!F2:F5", "args": {"formula": "=E2*0.05"}}, "w")
        self.assertEqual(proposal.action, "SetFormula")
        with self.assertRaises(ActionValidationError):
            p.validate_action({"action": "RunShell", "workspace_id": "w", "target": "terminal", "args": {}}, "w")
        with self.assertRaises(ActionValidationError):
            p.validate_action({"action": "SetFormula", "workspace_id": "other", "target": "A1", "args": {}}, "w")


class PersistenceTests(unittest.TestCase):
    def test_database_transaction_rolls_back(self):
        with tempfile.TemporaryDirectory() as td:
            store = WorkspaceStore(Path(td) / "db.sqlite", "w")
            with self.assertRaises(RuntimeError):
                with store.transaction() as con:
                    con.execute("INSERT INTO tasks VALUES(?,?,?,?,?,?,?)", ("t", "w", "EXECUTING", "x", "{}", "now", "now"))
                    raise RuntimeError("fault injection")
            with store.connection() as con:
                count = con.execute("SELECT COUNT(*) FROM tasks").fetchone()[0]
            self.assertEqual(count, 0)

    def test_audit_chain_and_tamper_detection(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "audit.jsonl"
            audit = AuditLog(path)
            audit.append(who="u", what="a", target="x")
            audit.append(who="u", what="b", target="y")
            self.assertTrue(audit.verify_chain()[0])
            rows = path.read_text(encoding="utf-8").splitlines()
            event = json.loads(rows[0]); event["target"] = "tampered"
            rows[0] = json.dumps(event)
            path.write_text("\n".join(rows) + "\n", encoding="utf-8")
            self.assertFalse(audit.verify_chain()[0])

    def test_audit_write_failure_is_not_silenced(self):
        with tempfile.TemporaryDirectory() as td:
            audit = AuditLog(Path(td) / "audit.jsonl")
            with mock.patch.object(Path, "open", side_effect=OSError("disk failure")):
                with self.assertRaises(AuditWriteError):
                    audit.append(who="u", what="a", target="x")


class FileBackupRestoreTests(unittest.TestCase):
    def test_atomic_file_save_snapshot_backup_restore(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            ws = Workspace.create(base / "workspace", "Test")
            files = FileService(ws)
            files.atomic_write("report.txt", b"v1", preserve_version=False)
            files.atomic_write("report.txt", b"v2", preserve_version=True)
            self.assertEqual((ws.root / "report.txt").read_bytes(), b"v2")
            versions = list((ws.root / ".khz" / "versions").rglob("*.txt"))
            self.assertEqual(len(versions), 1)
            backup = BackupService(ws).create(base / "backup.khzbackup.zip")
            manifest = BackupService.validate(backup, ws.info.workspace_id)
            self.assertIn("report.txt", manifest["files"])
            restored, preserved = BackupService.restore(backup, base / "restored")
            self.assertIsNone(preserved)
            self.assertEqual((restored / "report.txt").read_bytes(), b"v2")

    def test_backup_failure_reports_failure(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            ws = Workspace.create(base / "workspace", "Test")
            FileService(ws).atomic_write("a.txt", b"x", preserve_version=False)
            with self.assertRaises(BackupError):
                BackupService(ws).create(base)

    def test_restore_rejects_corrupt_archive(self):
        with tempfile.TemporaryDirectory() as td:
            bad = Path(td) / "bad.zip"
            bad.write_bytes(b"not a zip")
            with self.assertRaises(BackupError):
                BackupService.restore(bad, Path(td) / "restore")


class DataWorkspaceTests(unittest.TestCase):
    def test_csv_import_filter_sort_and_export(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            ws = Workspace.create(base / "workspace", "Data")
            source = base / "sample.csv"
            source.write_text("Department,Budget,Year\nOps,125000,2026\nResearch,98000.5,2026\n", encoding="utf-8")
            table_id = DataWorkspaceService(ws).import_csv(source, "Budget")
            cols, rows = ws.store.query_data(table_id, filters={"Year": 2026}, sort_by="Budget", descending=True)
            self.assertEqual(len(rows), 2)
            self.assertEqual(rows[0]["Department"], "Ops")
            self.assertIn("row_id", cols)
            dest = base / "export.csv"
            DataWorkspaceService(ws).export_csv(table_id, dest)
            self.assertIn("Department", dest.read_text(encoding="utf-8-sig"))

    def test_bulk_table_creation_rolls_back_on_invalid_row(self):
        with tempfile.TemporaryDirectory() as td:
            store = WorkspaceStore(Path(td) / "db.sqlite", "w")
            with self.assertRaises(ValueError):
                store.create_data_table_with_rows("T", [("A", "TEXT")], [{"Unknown": "x"}])
            self.assertEqual(len(store.list_data_tables()), 0)


class SessionTests(unittest.TestCase):
    def test_session_lock_delegates_to_os_boundary(self):
        calls = []
        service = SessionLockService(lambda: calls.append("lock") or True)
        self.assertTrue(service.supported)
        self.assertTrue(service.lock_now())
        self.assertEqual(calls, ["lock"])


class ToolTests(unittest.TestCase):
    def test_terminal_requires_authorization(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True)
            with self.assertRaises(PermissionError):
                term.run("echo blocked", authorized=False)


if __name__ == "__main__":
    unittest.main()
