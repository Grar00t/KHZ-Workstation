from __future__ import annotations

import shutil
import subprocess
import tempfile
import time
from pathlib import Path

from khz_workstation.app import KHZApp
from khz_workstation.fileops import FileService
from khz_workstation.workspace import Workspace


def capture(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["scrot", str(path)], check=True)


def main() -> int:
    repo = Path(__file__).resolve().parents[1]
    out = repo / "acceptance" / "ui-evidence"
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    with tempfile.TemporaryDirectory(prefix="khz-ui-") as td:
        root = Path(td) / "Synthetic-Workspace"
        ws = Workspace.create(root)
        files = FileService(ws)
        for name in ("InstitutionalReport.docx", "InstitutionalWorkbook.xlsx", "InstitutionalPresentation.pptx", "InstitutionalPacket.pdf"):
            files.import_file(repo / "acceptance" / "corpus" / name, name)
        files.atomic_write("README.txt", b"SYNTHETIC TEST DATA\nKHZ UI evidence workspace\n", preserve_version=False)
        table_id = ws.store.create_data_table("DepartmentBudget", [("Department", "TEXT"), ("Budget", "REAL"), ("Year", "INTEGER")])
        ws.store.add_data_row(table_id, {"Department": "Operations", "Budget": 125000.0, "Year": 2026})
        ws.store.add_data_row(table_id, {"Department": "Research", "Budget": 98000.0, "Year": 2026})
        subprocess.run(["git", "init"], cwd=root, capture_output=True, check=True)
        subprocess.run(["git", "config", "user.email", "synthetic@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.name", "Synthetic User"], cwd=root, check=True)
        subprocess.run(["git", "add", "README.txt"], cwd=root, check=True)
        subprocess.run(["git", "commit", "-m", "Synthetic baseline"], cwd=root, capture_output=True, check=True)

        app = KHZApp()
        app.open_workspace(root, create_if_missing=False)
        app.geometry("1280x820+0+0")
        app.update_idletasks(); app.update(); time.sleep(0.25)

        surfaces = ["Home", "Documents", "Sheets", "Slides", "PDF", "Data", "Repositories", "Terminal", "Settings", "Assistant"]
        for surface in surfaces:
            app.show_surface(surface)
            app.update_idletasks(); app.update(); time.sleep(0.15)
            capture(out / f"KHZ-{surface.replace(' ', '-')}.png")

        app.settings.apply_healthcare_hardened()
        app.show_surface("Settings")
        app.policy_label.config(text=app._policy_text())
        app.update_idletasks(); app.update(); time.sleep(0.15)
        capture(out / "KHZ-Healthcare-Hardened-Settings.png")
        app.destroy()

    print(f"UI_CAPTURES={len(list(out.glob('*.png')))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
