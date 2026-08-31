from __future__ import annotations

from getpass import getuser
import json
import os
import shutil
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

from .fileops import sha256_file
from .workspace import Workspace


class BackupError(IOError):
    pass


class BackupService:
    MANIFEST = "KHZ-BACKUP-MANIFEST.json"

    def __init__(self, workspace: Workspace) -> None:
        self.ws = workspace

    def create(self, destination: Path) -> Path:
        destination = destination.resolve()
        destination.parent.mkdir(parents=True, exist_ok=True)
        tmp = destination.with_name(destination.name + ".tmp-" + uuid4().hex)
        files: dict[str, str] = {}
        try:
            with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED, allowZip64=True) as zf:
                for path in self.ws.root.rglob("*"):
                    if not path.is_file():
                        continue
                    rel = str(path.relative_to(self.ws.root)).replace("\\", "/")
                    if rel.startswith(".khz/backups/"):
                        continue
                    files[rel] = sha256_file(path)
                    zf.write(path, rel)
                manifest = {
                    "format": "KHZ-WORKSPACE-BACKUP-V1",
                    "workspace_id": self.ws.info.workspace_id,
                    "created_utc": datetime.now(timezone.utc).isoformat(),
                    "files": files,
                }
                zf.writestr(self.MANIFEST, json.dumps(manifest, indent=2, sort_keys=True))
            self.validate(tmp, expected_workspace_id=self.ws.info.workspace_id)
            os.replace(tmp, destination)
        except Exception as exc:
            tmp.unlink(missing_ok=True)
            raise BackupError(str(exc)) from exc
        self.ws.audit.append(who=getuser(), what="backup.created", target=str(destination), result="PUBLISHED_ATOMICALLY", verification=sha256_file(destination))
        return destination

    @classmethod
    def validate(cls, backup: Path, expected_workspace_id: str | None = None) -> dict:
        try:
            with zipfile.ZipFile(backup, "r") as zf:
                names = set(zf.namelist())
                if cls.MANIFEST not in names:
                    raise BackupError("Backup manifest missing.")
                manifest = json.loads(zf.read(cls.MANIFEST))
                if manifest.get("format") != "KHZ-WORKSPACE-BACKUP-V1":
                    raise BackupError("Unknown backup format.")
                if expected_workspace_id and manifest.get("workspace_id") != expected_workspace_id:
                    raise BackupError("Workspace identity mismatch.")
                for name, digest in manifest.get("files", {}).items():
                    pure = Path(name)
                    if pure.is_absolute() or ".." in pure.parts:
                        raise BackupError("Unsafe path in backup.")
                    if name not in names:
                        raise BackupError(f"Missing backup member: {name}")
                    import hashlib
                    if hashlib.sha256(zf.read(name)).hexdigest() != digest:
                        raise BackupError(f"Hash mismatch: {name}")
                return manifest
        except (zipfile.BadZipFile, OSError, ValueError, KeyError) as exc:
            if isinstance(exc, BackupError):
                raise
            raise BackupError(str(exc)) from exc

    @classmethod
    def restore(cls, backup: Path, destination: Path, preserve_existing: bool = True) -> tuple[Path, Path | None]:
        manifest = cls.validate(backup)
        destination = destination.resolve()
        parent = destination.parent
        parent.mkdir(parents=True, exist_ok=True)
        stage = Path(tempfile.mkdtemp(prefix=destination.name + ".restore-stage-", dir=parent))
        preserved: Path | None = None
        try:
            with zipfile.ZipFile(backup, "r") as zf:
                for info in zf.infolist():
                    if info.filename == cls.MANIFEST:
                        continue
                    rel = Path(info.filename)
                    if rel.is_absolute() or ".." in rel.parts:
                        raise BackupError("Unsafe path in backup.")
                    target = stage / rel
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with zf.open(info) as src, target.open("wb") as dst:
                        shutil.copyfileobj(src, dst)
            for rel, digest in manifest["files"].items():
                if sha256_file(stage / rel) != digest:
                    raise BackupError(f"Staged restore hash mismatch: {rel}")
            if destination.exists():
                if not preserve_existing:
                    raise BackupError("Destination exists and preservation is required by policy.")
                preserved = parent / f"{destination.name}.pre-restore-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}"
                os.replace(destination, preserved)
            os.replace(stage, destination)
            return destination, preserved
        except Exception:
            if stage.exists():
                shutil.rmtree(stage, ignore_errors=True)
            if preserved and preserved.exists() and not destination.exists():
                os.replace(preserved, destination)
            raise
