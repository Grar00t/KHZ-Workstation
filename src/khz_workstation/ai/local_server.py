from __future__ import annotations

import contextlib
import json
import os
import secrets
import shutil
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request

from dataclasses import asdict, dataclass, replace
from pathlib import Path
from typing import Any
from uuid import uuid4

from ..models import utc_now
from ..workspace import Workspace
from .model_manager import InstalledModel, ModelManager, ModelManagerError
from .process_containment import ProcessContainmentError, contain_process_tree


class LocalAiServerError(RuntimeError):
    pass


@dataclass(frozen=True)
class LocalAiSession:
    schema_version: int
    session_id: str
    state: str
    endpoint: str
    api_token: str
    process_id: int
    process_containment: str
    model_family: str
    model_display_name: str
    model_sha256: str
    workspace_id: str
    workspace_root: str
    started_utc: str
    ready_utc: str | None = None


def default_runtime_root() -> Path:
    local_app_data = os.getenv("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / "KHZ" / "runtime"
    runtime_dir = os.getenv("XDG_RUNTIME_DIR")
    if runtime_dir:
        return Path(runtime_dir) / "khz"
    return Path.home() / ".local" / "state" / "khz" / "runtime"


def _write_json_atomically(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    try:
        descriptor = os.open(
            temporary,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL,
            0o600,
        )
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            json.dump(value, output, indent=2, sort_keys=True)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        with contextlib.suppress(OSError):
            path.chmod(0o600)
    finally:
        temporary.unlink(missing_ok=True)


def _resolve_server(configured: Path | None) -> Path:
    if configured is not None:
        candidate = configured
    else:
        setting = os.getenv("KHZ_LLAMA_SERVER")
        discovered = (
            setting
            or shutil.which("llama-server")
            or shutil.which("llama-server.exe")
        )
        if not discovered:
            raise LocalAiServerError(
                "llama-server was not found. Install current llama.cpp tools "
                "or set KHZ_LLAMA_SERVER."
            )
        candidate = Path(discovered)
    try:
        resolved = candidate.resolve(strict=True)
    except OSError as exc:
        raise LocalAiServerError("Configured llama-server path does not exist.") from exc
    if not resolved.is_file():
        raise LocalAiServerError("Configured llama-server path is not a file.")
    return resolved


def build_mcp_configuration(workspace: Workspace) -> dict[str, Any]:
    return {
        "mcpServers": {
            "khz-workspace": {
                "command": sys.executable,
                "args": [
                    "-m",
                    "khz_workstation.ai.workspace_mcp",
                    "--workspace",
                    str(workspace.root),
                    "--workspace-id",
                    workspace.info.workspace_id,
                ],
                "cwd": str(workspace.root),
                "env": {
                    "PYTHONIOENCODING": "utf-8",
                    "PYTHONUNBUFFERED": "1",
                    "PYTHONUTF8": "1",
                    "LLAMA_API_KEY": "",
                },
                "timeout_ms": 30_000,
            }
        }
    }


def build_server_environment(
    api_token: str,
    environ: dict[str, str] | None = None,
) -> dict[str, str]:
    source = os.environ if environ is None else environ
    allowed_names = {
        "APPDATA",
        "CUDA_VISIBLE_DEVICES",
        "GGML_CUDA_ENABLE_UNIFIED_MEMORY",
        "HIP_VISIBLE_DEVICES",
        "HOME",
        "LANG",
        "LC_ALL",
        "LOCALAPPDATA",
        "NUMBER_OF_PROCESSORS",
        "PATH",
        "PATHEXT",
        "PROGRAMDATA",
        "PYTHONPATH",
        "SYSTEMROOT",
        "TEMP",
        "TMP",
        "TMPDIR",
        "USERPROFILE",
        "WINDIR",
    }
    sanitized = {
        name: value
        for name, value in source.items()
        if name.upper() in allowed_names
    }
    sanitized["LLAMA_API_KEY"] = api_token
    return sanitized


class LocalAiServer:
    def __init__(
        self,
        *,
        model_manager: ModelManager | None = None,
        runtime_root: Path | None = None,
        server_binary: Path | None = None,
    ) -> None:
        self.model_manager = model_manager or ModelManager()
        self.runtime_root = (runtime_root or default_runtime_root()).resolve()
        self.server_binary = server_binary

    @property
    def session_path(self) -> Path:
        return self.runtime_root / "ai-session.json"

    def serve(
        self,
        family: str,
        *,
        workspace_root: Path,
        port: int = 8091,
        context_size: int | None = None,
        readiness_timeout_seconds: float = 120.0,
    ) -> int:
        if port < 1024 or port > 65535:
            raise LocalAiServerError("Port must be between 1024 and 65535.")
        if context_size is not None and not 512 <= context_size <= 131_072:
            raise LocalAiServerError(
                "Context size must be between 512 and 131072 tokens."
            )

        try:
            workspace = Workspace.open(workspace_root)
            model = self.model_manager.require_verified(family)
        except (OSError, ValueError, ModelManagerError) as exc:
            raise LocalAiServerError(str(exc)) from exc

        server = _resolve_server(self.server_binary)
        self.runtime_root.mkdir(parents=True, exist_ok=True)
        session_id = str(uuid4())
        mcp_path = self.runtime_root / f"mcp-{session_id}.json"
        _write_json_atomically(mcp_path, build_mcp_configuration(workspace))

        endpoint = f"http://127.0.0.1:{port}"
        token = secrets.token_urlsafe(48)
        environment = build_server_environment(token)
        command = self._command(
            server,
            model,
            mcp_path,
            port,
            context_size or model.context_size,
        )
        process: subprocess.Popen[bytes] | None = None
        try:
            process = subprocess.Popen(
                command,
                cwd=workspace.root,
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=None,
                stderr=None,
                start_new_session=os.name != "nt",
            )
            with contain_process_tree(process) as containment:
                session = LocalAiSession(
                    schema_version=1,
                    session_id=session_id,
                    state="STARTING",
                    endpoint=endpoint,
                    api_token=token,
                    process_id=process.pid,
                    process_containment=containment,
                    model_family=model.family,
                    model_display_name=model.display_name,
                    model_sha256=model.sha256,
                    workspace_id=workspace.info.workspace_id,
                    workspace_root=str(workspace.root),
                    started_utc=utc_now(),
                )
                self._publish_session(session)
                self._wait_until_ready(
                    process,
                    endpoint,
                    token,
                    readiness_timeout_seconds,
                )
                ready = replace(session, state="READY", ready_utc=utc_now())
                self._publish_session(ready)
                print(
                    f"KHZ local AI ready: {model.display_name} at {endpoint}",
                    flush=True,
                )
                print(
                    "Open Assistant in KHZ Workstation. Press Ctrl+C to stop.",
                    flush=True,
                )
                return_code = process.wait()
                if return_code != 0:
                    raise LocalAiServerError(
                        f"llama-server exited with code {return_code}."
                    )
                return return_code
        except KeyboardInterrupt:
            if process is not None:
                self._terminate(process)
            return 130
        except ProcessContainmentError as exc:
            if process is not None:
                self._terminate(process)
            raise LocalAiServerError(
                "The local model process could not be contained."
            ) from exc
        finally:
            if process is not None and process.poll() is None:
                self._terminate(process)
            self._remove_owned_session(session_id)
            mcp_path.unlink(missing_ok=True)

    @staticmethod
    def _command(
        server: Path,
        model: InstalledModel,
        mcp_path: Path,
        port: int,
        context_size: int,
    ) -> list[str]:
        return [
            str(server),
            "--model",
            model.path,
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--ctx-size",
            str(context_size),
            "--parallel",
            "1",
            "--jinja",
            "--no-agent",
            "--no-slots",
            "--cors-origins",
            f"http://127.0.0.1:{port}",
            "--mcp-servers-config",
            str(mcp_path),
        ]

    def _publish_session(self, session: LocalAiSession) -> None:
        _write_json_atomically(self.session_path, asdict(session))

    @staticmethod
    def _wait_until_ready(
        process: subprocess.Popen[bytes],
        endpoint: str,
        token: str,
        timeout_seconds: float,
    ) -> None:
        deadline = time.monotonic() + timeout_seconds
        health = endpoint + "/health"
        while time.monotonic() < deadline:
            code = process.poll()
            if code is not None:
                raise LocalAiServerError(
                    f"llama-server exited during startup with code {code}."
                )
            request = urllib.request.Request(
                health,
                headers={"Authorization": f"Bearer {token}"},
                method="GET",
            )
            try:
                with urllib.request.urlopen(request, timeout=1.0) as response:
                    if response.status == 200:
                        return
            except (OSError, urllib.error.HTTPError, urllib.error.URLError):
                pass
            time.sleep(0.2)
        raise LocalAiServerError("llama-server did not become ready before timeout.")

    @staticmethod
    def _terminate(process: subprocess.Popen[bytes]) -> None:
        if process.poll() is not None:
            return
        try:
            if os.name == "nt":
                process.terminate()
            else:
                os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=10)
        except (OSError, subprocess.TimeoutExpired):
            if process.poll() is None:
                try:
                    if os.name == "nt":
                        process.kill()
                    else:
                        os.killpg(process.pid, signal.SIGKILL)
                except OSError:
                    pass
                with contextlib.suppress(subprocess.TimeoutExpired):
                    process.wait(timeout=5)

    def _remove_owned_session(self, session_id: str) -> None:
        try:
            raw = json.loads(self.session_path.read_text(encoding="utf-8"))
            if raw.get("session_id") == session_id:
                self.session_path.unlink(missing_ok=True)
        except (OSError, ValueError, json.JSONDecodeError):
            return
