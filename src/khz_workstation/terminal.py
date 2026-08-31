from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class CommandResult:
    command: str
    working_directory: str
    exit_code: int
    stdout: str
    stderr: str
    timed_out: bool = False


def default_shell() -> list[str]:
    if os.name == "nt":
        pwsh = shutil.which("pwsh") or shutil.which("powershell")
        if pwsh:
            return [pwsh, "-NoLogo", "-NoProfile", "-Command"]
        return [os.environ.get("COMSPEC", "cmd.exe"), "/d", "/s", "/c"]
    shell = os.environ.get("SHELL") or "/bin/sh"
    return [shell, "-lc"]


class TerminalService:
    def __init__(self, workspace_root: Path, enabled: bool = True) -> None:
        self.workspace_root = workspace_root.resolve()
        self.enabled = enabled

    def run(self, command: str, *, authorized: bool, timeout: int = 60, env: dict[str, str] | None = None) -> CommandResult:
        if not self.enabled:
            raise PermissionError("Terminal is disabled by policy.")
        if not authorized:
            raise PermissionError("Command execution requires explicit authorization.")
        if not command.strip():
            raise ValueError("Command is empty.")
        process_env = os.environ.copy()
        if env:
            process_env.update(env)
        try:
            cp = subprocess.run([*default_shell(), command], cwd=self.workspace_root, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=max(1, min(timeout, 3600)), env=process_env)
            return CommandResult(command, str(self.workspace_root), cp.returncode, cp.stdout, cp.stderr, False)
        except subprocess.TimeoutExpired as exc:
            return CommandResult(command, str(self.workspace_root), 124, exc.stdout or "", exc.stderr or "", True)
