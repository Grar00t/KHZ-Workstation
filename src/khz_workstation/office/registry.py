from __future__ import annotations

import os
from pathlib import Path

from .base import IOfficeEngine
from .libreoffice import LibreOfficeEngine
from .onlyoffice import OnlyOfficeDesktopEngine


class OfficeRegistry:
    def __init__(self) -> None:
        self.engines: list[IOfficeEngine] = [LibreOfficeEngine(), OnlyOfficeDesktopEngine()]

    def selected(self, require_pdf: bool = False) -> IOfficeEngine | None:
        preferred = os.getenv("KHZ_OFFICE_ENGINE", "LibreOffice").casefold()
        available = [e for e in self.engines if e.info().available]
        if require_pdf:
            available = [e for e in available if e.info().can_convert_pdf]
        for engine in available:
            if preferred in engine.info().engine.casefold():
                return engine
        return available[0] if available else None

    def statuses(self):
        return [x.info() for x in self.engines]

    def convert_to_pdf(self, path: Path, output_dir: Path) -> Path:
        engine = self.selected(require_pdf=True)
        if engine is None:
            raise FileNotFoundError(
                "No Office engine with PDF conversion is available. Install LibreOffice."
            )
        return engine.convert_to_pdf(path, output_dir)

    def open_registered_or_system(self, path: Path) -> int | None:
        engine = self.selected()
        if engine and engine.info().can_edit:
            return engine.open_for_edit(path)
        if os.name == "nt":
            os.startfile(path)  # type: ignore[attr-defined]
            return None
        raise FileNotFoundError(
            "No supported Office engine detected. Install LibreOffice or use the OS registered editor."
        )
