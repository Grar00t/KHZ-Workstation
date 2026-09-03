from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

from .security.approval import Approval, ApprovalLedger, subject_digest
from .security.paths import WorkspacePathResolver

MAX_OUTPUT_BYTES = 1_000_000

# Only these variables reach a child shell. Everything else in the parent
# environment -- API tokens, cloud credentials, proxy overrides, model keys --
# is withheld from the process the user just approved. Names are matched
# case-insensitively because Windows stores them in mixed case.
ENV_ALLOWLIST: tuple[str, ...] = (
    "PATH",
    "PATHEXT",
    "COMSPEC",
    "SYSTEMDRIVE",
    "SYSTEMROOT",
    "WINDIR",
    "PROGRAMDATA",
    "PROGRAMFILES",
    "PROGRAMFILES(X86)",
    "APPDATA",
    "LOCALAPPDATA",
    "USERPROFILE",
    "HOMEDRIVE",
    "HOMEPATH",
    "PSMODULEPATH",
    "HOME",
    "SHELL",
    "TEMP",
    "TMP",
    "TMPDIR",
    "LANG",
    "LC_ALL",
    "TZ",
    "NUMBER_OF_PROCESSORS",
    "PROCESSOR_ARCHITECTURE",
)


@dataclass(frozen=True)
class CommandResult:
    command: str
    working_directory: str
    exit_code: int
    stdout: str
    stderr: str
    timed_out: bool = False
    command_sha256: str = ""
    approval_binding: str = "NONE"
    truncated: bool = False


def default_shell() -> list[str]:
    if os.name == "nt":
        pwsh = shutil.which("pwsh") or shutil.which("powershell")
        if pwsh:
            return [pwsh, "-NoLogo", "-NoProfile", "-Command"]
        return [os.environ.get("COMSPEC", "cmd.exe"), "/d", "/s", "/c"]
    shell = os.environ.get("SHELL") or "/bin/sh"
    return [shell, "-lc"]


class TerminalService:
    def __init__(
        self,
        workspace_root: Path,
        enabled: bool = True,
        *,
        require_approval: bool = False,
        max_output_bytes: int = MAX_OUTPUT_BYTES,
        env_allowlist: tuple[str, ...] | None = None,
        ledger: ApprovalLedger | None = None,
    ) -> None:
        # Resolve through the workspace boundary rather than a bare
        # Path.resolve(): a missing or non-directory root now fails here, and
        # the same resolver vets any subdirectory a caller asks to run in.
        self.paths = WorkspacePathResolver(Path(workspace_root))
        self.workspace_root = self.paths.root
        self.enabled = enabled
        self.require_approval = require_approval
        self.max_output_bytes = max(1, int(max_output_bytes))
        self.env_allowlist = ENV_ALLOWLIST if env_allowlist is None else env_allowlist
        self.ledger = ApprovalLedger() if ledger is None else ledger

    def build_env(self, env: dict[str, str] | None = None) -> dict[str, str]:
        allowed = {name.upper() for name in self.env_allowlist}
        process_env = {key: value for key, value in os.environ.items() if key.upper() in allowed}
        if env:
            process_env.update(env)
        return process_env

    def _cap(self, text: str | None) -> tuple[str, bool]:
        value = text or ""
        raw = value.encode("utf-8", errors="replace")
        if len(raw) <= self.max_output_bytes:
            return value, False
        clipped = raw[: self.max_output_bytes].decode("utf-8", errors="ignore")
        notice = f"\n[khz: output truncated at {self.max_output_bytes} bytes]\n"
        return clipped + notice, True

    def _authorize(self, command: str, authorized: bool, approval: Approval | None) -> str:
        if approval is not None:
            # Raises if the digest does not cover this exact command, or if this
            # approval was already spent.
            self.ledger.consume(approval, command)
            return "DIGEST_BOUND"
        if self.require_approval:
            raise PermissionError(
                "This terminal requires an Approval bound to the exact command; a "
                "boolean authorization flag is not accepted."
            )
        if authorized:
            return "LEGACY_BOOLEAN"
        raise PermissionError("Command execution requires explicit authorization.")

    def run(
        self,
        command: str,
        *,
        authorized: bool = False,
        approval: Approval | None = None,
        timeout: int = 60,
        env: dict[str, str] | None = None,
        working_subdirectory: str | None = None,
    ) -> CommandResult:
        if not self.enabled:
            raise PermissionError("Terminal is disabled by policy.")
        if not command.strip():
            raise ValueError("Command is empty.")
        binding = self._authorize(command, authorized, approval)
        digest = subject_digest(command)
        cwd = (
            self.paths.resolve(working_subdirectory, must_exist=True)
            if working_subdirectory
            else self.workspace_root
        )
        process_env = self.build_env(env)
        try:
            cp = subprocess.run(
                [*default_shell(), command],
                cwd=cwd,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=max(1, min(timeout, 3600)),
                env=process_env,
            )
        except subprocess.TimeoutExpired as exc:
            out, out_cut = self._cap(exc.stdout if isinstance(exc.stdout, str) else "")
            err, err_cut = self._cap(exc.stderr if isinstance(exc.stderr, str) else "")
            return CommandResult(
                command, str(cwd), 124, out, err, True, digest, binding, out_cut or err_cut
            )
        out, out_cut = self._cap(cp.stdout)
        err, err_cut = self._cap(cp.stderr)
        return CommandResult(
            command, str(cwd), cp.returncode, out, err, False, digest, binding, out_cut or err_cut
        )
