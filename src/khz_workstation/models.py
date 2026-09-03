from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import StrEnum
from types import MappingProxyType
from typing import Any
from uuid import uuid4


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


class Classification(StrEnum):
    PUBLIC = "PUBLIC"
    INTERNAL = "INTERNAL"
    CONFIDENTIAL = "CONFIDENTIAL"
    SENSITIVE = "SENSITIVE"
    HEALTH_DATA = "HEALTH_DATA"


class NetworkMode(StrEnum):
    DENY = "DENY"
    LOOPBACK_ONLY = "LOOPBACK_ONLY"
    ALLOWLIST = "ALLOWLIST"
    UNRESTRICTED = "UNRESTRICTED"


class TaskState(StrEnum):
    RECEIVED = "RECEIVED"
    INSPECTING = "INSPECTING"
    PLANNING = "PLANNING"
    AWAITING_APPROVAL = "AWAITING_APPROVAL"
    EXECUTING = "EXECUTING"
    OBSERVING = "OBSERVING"
    VERIFYING = "VERIFYING"
    COMPLETED = "COMPLETED"
    FAILED = "FAILED"
    BLOCKED = "BLOCKED"
    CANCELLED = "CANCELLED"


def _freeze(value: Any) -> Any:
    """Return a read-only view of a JSON-shaped value, recursively."""
    if isinstance(value, Mapping):
        return MappingProxyType({key: _freeze(item) for key, item in value.items()})
    if isinstance(value, (list, tuple)):
        return tuple(_freeze(item) for item in value)
    return value


def _thaw(value: Any) -> Any:
    """Inverse of _freeze, for callers that need a mutable plain-Python copy."""
    if isinstance(value, Mapping):
        return {key: _thaw(item) for key, item in value.items()}
    if isinstance(value, tuple):
        return [_thaw(item) for item in value]
    return value


@dataclass(frozen=True)
class WorkspaceInfo:
    workspace_id: str
    name: str
    root: str
    created_utc: str
    classification: Classification = Classification.INTERNAL

    @staticmethod
    def create(name: str, root: str, classification: Classification = Classification.INTERNAL) -> "WorkspaceInfo":
        return WorkspaceInfo(str(uuid4()), name, root, utc_now(), classification)


@dataclass(frozen=True)
class ContextManifest:
    workspace_id: str
    artifact: str
    selection: str
    item_count: int
    classification: Classification
    attachments: tuple[str, ...] = ()
    request_id: str = field(default_factory=lambda: str(uuid4()))

    def __post_init__(self) -> None:
        # `classification` used to default to None. ai/policy.release_context
        # gates protected health information on
        # `classification == Classification.HEALTH_DATA`, so an unclassified
        # manifest passed that gate silently. Classification is now required
        # and validated where the manifest is built, not where it is consumed.
        if not isinstance(self.classification, Classification):
            raise ValueError(
                "ContextManifest.classification is required and must be a Classification "
                "member; unclassified context cannot be released."
            )


@dataclass(frozen=True)
class ActionProposal:
    action: str
    workspace_id: str
    target: str
    args: Mapping[str, Any]
    proposal_id: str = field(default_factory=lambda: str(uuid4()))

    def __post_init__(self) -> None:
        # frozen=True freezes the field bindings, not the objects they point at.
        # ai/policy.validate_action builds this from model-supplied JSON while
        # the caller still holds a reference to the same dict, so what was
        # approved could be edited before it was executed. Snapshot the
        # arguments into a read-only structure at construction time.
        object.__setattr__(self, "args", _freeze(dict(self.args)))

    def args_as_dict(self) -> dict[str, Any]:
        """Mutable plain-Python copy of `args`, for serialization or logging.

        Prefer this over dataclasses.asdict(), which cannot deep-copy the
        read-only mapping held by `args`.
        """
        return _thaw(self.args)
