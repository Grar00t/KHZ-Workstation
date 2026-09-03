from __future__ import annotations

import hashlib
import hmac
import secrets
import unicodedata
from dataclasses import dataclass, field

from ..models import utc_now


class ApprovalError(PermissionError):
    """Raised when an approval does not authorize the subject being executed."""


class ApprovalReplayError(ApprovalError):
    """Raised when a single-use approval is presented a second time."""


def subject_digest(subject: str) -> str:
    """SHA-256 of a normalized subject string.

    Normalization is NFC so that two byte sequences that render identically --
    which matters for Arabic text, where an alef with hamza can arrive composed
    or decomposed -- cannot produce two different digests for the same command.
    """
    if not isinstance(subject, str):
        raise TypeError("Approval subject must be a string.")
    normalized = unicodedata.normalize("NFC", subject)
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()


@dataclass(frozen=True)
class Approval:
    """A user grant bound to one exact subject.

    ``authorized=True`` is a claim, not an authorization: it carries no record
    of what was shown to the person who consented. An Approval is evidence. It
    names who granted it, when, and the digest of the exact string they saw.
    Executing anything whose digest differs is refused rather than assumed to
    be equivalent.
    """

    proposal_id: str
    subject_sha256: str
    granted_by: str
    granted_utc: str = field(default_factory=utc_now)
    nonce: str = field(default_factory=lambda: secrets.token_hex(16))

    @classmethod
    def for_subject(cls, subject: str, *, proposal_id: str, granted_by: str) -> "Approval":
        if not proposal_id:
            raise ValueError("Approval requires a proposal_id.")
        if not granted_by:
            raise ValueError("Approval requires the identity that granted it.")
        return cls(
            proposal_id=proposal_id,
            subject_sha256=subject_digest(subject),
            granted_by=granted_by,
        )

    def matches(self, subject: str) -> bool:
        return hmac.compare_digest(self.subject_sha256, subject_digest(subject))

    def verify(self, subject: str) -> None:
        if not self.matches(subject):
            raise ApprovalError(
                "Approval does not cover this subject: the approved digest and the "
                "submitted digest differ."
            )


class ApprovalLedger:
    """Single-use enforcement for approvals.

    An approval is spent on first use. Presenting the same nonce again is
    refused, so one confirmation cannot be replayed to authorize a second
    execution.
    """

    def __init__(self) -> None:
        self._spent: set[str] = set()

    def consume(self, approval: Approval, subject: str) -> Approval:
        approval.verify(subject)
        if approval.nonce in self._spent:
            raise ApprovalReplayError("Approval has already been used.")
        self._spent.add(approval.nonce)
        return approval

    def is_spent(self, approval: Approval) -> bool:
        return approval.nonce in self._spent

    @property
    def spent_count(self) -> int:
        return len(self._spent)
