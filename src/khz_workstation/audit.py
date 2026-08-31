from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any

from .models import utc_now


class AuditWriteError(IOError):
    pass


class AuditLog:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.path.parent.mkdir(parents=True, exist_ok=True)

    def _last_hash(self) -> str:
        if not self.path.exists() or self.path.stat().st_size == 0:
            return "0" * 64
        last = ""
        with self.path.open("r", encoding="utf-8") as fh:
            for line in fh:
                if line.strip():
                    last = line
        if not last:
            return "0" * 64
        return json.loads(last)["hash"]

    def append(self, *, who: str, what: str, target: str, intent: str = "", approval: str = "N/A", execution: str = "", result: str = "", verification: str = "", metadata: dict[str, Any] | None = None) -> dict[str, Any]:
        event = {
            "when": utc_now(),
            "who": who,
            "what": what,
            "target": target,
            "intent": intent,
            "approval": approval,
            "execution": execution,
            "result": result,
            "verification": verification,
            "metadata": metadata or {},
            "prev_hash": self._last_hash(),
        }
        canonical = json.dumps(event, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        event["hash"] = hashlib.sha256(canonical).hexdigest()
        try:
            with self.path.open("a", encoding="utf-8", newline="\n") as fh:
                fh.write(json.dumps(event, sort_keys=True, ensure_ascii=False) + "\n")
                fh.flush()
                os.fsync(fh.fileno())
        except OSError as exc:
            raise AuditWriteError(str(exc)) from exc
        return event

    def read(self, limit: int = 200) -> list[dict[str, Any]]:
        if not self.path.exists():
            return []
        rows = [json.loads(line) for line in self.path.read_text(encoding="utf-8").splitlines() if line.strip()]
        return rows[-limit:]

    def verify_chain(self) -> tuple[bool, str]:
        prev = "0" * 64
        if not self.path.exists():
            return True, "empty"
        for index, line in enumerate(self.path.read_text(encoding="utf-8").splitlines(), start=1):
            if not line.strip():
                continue
            event = json.loads(line)
            recorded_hash = event.pop("hash", None)
            if event.get("prev_hash") != prev:
                return False, f"prev_hash mismatch at line {index}"
            canonical = json.dumps(event, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
            expected = hashlib.sha256(canonical).hexdigest()
            if recorded_hash != expected:
                return False, f"hash mismatch at line {index}"
            prev = recorded_hash
        return True, prev
