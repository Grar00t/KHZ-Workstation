# Security Engineering Notes

KHZ Workstation is not certified for a regulatory framework. This document separates implemented controls from deployment requirements.

## Implemented controls

- Workspace-relative path resolver with traversal and symlink/reparse escape denial.
- AI OFF by default and explicit fail-closed AI policy.
- Health-data-to-AI deny by default.
- Typed model-action allowlist; shell actions are rejected.
- User authorization gate for terminal and Git writes/network operations.
- In-process network policy modes.
- SQLite transactions and rollback tests.
- Append-oriented audit hash chain with tamper detection.
- Atomic direct-file writes and pre-edit snapshots.
- Backup manifest with SHA-256 hashes and staged restore.
- Healthcare Hardened policy profile.
- Manual/idle session-lock delegation to Windows `LockWorkStation` (Windows runtime UNVERIFIED here).

## Not implemented / not claimed

- Universal OS sandbox.
- Windows Job Object resource/isolation backend.
- Windows Firewall rule management.
- DPAPI/Credential Manager integration.
- BitLocker provisioning or verification.
- Secure PDF redaction.
- Macro sandboxing.
- Office-process egress mediation.
- Compliance/certification.

## Secrets

The current build has no cloud credentials or model credentials. Future secrets must not be placed in workspace metadata, logs, or source. Windows Credential Manager/DPAPI is the preferred Windows implementation.

## Encryption

No application-level encrypted-workspace claim is made. For institutional Windows deployment, BitLocker-protected volumes and NTFS ACLs are recommended OS controls. Temporary/version/backup files are plaintext whenever the underlying volume is plaintext.
