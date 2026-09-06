from __future__ import annotations

import argparse
import json
import os
import sys

from pathlib import Path
from typing import Any, Callable

from .proposals import ProposalError, create_write_proposal
from ..workspace import Workspace


MAX_READ_CHARACTERS = 100_000
MAX_SEARCH_FILE_BYTES = 1024 * 1024
IGNORED_DIRECTORIES = {
    ".git",
    ".khz",
    ".venv",
    "bin",
    "node_modules",
    "obj",
}
TEXT_SUFFIXES = {
    ".c",
    ".cc",
    ".cpp",
    ".cs",
    ".css",
    ".csv",
    ".h",
    ".hpp",
    ".html",
    ".ini",
    ".java",
    ".js",
    ".json",
    ".md",
    ".py",
    ".rs",
    ".sh",
    ".sql",
    ".toml",
    ".ts",
    ".tsx",
    ".txt",
    ".xaml",
    ".xml",
    ".yaml",
    ".yml",
}


class WorkspaceMcp:
    def __init__(self, workspace: Workspace) -> None:
        self.workspace = workspace
        self.tools: dict[str, Callable[[dict[str, Any]], Any]] = {
            "workspace_list": self.workspace_list,
            "workspace_read_text": self.workspace_read_text,
            "workspace_search_text": self.workspace_search_text,
            "workspace_propose_write_text": self.workspace_propose_write_text,
        }

    def tool_descriptions(self) -> list[dict[str, Any]]:
        return [
            {
                "name": "workspace_list",
                "description": (
                    "List files and folders directly inside a bounded workspace-relative directory. "
                    "KHZ metadata is never exposed."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "path": {"type": "string", "default": ""},
                        "max_entries": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 500,
                            "default": 200,
                        },
                    },
                    "additionalProperties": False,
                },
            },
            {
                "name": "workspace_read_text",
                "description": (
                    "Read a bounded UTF-8 text file from the active workspace. "
                    "Binary files and .khz metadata are denied."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "path": {"type": "string"},
                        "max_characters": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": MAX_READ_CHARACTERS,
                            "default": MAX_READ_CHARACTERS,
                        },
                    },
                    "required": ["path"],
                    "additionalProperties": False,
                },
            },
            {
                "name": "workspace_search_text",
                "description": (
                    "Search bounded text files inside the active workspace. "
                    "Network access, Git metadata, and KHZ metadata are excluded."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "query": {"type": "string"},
                        "path": {"type": "string", "default": ""},
                        "max_results": {
                            "type": "integer",
                            "minimum": 1,
                            "maximum": 100,
                            "default": 50,
                        },
                    },
                    "required": ["query"],
                    "additionalProperties": False,
                },
            },
            {
                "name": "workspace_propose_write_text",
                "description": (
                    "Create a pending text-write proposal for explicit user review. "
                    "This tool never modifies the target file and cannot approve its own proposal."
                ),
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "path": {"type": "string"},
                        "content": {"type": "string", "maxLength": 200000},
                        "expected_sha256": {
                            "type": ["string", "null"],
                            "pattern": "^[0-9a-f]{64}$",
                        },
                    },
                    "required": ["path", "content"],
                    "additionalProperties": False,
                },
            },
        ]

    def _safe_path(
        self,
        value: str,
        *,
        must_exist: bool,
    ) -> Path:
        relative = Path(value or ".")
        if any(part.casefold() in IGNORED_DIRECTORIES for part in relative.parts):
            raise PermissionError("Protected metadata is not available to the model.")
        return self.workspace.paths.resolve(relative, must_exist=must_exist)

    @staticmethod
    def _bounded_int(
        value: Any,
        default: int,
        minimum: int,
        maximum: int,
    ) -> int:
        result = default if value is None else int(value)
        if result < minimum or result > maximum:
            raise ValueError(f"value must be between {minimum} and {maximum}")
        return result

    def workspace_list(self, arguments: dict[str, Any]) -> Any:
        if set(arguments) - {"path", "max_entries"}:
            raise ValueError("Unknown workspace_list argument.")
        directory = self._safe_path(str(arguments.get("path", "")), must_exist=True)
        if not directory.is_dir():
            raise NotADirectoryError("Requested workspace path is not a directory.")
        maximum = self._bounded_int(arguments.get("max_entries"), 200, 1, 500)
        entries = []
        for path in sorted(directory.iterdir(), key=lambda item: item.name.casefold()):
            if path.name.casefold() in IGNORED_DIRECTORIES:
                continue
            relative_path = path.relative_to(self.workspace.root)
            try:
                safe_path = self.workspace.paths.resolve(
                    relative_path,
                    must_exist=True,
                )
            except (OSError, ValueError):
                continue
            entries.append(
                {
                    "path": relative_path.as_posix(),
                    "type": "directory" if safe_path.is_dir() else "file",
                    "size_bytes": (
                        None if safe_path.is_dir() else safe_path.stat().st_size
                    ),
                }
            )
            if len(entries) >= maximum:
                break
        return {"entries": entries, "truncated": len(entries) >= maximum}

    def workspace_read_text(self, arguments: dict[str, Any]) -> Any:
        if set(arguments) - {"path", "max_characters"}:
            raise ValueError("Unknown workspace_read_text argument.")
        path = self._safe_path(str(arguments.get("path", "")), must_exist=True)
        if not path.is_file():
            raise FileNotFoundError("Requested workspace path is not a file.")
        maximum = self._bounded_int(
            arguments.get("max_characters"),
            MAX_READ_CHARACTERS,
            1,
            MAX_READ_CHARACTERS,
        )
        if path.stat().st_size > MAX_SEARCH_FILE_BYTES:
            raise ValueError("Text file exceeds the 1 MiB read boundary.")
        raw = path.read_bytes()
        if b"\0" in raw:
            raise ValueError("Binary files are not available through this tool.")
        text = raw.decode("utf-8")
        truncated = len(text) > maximum
        return {
            "path": path.relative_to(self.workspace.root).as_posix(),
            "content": text[:maximum],
            "truncated": truncated,
        }

    def workspace_search_text(self, arguments: dict[str, Any]) -> Any:
        if set(arguments) - {"query", "path", "max_results"}:
            raise ValueError("Unknown workspace_search_text argument.")
        query = str(arguments.get("query", ""))
        if not query or len(query) > 500:
            raise ValueError("Search query must contain 1 to 500 characters.")
        root = self._safe_path(str(arguments.get("path", "")), must_exist=True)
        if not root.is_dir():
            raise NotADirectoryError("Search root is not a directory.")
        maximum = self._bounded_int(arguments.get("max_results"), 50, 1, 100)
        matches: list[dict[str, Any]] = []
        for current_root, directories, files in os.walk(root):
            directories[:] = [
                name
                for name in directories
                if name.casefold() not in IGNORED_DIRECTORIES
            ]
            for name in sorted(files):
                path = Path(current_root) / name
                if path.suffix.casefold() not in TEXT_SUFFIXES:
                    continue
                try:
                    relative_path = path.relative_to(self.workspace.root)
                    safe_path = self.workspace.paths.resolve(
                        relative_path,
                        must_exist=True,
                    )
                    if (
                        not safe_path.is_file()
                        or safe_path.stat().st_size > MAX_SEARCH_FILE_BYTES
                    ):
                        continue
                    for line_number, line in enumerate(
                        safe_path.read_text(encoding="utf-8").splitlines(),
                        start=1,
                    ):
                        column = line.casefold().find(query.casefold())
                        if column < 0:
                            continue
                        matches.append(
                            {
                                "path": relative_path.as_posix(),
                                "line": line_number,
                                "column": column + 1,
                                "preview": line[:500],
                            }
                        )
                        if len(matches) >= maximum:
                            return {"matches": matches, "truncated": True}
                except (OSError, UnicodeDecodeError, ValueError):
                    continue
        return {"matches": matches, "truncated": False}

    def workspace_propose_write_text(self, arguments: dict[str, Any]) -> Any:
        if set(arguments) - {"path", "content", "expected_sha256"}:
            raise ValueError("Unknown workspace_propose_write_text argument.")
        proposal = create_write_proposal(
            self.workspace,
            target=str(arguments.get("path", "")),
            content=arguments.get("content"),
            expected_sha256=arguments.get("expected_sha256"),
        )
        return {
            "proposal_id": proposal.proposal_id,
            "status": proposal.status,
            "target": proposal.target,
            "target_modified": False,
            "next_step": "Review and approve in KHZ Workstation.",
        }

    def handle(self, request: dict[str, Any]) -> dict[str, Any] | None:
        request_id = request.get("id")
        method = request.get("method")
        if method == "notifications/initialized":
            return None
        if method == "initialize":
            return {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "protocolVersion": "2025-06-18",
                    "capabilities": {"tools": {"listChanged": False}},
                    "serverInfo": {"name": "khz-workspace", "version": "0.1.0"},
                },
            }
        if method == "ping":
            return {"jsonrpc": "2.0", "id": request_id, "result": {}}
        if method == "tools/list":
            return {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {"tools": self.tool_descriptions()},
            }
        if method == "tools/call":
            params = request.get("params")
            if not isinstance(params, dict):
                return self._error(request_id, -32602, "Invalid tool parameters.")
            name = params.get("name")
            arguments = params.get("arguments", {})
            if name not in self.tools or not isinstance(arguments, dict):
                return self._error(request_id, -32602, "Unknown tool or arguments.")
            try:
                result = self.tools[name](arguments)
                return {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "result": {
                        "content": [
                            {
                                "type": "text",
                                "text": json.dumps(
                                    result,
                                    ensure_ascii=False,
                                    separators=(",", ":"),
                                ),
                            }
                        ],
                        "isError": False,
                    },
                }
            except (
                OSError,
                PermissionError,
                ProposalError,
                TypeError,
                ValueError,
            ) as exc:
                return {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "result": {
                        "content": [{"type": "text", "text": str(exc)}],
                        "isError": True,
                    },
                }
        return self._error(request_id, -32601, "Method not found.")

    @staticmethod
    def _error(request_id: Any, code: int, message: str) -> dict[str, Any]:
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "error": {"code": code, "message": message},
        }


def run_stdio(server: WorkspaceMcp) -> None:
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise ValueError("request must be an object")
            response = server.handle(request)
        except (ValueError, json.JSONDecodeError) as exc:
            response = WorkspaceMcp._error(None, -32700, str(exc))
        if response is not None:
            sys.stdout.write(
                json.dumps(response, ensure_ascii=False, separators=(",", ":"))
                + "\n"
            )
            sys.stdout.flush()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="KHZ bounded workspace MCP server")
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--workspace-id", required=True)
    args = parser.parse_args(argv)

    workspace = Workspace.open(args.workspace)
    if workspace.info.workspace_id != args.workspace_id:
        raise SystemExit("Workspace identity mismatch.")
    run_stdio(WorkspaceMcp(workspace))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
