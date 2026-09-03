from __future__ import annotations

from getpass import getuser
import hashlib
import os
import shutil
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

from .workspace import Workspace


OFFICE_KIND = {
    ".docx": "document", ".odt": "document", ".rtf": "document", ".txt": "document",
    ".xlsx": "sheet", ".xlsm": "sheet", ".ods": "sheet", ".csv": "sheet",
    ".pptx": "slides", ".odp": "slides",
    ".pdf": "pdf",
}


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


class FileService:
    def __init__(self, workspace: Workspace) -> None:
        self.ws = workspace

    def index_file(self, relative: str) -> str:
        path = self.ws.paths.resolve(relative, must_exist=True)
        if not path.is_file():
            raise ValueError("Only files may be indexed.")
        kind = OFFICE_KIND.get(path.suffix.lower(), "file")
        return self.ws.store.upsert_item(str(path.relative_to(self.ws.root)), kind, sha256_file(path))

    def index_tree(self, relative: str) -> int:
        root = self.ws.paths.resolve(relative, must_exist=True)
        if root.is_file():
            self.index_file(relative)
            return 1
        count = 0
        for path in root.rglob("*"):
            if not path.is_file() or self.ws.META_DIR in path.parts:
                continue
            rel = str(path.relative_to(self.ws.root))
            try:
                self.index_file(rel)
                count += 1
            except (OSError, ValueError):
                continue
        return count

    def scan(self) -> int:
        count = 0
        for path in self.ws.root.rglob("*"):
            if not path.is_file() or self.ws.META_DIR in path.parts:
                continue
            try:
                self.index_file(str(path.relative_to(self.ws.root)))
                count += 1
            except (OSError, ValueError):
                continue
        return count

    def atomic_write(self, relative: str, data: bytes, preserve_version: bool = True) -> Path:
        target = self.ws.paths.resolve(relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        if preserve_version and target.exists():
            self.snapshot(relative)
        tmp = target.with_name(target.name + ".khz-tmp-" + uuid4().hex)
        try:
            with tmp.open("wb") as fh:
                fh.write(data)
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(tmp, target)
        finally:
            tmp.unlink(missing_ok=True)
        self.index_file(relative)
        self.ws.audit.append(who=getuser(), what="file.saved", target=relative, result="SAVED", verification=sha256_file(target))
        return target

    def snapshot(self, relative: str) -> Path:
        source = self.ws.paths.resolve(relative, must_exist=True)
        item_id = self.index_file(relative)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        dest_dir = self.ws.root / self.ws.META_DIR / "versions" / item_id
        dest_dir.mkdir(parents=True, exist_ok=True)
        digest = sha256_file(source)[:12]
        dest = dest_dir / f"{stamp}-{digest}{source.suffix}"
        shutil.copy2(source, dest)
        return dest

    def safe_delete(self, relative: str) -> Path:
        source = self.ws.paths.resolve(relative, must_exist=True)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        trash = self.ws.root / self.ws.META_DIR / "trash" / f"{stamp}-{uuid4().hex[:8]}" / relative
        trash.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(source), str(trash))
        self.ws.store.remove_item(relative, descendants=True)
        self.ws.audit.append(who=getuser(), what="file.safe_deleted", target=relative, result="MOVED_TO_WORKSPACE_TRASH")
        return trash

    def import_file(self, source: Path, dest_rel: str) -> Path:
        source = source.resolve(strict=True)
        if not source.is_file():
            raise ValueError("Import source must be a file.")
        dest = self.ws.paths.resolve(dest_rel)
        dest.parent.mkdir(parents=True, exist_ok=True)
        if dest.exists():
            raise FileExistsError(dest)
        shutil.copy2(source, dest)
        self.index_file(dest_rel)
        self.ws.audit.append(who=getuser(), what="file.imported", target=dest_rel, result="COPIED_FROM_EXTERNAL_SOURCE", metadata={"source_name": source.name})
        return dest

    def copy(self, source_rel: str, dest_rel: str) -> Path:
        source = self.ws.paths.resolve(source_rel, must_exist=True)
        dest = self.ws.paths.resolve(dest_rel)
        dest.parent.mkdir(parents=True, exist_ok=True)
        if source.is_dir():
            shutil.copytree(source, dest)
            self.index_tree(dest_rel)
        else:
            shutil.copy2(source, dest)
            self.index_file(dest_rel)
        self.ws.audit.append(who=getuser(), what="file.copied", target=source_rel, result=dest_rel)
        return dest

    def move(self, source_rel: str, dest_rel: str) -> Path:
        source = self.ws.paths.resolve(source_rel, must_exist=True)
        was_dir = source.is_dir()
        dest = self.ws.paths.resolve(dest_rel)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(source), str(dest))
        self.ws.store.remove_item(source_rel, descendants=True)
        if dest.is_file():
            self.index_file(dest_rel)
        elif was_dir or dest.is_dir():
            self.index_tree(dest_rel)
        self.ws.audit.append(who=getuser(), what="file.moved", target=source_rel, result=dest_rel)
        return dest
