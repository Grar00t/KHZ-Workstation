from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .workspace import Workspace


@dataclass(frozen=True)
class SearchResult:
    relative_path: str
    kind: str
    reason: str


class LocalSearch:
    TEXT_EXTENSIONS = {".txt", ".md", ".csv", ".json", ".py", ".js", ".ts", ".cs", ".xml", ".yaml", ".yml"}

    def __init__(self, workspace: Workspace, content_enabled: bool = False) -> None:
        self.ws = workspace
        self.content_enabled = content_enabled

    def query(self, text: str, limit: int = 200) -> list[SearchResult]:
        needle = text.casefold().strip()
        if not needle:
            return []
        out: list[SearchResult] = []
        for path in self.ws.root.rglob("*"):
            if self.ws.META_DIR in path.parts or not path.is_file():
                continue
            rel = str(path.relative_to(self.ws.root))
            if needle in path.name.casefold():
                out.append(SearchResult(rel, "file", "filename"))
            elif self.content_enabled and path.suffix.lower() in self.TEXT_EXTENSIONS and path.stat().st_size <= 2_000_000:
                try:
                    if needle in path.read_text(encoding="utf-8", errors="ignore").casefold():
                        out.append(SearchResult(rel, "file", "local text content"))
                except OSError:
                    pass
            if len(out) >= limit:
                break
        return out
