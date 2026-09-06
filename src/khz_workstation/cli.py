from __future__ import annotations

import argparse
import sys

from pathlib import Path

from .ai.local_server import LocalAiServer, LocalAiServerError
from .ai.model_manager import (
    ModelLicenseAcceptanceRequired,
    ModelManager,
    ModelManagerError,
)


def _path(value: str) -> Path:
    return Path(value).expanduser()


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="khz",
        description="KHZ local-first workstation command line",
    )
    subcommands = parser.add_subparsers(dest="command", required=True)

    subcommands.add_parser("catalog", help="List supported local model families")

    pull = subcommands.add_parser(
        "pull",
        help="Download and verify a GGUF model with llama-download",
    )
    pull.add_argument("family", choices=("llama", "lama", "qwen", "phi"))
    pull.add_argument("--accept-license", action="store_true")
    pull.add_argument("--models-dir", type=_path)
    pull.add_argument("--downloader", type=_path)

    models = subcommands.add_parser("models", help="List installed KHZ models")
    models.add_argument("--models-dir", type=_path)

    serve = subcommands.add_parser(
        "serve",
        help="Run a verified model for one KHZ workspace",
    )
    serve.add_argument("family", choices=("llama", "lama", "qwen", "phi"))
    serve.add_argument("--workspace", type=_path, required=True)
    serve.add_argument("--models-dir", type=_path)
    serve.add_argument("--server", type=_path)
    serve.add_argument("--runtime-dir", type=_path)
    serve.add_argument("--port", type=int, default=8091)
    serve.add_argument("--context-size", type=int)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.command == "catalog":
            for spec in ModelManager().catalog():
                acceptance = (
                    "explicit acceptance required"
                    if spec.requires_license_acceptance
                    else "included license"
                )
                print(
                    f"{spec.family:<5} {spec.display_name} · {spec.quantization} · "
                    f"{spec.license_name} ({acceptance})"
                )
            return 0

        if args.command == "pull":
            manager = ModelManager(args.models_dir, args.downloader)
            installed = manager.pull(
                args.family,
                accept_license=args.accept_license,
            )
            print(
                f"Installed {installed.display_name}\n"
                f"Path: {installed.path}\n"
                f"SHA-256: {installed.sha256}"
            )
            return 0

        if args.command == "models":
            installed = ModelManager(args.models_dir).list_installed()
            if not installed:
                print("No KHZ local models are installed.")
                return 0
            for model in installed:
                gib = model.size_bytes / (1024 ** 3)
                print(
                    f"{model.family:<5} {model.display_name} · {gib:.2f} GiB · "
                    f"sha256:{model.sha256[:12]}"
                )
            return 0

        manager = ModelManager(args.models_dir)
        server = LocalAiServer(
            model_manager=manager,
            runtime_root=args.runtime_dir,
            server_binary=args.server,
        )
        return server.serve(
            args.family,
            workspace_root=args.workspace,
            port=args.port,
            context_size=args.context_size,
        )
    except (
        LocalAiServerError,
        ModelLicenseAcceptanceRequired,
        ModelManagerError,
        OSError,
        ValueError,
    ) as exc:
        print(f"khz: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
