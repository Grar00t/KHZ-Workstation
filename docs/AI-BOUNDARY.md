# AI Boundary

## Baseline

KHZ Workstation remains fully usable without AI. Local AI is disabled at process start and must be enabled explicitly for the current application session. The enable state is not persisted.

KHZ does not download model weights. A user supplies a local `llama-server.exe`, a local GGUF model, and optionally a local LoRA adapter or chat-template file.

## Local runtime

The native WPF host can launch `llama-server.exe` as a child process bound to a dynamically selected `127.0.0.1` port. KHZ starts it with offline mode, waits on the local health endpoint, keeps the model process resident between requests, captures only bounded runtime logs, and terminates the process tree when the local-AI host is stopped.

Model identity displayed in the UI comes from KHZ configuration. Model prose is never used as evidence of model identity. This avoids treating a model saying "DeepSeek", "OLMo", or any other name as runtime metadata.

## Reasoning visibility

KHZ requests separated reasoning when the local server supports it and does not persist the server's reasoning field. The visible response path also strips common `<think>`, `<reasoning>`, and `<analysis>` blocks as a compatibility fallback. Chat history stores the visible assistant answer, not hidden reasoning.

This is a presentation/storage boundary. It does not claim that a reasoning-capable model performs no internal reasoning.

## Conversations

Local chat state is stored separately at `%LOCALAPPDATA%\KHZ\state\local-ai.db`.

Conversations are scoped to one context:

- a KHZ workspace uses its stable `workspace_id`;
- folder mode uses a SHA-256-derived context identifier for the normalized folder path.

Changing the active workspace/folder changes the visible conversation set. The raw folder path is not used as a conversation primary key.

## Tools

The local model can request bounded tools when tools are enabled:

- `list_directory`
- `read_file`
- `search_text`
- `inspect_repository`
- `run_powershell`

File/search tools accept relative paths only, reject traversal outside the active workspace/folder, skip reparse-directory traversal, and do not expose `.khz` internal metadata.

Repository inspection reuses KHZ's existing read-only repository inspector.

`run_powershell` is different: the exact proposed command and working directory are shown to the user and execution requires an explicit Yes/No confirmation for every call. A model tool call alone cannot authorize command execution. Execution then reuses the existing bounded PowerShell runner with timeout/cancellation/output limits and the existing non-elevated-app restriction.

## Network

The local model server is launched on loopback and with llama.cpp offline mode. KHZ does not configure a hosted AI provider in this path. This is not a claim that Windows globally blocks all process networking; OS-level egress enforcement remains a separate deployment control.

## Audit

Chat audit events record status, lengths, model label, tool-step counts, and whether hidden reasoning was persisted. Raw user prompts and raw PowerShell command text are not copied into the activity log by this feature.

## Authority

- Model self-identification authority: NO
- Model approval authority: NO
- Model verification authority: NO
- Direct unconfirmed PowerShell execution: NO
- Automatic model downloads: NO
- AI required for normal workspace use: NO
