from __future__ import annotations

import hashlib
import hmac
import json
import os
import shutil
import subprocess

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from ..models import utc_now


class ModelManagerError(RuntimeError):
    pass


class ModelLicenseAcceptanceRequired(ModelManagerError):
    pass


@dataclass(frozen=True)
class ModelSpec:
    family: str
    display_name: str
    hf_spec: str
    quantization: str
    context_size: int
    license_name: str
    license_url: str
    source_url: str
    requires_license_acceptance: bool = False


@dataclass(frozen=True)
class InstalledModel:
    family: str
    display_name: str
    source: str
    source_url: str
    quantization: str
    context_size: int
    license_name: str
    license_url: str
    license_accepted: bool
    path: str
    size_bytes: int
    sha256: str
    installed_utc: str


MODEL_CATALOG: dict[str, ModelSpec] = {
    "llama": ModelSpec(
        family="llama",
        display_name="Llama 3.2 3B Instruct",
        hf_spec=(
            "hugging-quants/"
            "Llama-3.2-3B-Instruct-Q4_K_M-GGUF:Q4_K_M"
        ),
        quantization="Q4_K_M",
        context_size=8192,
        license_name="Llama 3.2 Community License",
        license_url="https://www.llama.com/llama3_2/license/",
        source_url=(
            "https://huggingface.co/hugging-quants/"
            "Llama-3.2-3B-Instruct-Q4_K_M-GGUF"
        ),
        requires_license_acceptance=True,
    ),
    "qwen": ModelSpec(
        family="qwen",
        display_name="Qwen3 4B",
        hf_spec="Qwen/Qwen3-4B-GGUF:Q4_K_M",
        quantization="Q4_K_M",
        context_size=8192,
        license_name="Apache-2.0",
        license_url=(
            "https://huggingface.co/Qwen/Qwen3-4B-GGUF/"
            "blob/main/LICENSE"
        ),
        source_url="https://huggingface.co/Qwen/Qwen3-4B-GGUF",
    ),
    "phi": ModelSpec(
        family="phi",
        display_name="Phi-4 Mini Instruct",
        hf_spec="bartowski/microsoft_Phi-4-mini-instruct-GGUF:Q4_K_M",
        quantization="Q4_K_M",
        context_size=8192,
        license_name="MIT",
        license_url=(
            "https://huggingface.co/microsoft/"
            "Phi-4-mini-instruct/blob/main/LICENSE"
        ),
        source_url=(
            "https://huggingface.co/bartowski/"
            "microsoft_Phi-4-mini-instruct-GGUF"
        ),
    ),
}


MODEL_ALIASES = {
    "lama": "llama",
    "llama": "llama",
    "qwen": "qwen",
    "phi": "phi",
}


