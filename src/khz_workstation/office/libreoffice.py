from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

from .base import IOfficeEngine, OfficeEngineInfo


def _candidate_paths() -> list[Path]:
    candidates: list[Path] = []
    for name in ("soffice", "libreoffice"):
        found = shutil.which(name)
        if found:
            candidates.append(Path(found))
    if os.name == "nt":
        for env_name in ("PROGRAMFILES", "PROGRAMFILES(X86)"):
            base = os.getenv(env_name)
            if base:
                candidates.extend(
                    [
                        Path(base) / "LibreOffice" / "program" / "soffice.exe",
                        Path(base) / "LibreOffice" / "program" / "soffice.com",
                    ]
                )
    return candidates


class LibreOfficeEngine(IOfficeEngine):
    def __init__(self, executable: Path | None = None) -> None:
        self.executable = executable or next((p for p in _candidate_paths() if p.exists()), None)

    def info(self) -> OfficeEngineInfo:
        version = None
        if self.executable:
            try:
                cp = subprocess.run(
                    [str(self.executable), "--version"],
                    capture_output=True,
                    text=True,
                    timeout=10,
                    encoding="utf-8",
                    errors="replace",
                )
                version = (cp.stdout or cp.stderr).strip() or None
            except Exception:
                version = None
        return OfficeEngineInfo(
            "LibreOffice",
            str(self.executable) if self.executable else None,
            version,
            bool(self.executable),
            "out-of-process local desktop editor; deterministic headless conversion",
            can_edit=True,
            can_convert_pdf=bool(self.executable),
        )

    def open_for_edit(self, path: Path) -> int | None:
        if not self.executable:
            raise FileNotFoundError("LibreOffice is not installed or was not detected.")
        if not path.exists():
            raise FileNotFoundError(path)
        proc = subprocess.Popen(
            [str(self.executable), "--norestore", "--nofirststartwizard", str(path)]
        )
        return proc.pid

    def convert_to_pdf(self, path: Path, output_dir: Path) -> Path:
        if not self.executable:
            raise FileNotFoundError("LibreOffice is not installed or was not detected.")
        output_dir.mkdir(parents=True, exist_ok=True)
        cp = subprocess.run(
            [
                str(self.executable),
                "--headless",
                "--norestore",
                "--nofirststartwizard",
                "--convert-to",
                "pdf",
                "--outdir",
                str(output_dir),
                str(path),
            ],
            capture_output=True,
            text=True,
            timeout=120,
            encoding="utf-8",
            errors="replace",
        )
        if cp.returncode != 0:
            raise RuntimeError(f"LibreOffice conversion failed: {cp.stderr or cp.stdout}")
        result = output_dir / (path.stem + ".pdf")
        if not result.exists():
            raise RuntimeError(f"LibreOffice did not produce expected output: {result}")
        return result
