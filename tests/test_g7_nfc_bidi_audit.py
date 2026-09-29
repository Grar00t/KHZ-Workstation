from __future__ import annotations

import tempfile
import unittest
import unicodedata
from pathlib import Path

from khz_workstation.data_service import DataWorkspaceService
from khz_workstation.search import LocalSearch
from khz_workstation.store import WorkspaceStore
from khz_workstation.workspace import Workspace


def _nfd(s: str) -> str:
    return unicodedata.normalize("NFD", s)


class NfcBoundaryTests(unittest.TestCase):
    def test_import_csv_normalizes_cells_to_nfc(self) -> None:
        nfc_name = "caf\xe9"
        nfd_name = _nfd(nfc_name)
        with tempfile.TemporaryDirectory() as td:
            ws = Workspace.create(Path(td) / "ws", "Data")
            src = Path(td) / "in.csv"
            src.write_text(f"Header,Name\n1,{nfd_name}\n", encoding="utf-8")
            table_id = DataWorkspaceService(ws).import_csv(src, "T")
            cols, rows = ws.store.query_data(table_id)
            stored = rows[0]["Name"]
            self.assertEqual(
                unicodedata.normalize("NFC", stored), stored,
                "stored cell is not in NFC",
            )

    def test_search_needle_is_nfc_normalized(self) -> None:
        nfc_q = "caf\xe9"
        with tempfile.TemporaryDirectory() as td:
            ws = Workspace.create(Path(td) / "ws", "Data")
            (ws.root / "caf\xe9-note.md").write_text("ok", encoding="utf-8")
            res = LocalSearch(ws, content_enabled=False).query(_nfd(nfc_q))
            self.assertTrue(any("caf\xe9" in r.relative_path for r in res))


class ExplicitCollationTests(unittest.TestCase):
    def test_list_items_uses_explicit_collation(self) -> None:
        from khz_workstation.store import WorkspaceStore
        import inspect
        src = inspect.getsource(WorkspaceStore.list_items)
        self.assertIn("COLLATE", src, "ORDER BY has no explicit COLLATE")

    def test_list_data_tables_uses_explicit_collation(self) -> None:
        import inspect
        src = inspect.getsource(WorkspaceStore.list_data_tables)
        self.assertIn("COLLATE", src, "ORDER BY has no explicit COLLATE")

    def test_query_data_uses_explicit_collation(self) -> None:
        import inspect
        src = inspect.getsource(WorkspaceStore.query_data)
        self.assertIn("COLLATE", src, "ORDER BY has no explicit COLLATE")


if __name__ == "__main__":
    unittest.main()
