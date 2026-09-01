# Security Engineering Notes

KHZ Workstation is not certified for a regulatory framework. This document separates implemented controls from deployment requirements.

## Implemented controls

- Workspace-relative path resolver with traversal and symlink/reparse escape denial.
- AI OFF by default and explicit fail-closed AI policy.
- Health-data-to-AI deny by default.
- Typed model-action allowlist; shell actions are rejected.
- User authorization gate for terminal and Git writes/network operations.
- Kill-on-close Windows Job Object containment for WPF terminal and local-model process trees.
- Authenticated ONLYOFFICE spike: enabled JWT, separate browser token, signed route capabilities, exact container-source checks, and loopback-only publication.
- Verified local-model manifests and a workspace MCP limited to bounded list/read/search/write-proposal tools.
- Human-only AI proposal application with target hash recheck, version snapshot, flushed temporary write, and atomic replacement.
- In-process network policy modes.
- SQLite transactions and rollback tests.
- Append-oriented audit hash chain with tamper detection.
- Atomic direct-file writes and pre-edit snapshots.
- Backup manifest with SHA-256 hashes and staged restore.
- Healthcare Hardened policy profile.
- Manual/idle session-lock delegation to Windows `LockWorkStation` (Windows runtime UNVERIFIED here).

## Not implemented / not claimed

- Universal OS sandbox.
- AppContainer/restricted-token filesystem or network sandbox.
- Windows Firewall rule management.
- DPAPI/Credential Manager integration.
- BitLocker provisioning or verification.
- Secure PDF redaction.
- Macro sandboxing.
- Office-process egress mediation.
- Compliance/certification.

## Secrets

The current build has no bundled cloud credentials. Office and local-model sessions generate ephemeral bearer/JWT values, do not put them on command lines or in audit records, and keep their session files outside the workspace. They are protected by the logged-in user's filesystem boundary, not DPAPI. Credential Manager/DPAPI remains the preferred Windows implementation for any long-lived secret.

## Encryption

No application-level encrypted-workspace claim is made. For institutional Windows deployment, BitLocker-protected volumes and NTFS ACLs are recommended OS controls. Temporary/version/backup files are plaintext whenever the underlying volume is plaintext.
