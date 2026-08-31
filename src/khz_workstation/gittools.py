from __future__ import annotations

import subprocess
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class GitResult:
    exit_code: int
    stdout: str
    stderr: str


class GitService:
    def __init__(self, root: Path) -> None:
        self.root = root.resolve()

    def _run(self, args: list[str], timeout: int = 20) -> GitResult:
        cp = subprocess.run(["git", "-C", str(self.root), *args], capture_output=True, text=True, timeout=timeout, encoding="utf-8", errors="replace")
        return GitResult(cp.returncode, cp.stdout, cp.stderr)

    def is_repository(self) -> bool:
        return self._run(["rev-parse", "--is-inside-work-tree"]).stdout.strip() == "true"

    def current_branch(self) -> str:
        return self._run(["branch", "--show-current"]).stdout.strip()

    def status(self) -> GitResult:
        return self._run(["status", "--short", "--branch"])

    def diff(self) -> GitResult:
        return self._run(["diff", "--no-ext-diff"])

    def history(self, limit: int = 30) -> GitResult:
        return self._run(["log", f"-{max(1, min(limit, 100))}", "--date=iso-strict", "--pretty=format:%h %ad %an %s"])

    def file_history(self, relative_path: str, limit: int = 30) -> GitResult:
        return self._run(["log", f"-{max(1, min(limit, 100))}", "--follow", "--pretty=format:%h %ad %an %s", "--", relative_path])

    def stage(self, paths: list[str], authorized: bool) -> GitResult:
        if not authorized:
            raise PermissionError("Git write requires explicit authorization.")
        return self._run(["add", "--", *paths])

    def unstage(self, paths: list[str], authorized: bool) -> GitResult:
        if not authorized:
            raise PermissionError("Git write requires explicit authorization.")
        return self._run(["restore", "--staged", "--", *paths])

    def commit(self, message: str, authorized: bool) -> GitResult:
        if not authorized:
            raise PermissionError("Git write requires explicit authorization.")
        if not message.strip():
            raise ValueError("Commit message is required.")
        return self._run(["commit", "-m", message])

    def network(self, operation: str, authorized: bool, policy_enabled: bool) -> GitResult:
        if operation not in {"fetch", "pull", "push"}:
            raise ValueError("Unsupported Git network operation.")
        if not authorized or not policy_enabled:
            raise PermissionError("Git network operation denied by policy or missing authorization.")
        return self._run([operation], timeout=120)
