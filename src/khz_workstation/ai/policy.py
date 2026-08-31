from __future__ import annotations

from dataclasses import asdict
from typing import Any

from ..models import ActionProposal, Classification, ContextManifest


class AIDisabledError(PermissionError):
    pass


class ActionValidationError(ValueError):
    pass


class AIPolicy:
    ALLOWED_ACTIONS = {
        "SetCellValue", "SetFormula", "SetNumberFormat", "InsertChart",
        "ReplaceParagraph", "CreateSlide", "RenameFile",
    }

    def __init__(self, enabled: bool = False, allow_health_data: bool = False) -> None:
        self.enabled = enabled
        self.allow_health_data = allow_health_data

    def require_enabled(self) -> None:
        if not self.enabled:
            raise AIDisabledError("AI is OFF. No prompt, provider call, embedding, or AI background action is permitted.")

    def release_context(self, manifest: ContextManifest, payload: Any) -> tuple[dict, Any]:
        self.require_enabled()
        if manifest.classification == Classification.HEALTH_DATA and not self.allow_health_data:
            raise PermissionError("PHI_TO_AI is DENY by default.")
        if manifest.item_count < 0 or manifest.item_count > 100_000:
            raise ValueError("Context item count exceeds policy bounds.")
        return asdict(manifest), payload

    def validate_action(self, raw: dict[str, Any], workspace_id: str) -> ActionProposal:
        self.require_enabled()
        if not isinstance(raw, dict):
            raise ActionValidationError("Action must be an object.")
        allowed_keys = {"action", "workspace_id", "target", "args"}
        if set(raw) - allowed_keys:
            raise ActionValidationError("Unknown action fields are rejected.")
        action = raw.get("action")
        if action not in self.ALLOWED_ACTIONS:
            raise ActionValidationError("Unsupported action type.")
        if raw.get("workspace_id") != workspace_id:
            raise ActionValidationError("Workspace mismatch.")
        target = raw.get("target")
        args = raw.get("args")
        if not isinstance(target, str) or not target or len(target) > 500:
            raise ActionValidationError("Invalid target.")
        if not isinstance(args, dict) or len(args) > 50:
            raise ActionValidationError("Invalid action arguments.")
        for key, value in args.items():
            if not isinstance(key, str) or len(key) > 100:
                raise ActionValidationError("Invalid argument key.")
            if isinstance(value, str) and len(value) > 100_000:
                raise ActionValidationError("Argument exceeds size bound.")
        return ActionProposal(action=action, workspace_id=workspace_id, target=target, args=args)
