from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path

from .models import NetworkMode


@dataclass
class AppSettings:
    locale: str = "en-US"
    profile: str = "Full Workstation"
    ai_enabled: bool = False
    remote_ai_enabled: bool = False
    embeddings_enabled: bool = False
    telemetry_enabled: bool = False
    updates_enabled: bool = False
    git_network_enabled: bool = False
    terminal_enabled: bool = True
    plugins_enabled: bool = False
    macros_enabled: bool = False
    content_indexing_enabled: bool = False
    healthcare_hardened: bool = False
    network_mode: str = NetworkMode.DENY.value
    network_allowlist: list[str] = field(default_factory=list)
    session_timeout_minutes: int = 15

    PROFILES = ("Office", "Developer", "Healthcare Hardened", "Full Workstation")

    def apply_profile(self, profile: str) -> None:
        if profile not in self.PROFILES:
            raise ValueError(f"Unknown profile: {profile}")
        if profile == "Healthcare Hardened":
            self.apply_healthcare_hardened()
            return
        self.healthcare_hardened = False
        self.profile = profile
        self.terminal_enabled = profile != "Office"
        if profile == "Office":
            self.git_network_enabled = False

    def apply_healthcare_hardened(self) -> None:
        self.healthcare_hardened = True
        self.profile = "Healthcare Hardened"
        self.ai_enabled = False
        self.remote_ai_enabled = False
        self.embeddings_enabled = False
        self.telemetry_enabled = False
        self.updates_enabled = False
        self.git_network_enabled = False
        self.terminal_enabled = False
        self.plugins_enabled = False
        self.macros_enabled = False
        self.network_mode = NetworkMode.LOOPBACK_ONLY.value


class SettingsStore:
    def __init__(self, path: Path | None = None) -> None:
        if path is None:
            base = Path(os.getenv("LOCALAPPDATA") or Path.home() / ".config") / "KHZWorkstation"
            path = base / "settings.json"
        self.path = path

    def load(self) -> AppSettings:
        if not self.path.exists():
            return AppSettings()
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
            allowed = AppSettings.__dataclass_fields__
            return AppSettings(**{k: v for k, v in raw.items() if k in allowed})
        except (OSError, ValueError, TypeError):
            return AppSettings()

    def save(self, settings: AppSettings) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(asdict(settings), indent=2, sort_keys=True), encoding="utf-8")
        os.replace(tmp, self.path)
