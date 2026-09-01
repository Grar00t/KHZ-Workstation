from __future__ import annotations

import hashlib
import json
import os

from dataclasses import asdict, dataclass
from getpass import getuser
from pathlib import Path
from uuid import uuid4

from ..models import utc_now
from ..workspace import Workspace


MAX_PROPOSED_TEXT_CHARACTERS = 200_000
PROTECTED_DIRECTORIES = {".git", ".khz", ".svn", ".hg"}


class ProposalError(ValueError):
    pass


@dataclass(frozen=True)
class WorkspaceWriteProposal:
    schema_version: int
    proposal_id: str
    workspace_id: str
    operation: str
    target: str
    expected_sha256: str | None
    observed_sha256: str | None
    proposed_content: str
    status: str
    created_utc: str


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _proposal_directory(workspace: Workspace) -> Path:
    return workspace.root / workspace.META_DIR / "ai-proposals"


def _safe_target(workspace: Workspace, target: str) -> tuple[str, Path]:
    if not isinstance(target, str) or not target.strip() or len(target) > 1000:
        raise ProposalError("Target must be a bounded workspace-relative path.")
    relative = Path(target.strip())
    if any(part.casefold() in PROTECTED_DIRECTORIES for part in relative.parts):
        raise ProposalError("AI proposals cannot target protected metadata.")
    resolved = workspace.paths.resolve(relative)
    if resolved.exists() and not resolved.is_file():
        raise ProposalError("AI text proposals must target a file.")
    normalized = resolved.relative_to(workspace.root).as_posix()
    return normalized, resolved


def create_write_proposal(
    workspace: Workspace,
    *,
    target: str,
    content: str,
    expected_sha256: str | None = None,
) -> WorkspaceWriteProposal:
    if not isinstance(content, str):
        raise ProposalError("Proposed content must be text.")
    if len(content) > MAX_PROPOSED_TEXT_CHARACTERS:
        raise ProposalError("Proposed text exceeds the 200000 character limit.")
    if expected_sha256 is not None:
        expected_sha256 = expected_sha256.casefold()
        if len(expected_sha256) != 64 or any(
            character not in "0123456789abcdef" for character in expected_sha256
        ):
            raise ProposalError("expected_sha256 must be a lowercase SHA-256 value.")

    normalized, resolved = _safe_target(workspace, target)
    observed = _sha256_bytes(resolved.read_bytes()) if resolved.is_file() else None
    if expected_sha256 is not None and observed != expected_sha256:
        raise ProposalError("Target content changed before proposal creation.")

    proposal = WorkspaceWriteProposal(
        schema_version=1,
        proposal_id=str(uuid4()),
        workspace_id=workspace.info.workspace_id,
        operation="write_text",
        target=normalized,
        expected_sha256=expected_sha256,
        observed_sha256=observed,
        proposed_content=content,
        status="PENDING",
        created_utc=utc_now(),
    )

    directory = _proposal_directory(workspace)
    directory.mkdir(parents=True, exist_ok=True)
    destination = directory / f"{proposal.proposal_id}.json"
    temporary = directory / f".{proposal.proposal_id}.{os.getpid()}.tmp"
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as output:
            json.dump(asdict(proposal), output, indent=2, sort_keys=True)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)

    workspace.audit.append(
        who=getuser(),
        what="ai.proposal.created",
        target=normalized,
        intent="model proposed a bounded text write",
        approval="PENDING_USER_APPROVAL",
        execution="NONE",
        result=proposal.proposal_id,
        verification="target hash captured",
        metadata={
            "operation": proposal.operation,
            "content_characters": len(content),
            "model_can_apply": False,
        },
    )
    return proposal
