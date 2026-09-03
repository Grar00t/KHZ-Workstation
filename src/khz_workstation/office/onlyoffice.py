from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

from .base import IOfficeEngine, OfficeEngineInfo


class OnlyOfficeDesktopEngine(IOfficeEngine):
    def __init__(self) -> None:
        candidates: list[Path] = []
        for name in ("DesktopEditors", "desktopeditors", "onlyoffice-desktopeditors"):
            found = shutil.which(name)
            if found:
                candidates.append(Path(found))
        if os.name == "nt":
            for env_name in ("PROGRAMFILES", "PROGRAMFILES(X86)"):
                base = os.getenv(env_name)
                if base:
                    candidates.append(Path(base) / "ONLYOFFICE" / "DesktopEditors" / "DesktopEditors.exe")
        self.executable = next((x for x in candidates if x.exists()), None)

    def info(self) -> OfficeEngineInfo:
        available = bool(self.executable)
        return OfficeEngineInfo(
            "ONLYOFFICE Desktop Editors",
            str(self.executable) if self.executable else None,
            None,
            available,
            "out-of-process local desktop editor fallback; no deterministic CLI PDF path",
            can_edit=available,
            can_convert_pdf=False,
        )

    def open_for_edit(self, path: Path) -> int | None:
        if not self.executable:
            raise FileNotFoundError("ONLYOFFICE Desktop Editors not detected.")
        return subprocess.Popen([str(self.executable), str(path)]).pid

    def convert_to_pdf(self, path: Path, output_dir: Path) -> Path:
        raise NotImplementedError("Deterministic command-line PDF conversion is not implemented for this adapter.")
