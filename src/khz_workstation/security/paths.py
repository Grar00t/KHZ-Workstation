from __future__ import annotations

import os
import stat
from pathlib import Path


class WorkspaceBoundaryError(ValueError):
    pass


def _is_reparse_or_link(path: Path) -> bool:
    try:
        if path.is_symlink():
            return True
        st = path.lstat()
        attrs = getattr(st, "st_file_attributes", 0)
        reparse = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        return bool(attrs & reparse)
    except FileNotFoundError:
        return False


class WorkspacePathResolver:
    def __init__(self, root: Path, reject_links: bool = True) -> None:
        self.root = root.resolve(strict=True)
        self.reject_links = reject_links

    def resolve(self, relative: str | Path, must_exist: bool = False) -> Path:
        rel = Path(relative)
        if rel.is_absolute():
            raise WorkspaceBoundaryError("Absolute paths are not workspace-relative.")
        candidate = self.root / rel
        probe = candidate if candidate.exists() else candidate.parent
        resolved_probe = probe.resolve(strict=probe.exists())
        if not resolved_probe.is_relative_to(self.root):
            raise WorkspaceBoundaryError("Path escapes workspace boundary.")
        if self.reject_links:
            current = self.root
            for part in rel.parts[:-1] if not candidate.exists() else rel.parts:
                current = current / part
                if _is_reparse_or_link(current):
                    raise WorkspaceBoundaryError("Symlink/reparse-point traversal is denied.")
        resolved = candidate.resolve(strict=must_exist) if must_exist else candidate.resolve(strict=False)
        if not resolved.is_relative_to(self.root):
            raise WorkspaceBoundaryError("Resolved path escapes workspace boundary.")
        return resolved
