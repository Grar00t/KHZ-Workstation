# KHZ Workstation Architecture

## Current host

The primary desktop product is the native Windows WPF application under `windows/KHZ.App`, targeting .NET 9. The Python package under `src/khz_workstation` remains useful for deterministic services and acceptance/regression coverage; it is not the primary desktop shell.

```text
KHZ WPF shell
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
         +-- bounded workspace tools
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

The WPF shell currently contains a local ONLYOFFICE embedding spike behind the loopback gateway at `127.0.0.1:8090`. Historical LibreOffice acceptance artifacts remain compatibility evidence. Neither fact implies that a third-party Office engine is globally sandboxed by KHZ.

## Terminal

User terminal execution is explicit. The native PowerShell runner:

- refuses to run while KHZ itself is elevated;
- runs without stdin or hidden credential prompts;
- fixes a concrete working directory;
- bounds command length, timeout, stdout, and stderr;
- kills the process tree on cancellation/timeout.

It is execution control, not an OS sandbox.

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
