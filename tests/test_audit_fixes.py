from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from khz_workstation.data_service import DataWorkspaceService, _infer_type
from khz_workstation.fileops import FileService
from khz_workstation.gittools import GitService
from khz_workstation.office.base import OfficeEngineInfo
from khz_workstation.office.onlyoffice import OnlyOfficeDesktopEngine
from khz_workstation.office.registry import OfficeRegistry
from khz_workstation.schema_sidecar import SchemaError
from khz_workstation.workspace import Workspace


class InferTypeTests(unittest.TestCase):
    def test_leading_zeros_remain_text(self):
        self.assertEqual(_infer_type(["00123", "00456"]), "TEXT")

    def test_long_account_remains_text(self):
        self.assertEqual(_infer_type(["1234567890123456"]), "TEXT")

    def test_scientific_literal_remains_text(self):
        self.assertEqual(_infer_type(["1.2e3", "4e10"]), "TEXT")

    def test_plain_integers_stay_integer(self):
        self.assertEqual(_infer_type(["12", "98000"]), "INTEGER")


class SchemaSidecarTests(unittest.TestCase):
    def test_sidecar_forces_text_contract(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            ws = Workspace.create(base / "workspace", "Data")
            source = base / "accounts.csv"
            source.write_text("Account,Label\n00123,Ops\n", encoding="utf-8")
            sidecar = base / "accounts.schema.json"
            sidecar.write_text(json.dumps({"columns": [{"name": "Account", "type": "text"}, {"name": "Label", "type": "text"}]}), encoding="utf-8")
            table_id = DataWorkspaceService(ws).import_csv(source, "Accounts")
            _cols, rows = ws.store.query_data(table_id)
            self.assertEqual(rows[0]["Account"], "00123")

    def test_require_schema_without_sidecar_fails(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            ws = Workspace.create(base / "workspace", "Data")
            source = base / "plain.csv"
            source.write_text("A\n1\n", encoding="utf-8")
            with self.assertRaises(SchemaError):
                DataWorkspaceService(ws).import_csv(source, "Plain", require_schema=True)


class OfficeCapabilityTests(unittest.TestCase):
    def test_onlyoffice_declares_no_pdf(self):
        info = OnlyOfficeDesktopEngine().info()
        self.assertIsInstance(info, OfficeEngineInfo)
        self.assertFalse(info.can_convert_pdf)
        self.assertTrue(info.can_edit)

    def test_selected_require_pdf_skips_onlyoffice(self):
        registry = OfficeRegistry()
        registry.engines = [OnlyOfficeDesktopEngine()]
        self.assertIsNone(registry.selected(require_pdf=True))


class GitMissingBinaryTests(unittest.TestCase):
    def test_missing_git_returns_result(self):
        with tempfile.TemporaryDirectory() as td:
            service = GitService(Path(td))
            with mock.patch("khz_workstation.gittools.subprocess.run", side_effect=FileNotFoundError("git")):
                result = service.status()
            self.assertEqual(result.exit_code, 127)
            self.assertIn("git executable", result.stderr)


class FolderIndexTests(unittest.TestCase):
    def test_safe_delete_folder_removes_child_index_rows(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            ws = Workspace.create(base / "workspace", "Files")
            files = FileService(ws)
            files.atomic_write("pack/a.txt", b"a", preserve_version=False)
            files.atomic_write("pack/b.txt", b"b", preserve_version=False)
            self.assertGreaterEqual(len(ws.store.list_items()), 2)
            files.safe_delete("pack")
            leftovers = [row["relative_path"] for row in ws.store.list_items()]
            self.assertNotIn("pack/a.txt", leftovers)
            self.assertNotIn("pack/b.txt", leftovers)


if __name__ == "__main__":
    unittest.main()
