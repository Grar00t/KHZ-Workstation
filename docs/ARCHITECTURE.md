# KHZ Workstation Architecture

## Status

This document describes implemented code in this repository plus explicitly identified future boundaries. It does not claim Windows runtime verification where none was performed.

## Host decision

The current host is Python 3.11+ with Tk/ttk. This is not an ideological choice and it is not an attempt to make Tk the Office renderer. It was selected for this source package because the build environment could compile, test, and launch it without fabricating a Windows-only build. Office rendering/editing stays behind a replaceable mature-engine boundary.

A future native Windows host can replace the UI shell without changing workspace metadata, audit semantics, task/action contracts, or Office adapter concepts.

## Major boundaries

```text
KHZ UI
  |
  +-- Workspace / filesystem services
  +-- SQLite metadata + structured Data
  +-- Search
  +-- Audit / versions / backup / restore
  +-- Git adapter
  +-- Terminal executor
  +-- Office adapter --------------------> external LibreOffice / alternative engine
  +-- Network policy
  +-- AI policy -------------------------> optional IModelProvider (none configured)
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

`IOfficeEngine` is the replaceable interface. Implemented adapters:

- `LibreOfficeEngine` - selected when detected;
- `OnlyOfficeDesktopEngine` - external-process fallback detection only.

Interactive editing is out-of-process. Deterministic LibreOffice conversion uses headless invocation. The acceptance spike uses UNO to open/edit/save/reopen real files.

Embedding LibreOfficeKit or a licensed ONLYOFFICE Developer surface is a future integration choice, not silently simulated here.

## AI boundary

AI defaults OFF. `AIPolicy.require_enabled()` fails closed before context release or action validation. `ContextManifest` is a typed boundary. Health-data release is denied by default. Model actions are allowlisted and bound to one workspace.

The current repository has no configured provider and no model process. A future provider must implement `IModelProvider` and supply runtime-owned metadata rather than trusting model self-identification.

## Network policy

`NetworkPolicy` implements `DENY`, `LOOPBACK_ONLY`, `ALLOWLIST`, and `UNRESTRICTED` decisions for KHZ-owned calls. It is not a universal OS firewall. Third-party Office, Git, plugin, update, or future model processes require process/OS-level controls for institutional zero-egress enforcement.

## Git

Read-only operations never call remotes. `fetch`, `pull`, and `push` require both explicit authorization and a policy-enabled flag.

## Terminal

Terminal commands are not model text. `TerminalService.run` requires `authorized=True`, bounds timeout, captures stdout/stderr/exit code, and fixes the working directory to the workspace root. This is execution control, not a security sandbox.

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
