# KHZ Workstation Architecture

## Current host

This document describes implemented code in this repository plus explicitly identified future boundaries. It does not claim Windows runtime verification where none was performed.

## Host decision

The primary desktop product is the native Windows WPF application under `windows/KHZ.App`, targeting .NET 9. Python 3.11+ under `src/khz_workstation` remains useful for deterministic services, acceptance/regression coverage, model management, and the bounded workspace MCP server; it is not the primary desktop shell.

```text
KHZ WPF shell / Python services
  |
  +-- real workspace/filesystem
  +-- .khz workspace identity + metadata
  +-- local SQLite state / structured data / tasks
  +-- search
  +-- read-only Git inspection
  +-- bounded PowerShell terminal
  +-- backup / restore
  +-- replaceable Office layer
  +-- optional local chat
         |
         +-- user-supplied llama-server.exe
         +-- user-supplied GGUF (+ optional LoRA/template)
         +-- loopback HTTP only
         +-- bounded workspace MCP
```

## Workspace ownership

A KHZ workspace stores stable identity in `.khz/workspace.json`. The native `WorkspaceService` validates the manifest, rejects reparse-point roots, binds metadata to the same `workspace_id`, and stores workspace metadata in `.khz/metadata.db`.

Normal files remain normal filesystem files. KHZ does not move Office documents into a proprietary database.

## Local state

Application state is SQLite under `%LOCALAPPDATA%\KHZ\state`.

- `khz.db`: activity, settings, integrations, local tasks.
- `local-ai.db`: local model configuration and chat history.

Local AI is kept in a separate database so the normal workstation baseline does not depend on chat state or a model runtime.

## Office

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

Deterministic LibreOffice conversion and the historical corpus use external/headless processes. The WPF shell also contains a local ONLYOFFICE embedding spike behind a loopback gateway. The spike uses a pinned container, enabled JWT, signed per-route capabilities, a separate browser session token, and a restricted WebView2 host. It remains a spike and is not bundled as an approved distribution.

## AI boundary

AI defaults OFF. `AIPolicy.require_enabled()` still fails closed before typed context release or action validation, and health-data release is denied by default.

The optional `khz` CLI manages external GGUF models for direct `llama.cpp` execution. Pull records source/license metadata and a SHA-256 manifest; serve verifies it, binds one ephemeral authenticated loopback session to one workspace, and exposes only list/read/search/propose MCP tools. The WPF Local Assistant blocks external origins. A model-created write proposal cannot modify a target until the user explicitly applies it through KHZ with a fresh hash check and version snapshot.

## Network policy

`NetworkPolicy` implements `DENY`, `LOOPBACK_ONLY`, `ALLOWLIST`, and `UNRESTRICTED` decisions for KHZ-owned calls. It is not a universal OS firewall. Third-party Office, Git, plugin, update, or future model processes require process/OS-level controls for institutional zero-egress enforcement.

## Git

Read-only operations never call remotes. `fetch`, `pull`, and `push` require both explicit authorization and a policy-enabled flag.

## Terminal

User terminal execution is explicit. Terminal commands are not model text. Python `TerminalService.run` requires `authorized=True`, bounds timeout, captures stdout/stderr/exit code, and fixes the working directory to the workspace root. The native WPF PowerShell runner also:

- refuses to run while KHZ itself is elevated;
- runs without stdin or hidden credential prompts;
- fixes a concrete working directory;
- bounds command length, timeout, stdout, and stderr;
- kills the process tree on cancellation/timeout.

It fails closed unless it can attach the spawned PowerShell process to a kill-on-close Windows Job Object. This is process-tree lifecycle containment and execution control, not an AppContainer or filesystem/network sandbox.

## Local chat

Local chat is optional and session-disabled by default. KHZ does not download a model.

When enabled and configured, KHZ starts a user-supplied `llama-server.exe` on a dynamically chosen loopback port with offline mode. Model identity shown by the UI comes from KHZ configuration, not model prose.

The chat client does not persist separated model reasoning and strips common reasoning tags from visible compatibility output. Stored conversation history is bounded before inference.

Available tools are explicit. Read/list/search/repository inspection stay inside the active workspace/folder and reject `.khz` plus reparse traversal. File mutation uses a SHA-256 precondition, unique exact replacement, user confirmation, and an atomic sibling publication path. PowerShell always requires separate confirmation of the exact proposed command.

See `docs/AI-BOUNDARY.md` for the detailed contract.

## Network

KHZ-managed network paths are intentionally narrow:

- Office spike: loopback gateway.
- local chat: dynamic `127.0.0.1` endpoint with llama.cpp offline mode.

This is not a machine-wide firewall claim. Hardened institutional deployments still need OS/process-level egress controls for third-party child processes.

## Verification

Repository claims are evidence-scoped:

- CI compilation/test success proves the checked-in code builds/tests in the configured runners.
- runtime behavior is only called verified after the relevant executable path is exercised.
- a model's fluent answer is never treated as verification of tool execution, model identity, or filesystem state.