def default_models_root() -> Path:
    local_app_data = os.getenv("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / "KHZ" / "models"
    return Path.home() / ".local" / "share" / "khz" / "models"


def normalize_family(value: str) -> str:
    family = MODEL_ALIASES.get(value.strip().casefold())
    if family is None:
        supported = ", ".join(sorted(MODEL_CATALOG))
        raise ValueError(f"Unknown model family. Choose one of: {supported}")
    return family


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(4 * 1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _write_json_atomically(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as output:
            json.dump(value, output, indent=2, sort_keys=True)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def build_download_environment(
    cache_dir: Path,
    environ: dict[str, str] | None = None,
) -> dict[str, str]:
    """Build an explicit environment for the network-capable downloader."""

    source = os.environ if environ is None else environ
    allowed_names = {
        "APPDATA",
        "CURL_CA_BUNDLE",
        "HOME",
        "LANG",
        "LC_ALL",
        "LOCALAPPDATA",
        "PATH",
        "PATHEXT",
        "PROGRAMDATA",
        "SSL_CERT_DIR",
        "SSL_CERT_FILE",
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
    sanitized["LLAMA_CACHE"] = str(cache_dir)

    # Proxy use must be an explicit KHZ download choice so credentials in a
    # generic process environment are not silently inherited.
    proxy = source.get("KHZ_MODEL_DOWNLOAD_PROXY")
    if proxy:
        sanitized["HTTPS_PROXY"] = proxy
        sanitized["HTTP_PROXY"] = proxy
    return sanitized


class ModelManager:
    def __init__(
        self,
        models_root: Path | None = None,
        downloader: Path | None = None,
    ) -> None:
        self.models_root = (models_root or default_models_root()).resolve()
        self.cache_dir = self.models_root / "cache"
        self.manifest_dir = self.models_root / "manifests"
        self.downloader = downloader

    def catalog(self) -> list[ModelSpec]:
        return [MODEL_CATALOG[key] for key in sorted(MODEL_CATALOG)]

    def pull(
        self,
        family: str,
        *,
        accept_license: bool = False,
    ) -> InstalledModel:
        normalized = normalize_family(family)
        spec = MODEL_CATALOG[normalized]

        if spec.requires_license_acceptance and not accept_license:
            raise ModelLicenseAcceptanceRequired(
                f"{spec.display_name} requires acceptance of "
                f"{spec.license_name}: {spec.license_url}. "
                "Re-run with --accept-license after reviewing it."
            )

        self.cache_dir.mkdir(parents=True, exist_ok=True)
        model_path = self._download(spec)
        resolved = model_path.resolve(strict=True)
        if not resolved.is_relative_to(self.cache_dir.resolve()):
            raise ModelManagerError(
                "llama-download returned a model outside the KHZ model cache."
            )
        if resolved.suffix.casefold() != ".gguf":
            raise ModelManagerError("Downloaded model is not a GGUF file.")

        installed = InstalledModel(
            family=spec.family,
            display_name=spec.display_name,
            source=spec.hf_spec,
            source_url=spec.source_url,
            quantization=spec.quantization,
            context_size=spec.context_size,
            license_name=spec.license_name,
            license_url=spec.license_url,
            license_accepted=(
                accept_license or not spec.requires_license_acceptance
            ),
            path=str(resolved),
            size_bytes=resolved.stat().st_size,
            sha256=sha256_file(resolved),
            installed_utc=utc_now(),
        )
        _write_json_atomically(
            self.manifest_dir / f"{spec.family}.json",
            asdict(installed),
        )
        return installed

    def list_installed(self) -> list[InstalledModel]:
        if not self.manifest_dir.exists():
            return []
        installed: list[InstalledModel] = []
        for manifest_path in sorted(self.manifest_dir.glob("*.json")):
            try:
                raw = json.loads(manifest_path.read_text(encoding="utf-8"))
                model = InstalledModel(**raw)
                if Path(model.path).is_file():
                    installed.append(model)
            except (OSError, TypeError, ValueError, json.JSONDecodeError):
                continue
        return installed

    def require_verified(self, family: str) -> InstalledModel:
        normalized = normalize_family(family)
        model = next(
            (
                candidate
                for candidate in self.list_installed()
                if candidate.family == normalized
            ),
            None,
        )
        if model is None:
            raise ModelManagerError(
                f"Model {normalized} is not installed. Run: khz pull {normalized}"
            )
        path = Path(model.path)
        actual_size = path.stat().st_size
        if actual_size != model.size_bytes:
            raise ModelManagerError(
                f"Model size verification failed for {normalized}."
            )
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(self.cache_dir.resolve()):
            raise ModelManagerError(
                f"Model path verification failed for {normalized}."
            )
        actual_hash = sha256_file(resolved)
        if not hmac.compare_digest(actual_hash, model.sha256):
            raise ModelManagerError(
                f"Model SHA-256 verification failed for {normalized}."
            )
        return model

    def _resolve_downloader(self) -> Path:
        if self.downloader is not None:
            candidate = self.downloader
        else:
            configured = os.getenv("KHZ_LLAMA_DOWNLOAD")
            found = configured or shutil.which("llama-download") or shutil.which(
                "llama-download.exe"
            )
            if not found:
                raise ModelManagerError(
                    "llama-download was not found. Install the current llama.cpp "
                    "tools (Windows: winget install llama.cpp) or set "
                    "KHZ_LLAMA_DOWNLOAD."
                )
            candidate = Path(found)
        resolved = candidate.resolve(strict=True)
        if not resolved.is_file():
            raise ModelManagerError("Configured llama-download path is not a file.")
        return resolved

    def _download(self, spec: ModelSpec) -> Path:
        downloader = self._resolve_downloader()
        environment = build_download_environment(self.cache_dir)
        command = [str(downloader), "-hf", spec.hf_spec]

        process = subprocess.Popen(
            command,
            cwd=self.models_root,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=None,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        output_lines: list[str] = []
        assert process.stdout is not None
        for line in process.stdout:
            print(line, end="", flush=True)
            output_lines.append(line.strip())
        exit_code = process.wait()
        if exit_code != 0:
            raise ModelManagerError(
                f"llama-download failed with exit code {exit_code}."
            )

        candidates = [
            Path(line.strip().strip('"'))
            for line in output_lines
            if line.strip().strip('"').casefold().endswith(".gguf")
        ]
        if not candidates:
            raise ModelManagerError(
                "llama-download did not report a downloaded GGUF path."
            )
        return candidates[0]
