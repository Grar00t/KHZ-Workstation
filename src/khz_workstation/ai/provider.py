from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class ModelRuntimeMetadata:
    provider: str
    model_identifier: str
    model_hash: str | None
    adapter: str | None
    endpoint: str
    local: bool
    context_size: int | None
    request_id: str
    start_time: str
    finish_time: str | None = None


class IModelProvider(ABC):
    @abstractmethod
    def runtime_metadata(self) -> ModelRuntimeMetadata:
        raise NotImplementedError

    @abstractmethod
    def infer(self, context: Any) -> dict[str, Any]:
        raise NotImplementedError


class DisabledModelProvider(IModelProvider):
    def runtime_metadata(self) -> ModelRuntimeMetadata:
        raise RuntimeError("No model provider is configured.")

    def infer(self, context: Any) -> dict[str, Any]:
        raise RuntimeError("No model provider is configured.")
