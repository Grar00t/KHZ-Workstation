from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from khz_workstation.ai.policy import AIDisabledError, AIPolicy
from khz_workstation.backup import BackupService
from khz_workstation.fileops import FileService
from khz_workstation.gittools import GitService
from khz_workstation.models import Classification, ContextManifest
from khz_workstation.search import LocalSearch
from khz_workstation.terminal import TerminalService
from khz_workstation.workspace import Workspace


def main() -> int:
    checks = {}
    with tempfile.TemporaryDirectory() as td:
        base = Path(td)
        ws = Workspace.create(base / "workspace", "NO_AI_BASELINE")
        checks["workspace_create"] = True
        policy = AIPolicy(enabled=False)
        try:
            policy.release_context(ContextManifest(ws.info.workspace_id, "x", "selection", 1, Classification.INTERNAL), "x")
            checks["ai_kill_switch"] = False
        except AIDisabledError:
            checks["ai_kill_switch"] = True
        fs = FileService(ws)
        fs.atomic_write("Documents/report.txt", b"synthetic document\n", preserve_version=False)
        fs.atomic_write("Sheets/data.csv", b"Name,Amount\nA,10\nB,20\n", preserve_version=False)
        fs.atomic_write("Slides/outline.txt", b"Title\nPoint one\n", preserve_version=False)
        fs.atomic_write("PDF/readme.txt", b"PDF fixture is exercised separately.\n", preserve_version=False)
        checks["files"] = (ws.root / "Documents/report.txt").exists()
        table = ws.store.create_data_table("Operations", [("Name", "TEXT"), ("Amount", "REAL")])
        ws.store.add_data_row(table, {"Name": "Synthetic", "Amount": 42.0})
        checks["data"] = ws.store.query_data(table)[1][0]["Name"] == "Synthetic"
        checks["search"] = any(x.relative_path.endswith("report.txt") for x in LocalSearch(ws).query("report"))
        if shutil.which("git"):
            subprocess.run(["git", "init", str(ws.root)], capture_output=True)
            checks["git_read_only"] = GitService(ws.root).is_repository()
        else:
            checks["git_read_only"] = None
        term = TerminalService(ws.root, enabled=True)
        result = term.run(f'"{sys.executable}" -c "print(12345)"', authorized=True, timeout=20)
        checks["terminal"] = result.exit_code == 0 and "12345" in result.stdout
        backup = BackupService(ws).create(base / "baseline.khzbackup.zip")
        checks["backup"] = backup.exists()
        restored, _ = BackupService.restore(backup, base / "restored")
        checks["restore"] = (restored / "Documents/report.txt").exists()
        checks["audit"] = ws.audit.verify_chain()[0]
    required = [v for v in checks.values() if v is not None]
    status = "PASSED" if all(required) else "FAILED"
    out = {"scenario": "NO_AI_BASELINE", "status": status, "checks": checks, "office_roundtrip": "SEE acceptance/reports/office-roundtrip.json"}
    report = Path(__file__).resolve().parents[1] / "acceptance" / "reports" / "no-ai-baseline.json"
    report.parent.mkdir(parents=True, exist_ok=True); report.write_text(json.dumps(out, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps(out, indent=2, sort_keys=True))
    return 0 if status == "PASSED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
