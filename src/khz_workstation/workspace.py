from __future__ import annotations

from getpass import getuser
import json
from pathlib import Path

from .audit import AuditLog
from .models import Classification, WorkspaceInfo
from .security.paths import WorkspacePathResolver
from .store import WorkspaceStore


class Workspace:
    META_DIR = ".khz"

    def __init__(self, root: Path) -> None:
        self.root = root.resolve(strict=True)
        meta = self.root / self.META_DIR / "workspace.json"
        raw = json.loads(meta.read_text(encoding="utf-8"))
        self.info = WorkspaceInfo(
            workspace_id=raw["workspace_id"],
            name=raw["name"],
            root=str(self.root),
            created_utc=raw["created_utc"],
            classification=Classification(raw.get("classification", Classification.INTERNAL.value)),
        )
        self.paths = WorkspacePathResolver(self.root)
        self.store = WorkspaceStore(self.root / self.META_DIR / "metadata.db", self.info.workspace_id)
        self.audit = AuditLog(self.root / self.META_DIR / "audit.jsonl")

    @classmethod
    def create(cls, root: Path, name: str | None = None, classification: Classification = Classification.INTERNAL) -> "Workspace":
        root = root.resolve()
        root.mkdir(parents=True, exist_ok=True)
        meta_dir = root / cls.META_DIR
        meta_dir.mkdir(exist_ok=True)
        if (meta_dir / "workspace.json").exists():
            return cls(root)
        info = WorkspaceInfo.create(name or root.name, str(root), classification)
        (meta_dir / "workspace.json").write_text(json.dumps({
            "workspace_id": info.workspace_id,
            "name": info.name,
            "created_utc": info.created_utc,
            "classification": info.classification.value,
            "schema_version": 1,
        }, indent=2), encoding="utf-8")
        ws = cls(root)
        ws.audit.append(who=getuser(), what="workspace.created", target=".", result="CREATED", verification="workspace metadata persisted")
        return ws

    @classmethod
    def open(cls, root: Path) -> "Workspace":
        if not (root / cls.META_DIR / "workspace.json").exists():
            raise FileNotFoundError("Not a KHZ workspace. Use create first.")
        ws = cls(root)
        ws.audit.append(who=getuser(), what="workspace.opened", target=".", result="OPENED")
        return ws
