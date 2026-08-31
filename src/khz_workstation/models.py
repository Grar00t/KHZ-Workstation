from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import StrEnum
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
    classification: Classification | None = None
    attachments: tuple[str, ...] = ()
    request_id: str = field(default_factory=lambda: str(uuid4()))


@dataclass(frozen=True)
class ActionProposal:
    action: str
    workspace_id: str
    target: str
    args: dict[str, Any]
    proposal_id: str = field(default_factory=lambda: str(uuid4()))
