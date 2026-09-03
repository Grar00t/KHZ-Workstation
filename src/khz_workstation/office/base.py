from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class OfficeEngineInfo:
    engine: str
    executable: str | None
    version: str | None
    available: bool
    integration: str
    can_edit: bool = True
    can_convert_pdf: bool = False


class IOfficeEngine(ABC):
    @abstractmethod
    def info(self) -> OfficeEngineInfo:
        raise NotImplementedError

    @abstractmethod
    def open_for_edit(self, path: Path) -> int | None:
        raise NotImplementedError

    @abstractmethod
    def convert_to_pdf(self, path: Path, output_dir: Path) -> Path:
        raise NotImplementedError
