# KHZ Workstation Architecture

## Status

This document describes implemented code in this repository plus explicitly identified future boundaries. It does not claim Windows runtime verification where none was performed.

## Host decision

The Windows client is a .NET 9 WPF application under `windows/KHZ.App`. Python 3.11+ supplies deterministic workspace services, the original cross-platform host, model management, and the bounded workspace MCP server. Office rendering/editing remains behind a replaceable mature-engine boundary.

## Major boundaries

```text
WPF / Python UI
  |
  +-- Workspace / filesystem services
  +-- SQLite metadata + structured Data
  +-- Search
  +-- Audit / versions / backup / restore
  +-- Git adapter
  +-- Terminal executor ----------------> Windows Job Object lifecycle containment
  +-- Office adapter --------------------> external LibreOffice / authenticated ONLYOFFICE spike
  +-- Network policy
  +-- AI policy / model manager --------> optional llama.cpp loopback server
                                            |
                                            +-- bounded workspace MCP
```

## Workspace ownership

`Workspace` reads a stable `workspace_id` from `.khz/workspace.json`. Metadata rows include `workspace_id`. Path resolution never infers ownership from a UI selection; every filesystem operation is resolved against a concrete workspace root.

`WorkspacePathResolver` rejects:

- absolute input paths;
- `..` escapes;
- resolved paths outside the workspace;
- symlink/reparse traversal by default.

## Storage separation

Office files remain filesystem files. The SQLite store contains metadata, structured Data tables, and task records. Office files are not placed into SQLite blobs.

SQLite settings include foreign-key enforcement and WAL journaling. Mutating store operations use explicit transactions and rollback on exceptions.

## File mutation

Direct KHZ writes use:

1. optional pre-change snapshot;
2. write to a unique temporary sibling;
3. flush and fsync;
4. `os.replace` to publish atomically where supported;
5. hash/index update;
6. audit metadata.

Office edits are made by an external mature process. Before launch, KHZ captures a version snapshot. KHZ cannot guarantee the Office process itself uses KHZ's atomic-write algorithm.

## Office boundary

Python uses `IOfficeEngine`; WPF uses `IOfficeEngineAdapter`. Implemented paths include:

- `LibreOfficeEngine` - selected when detected;
- `OnlyOfficeDesktopEngine` - external-process fallback detection.
- `OnlyOfficeGatewayAdapter` - authenticated loopback request/navigation adapter for the experimental Document Server spike.

Deterministic LibreOffice conversion and the historical corpus use external/headless processes. The ONLYOFFICE spike uses a pinned container, enabled JWT, signed per-route capabilities, a separate browser session token, and a restricted WebView2 host. It remains a spike and is not bundled as an approved distribution.

## AI boundary

AI defaults OFF. `AIPolicy.require_enabled()` still fails closed before typed context release or action validation, and health-data release is denied by default.

The optional `khz` CLI manages external GGUF models for direct `llama.cpp` execution. Pull records source/license metadata and a SHA-256 manifest; serve verifies it, binds one ephemeral authenticated loopback session to one workspace, and exposes only list/read/search/propose MCP tools. The WPF Local Assistant blocks external origins. A model-created write proposal cannot modify a target until the user explicitly applies it through KHZ with a fresh hash check and version snapshot.

## Network policy

`NetworkPolicy` implements `DENY`, `LOOPBACK_ONLY`, `ALLOWLIST`, and `UNRESTRICTED` decisions for KHZ-owned calls. It is not a universal OS firewall. Third-party Office, Git, plugin, update, or future model processes require process/OS-level controls for institutional zero-egress enforcement.

## Git

Read-only operations never call remotes. `fetch`, `pull`, and `push` require both explicit authorization and a policy-enabled flag.

## Terminal

Terminal commands are not model text. Python `TerminalService.run` requires `authorized=True`, bounds timeout, captures stdout/stderr/exit code, and fixes the working directory to the workspace root. The WPF runner also blocks elevated execution and fails closed unless it can attach the spawned PowerShell process to a kill-on-close Windows Job Object. This is process-tree lifecycle containment, not an AppContainer or filesystem/network sandbox.

## Audit

Audit events are JSON Lines with:

- timestamp;
- actor;
- action;
- target;
- intent;
- approval;
- execution;
- result;
- verification;
- metadata;
- previous hash;
- event hash.

The chain detects simple tampering. It does not prove an event was truthful.

## Backup / restore

Backup files contain a workspace identity and file hashes. Publication occurs only after validation. Restore stages and verifies content before replacing a destination and preserves the old destination when one exists.

## Localization

Canonical UI and logs are English (`en-US`). The host keeps locale as a setting so future resources can be added. Arabic parity is not claimed in this build.

## Deterministic workspace services added in this build

- `FileService`: filesystem-backed rename/copy/move/import/safe-delete, hashes, atomic writes, and pre-edit version snapshots.
- `DataWorkspaceService`: typed CSV/XLSX import/export over workspace-owned SQLite tables; filter/sort queries validate column names and bind values.
- `SessionLockService`: delegates manual/Healthcare idle locking to the native Windows session lock instead of creating a KHZ password boundary.
- `Localizer`: canonical `en-US` resource boundary plus secondary locale/RTL metadata; Arabic catalog/UI parity remains incomplete.
- Cross-Office workflows: Sheet range → DOCX table, DOCX table → XLSX, DOCX outline → PPTX draft, and Office → PDF export.

These services are deterministic and do not invoke an LLM.
