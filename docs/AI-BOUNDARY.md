# AI Boundary

## Baseline

AI is not required for normal use. Default settings keep AI, remote AI, and embeddings off. Local AI is disabled at process start and must be enabled explicitly for the current application session. No model weights or llama.cpp binaries are bundled.

The optional local path is user initiated:

```text
khz pull <llama|qwen|phi>
khz serve <family> --workspace <path>
```

Pulling requires network access to the recorded upstream model source. Serving uses a verified local GGUF and loopback only. KHZ does not bundle or silently download model weights: a user supplies a local `llama-server.exe`, a local GGUF model, and optionally a local LoRA adapter or chat-template file.

## Local runtime

`ModelManager` owns the supported catalog and writes an atomic manifest containing source, license metadata, path, size, SHA-256, and installation time. `LocalAiServer` refuses to start a missing or changed model. Model identity comes from this runtime metadata, never model prose.

The native WPF host can launch `llama-server.exe` as a child process bound to a dynamically selected `127.0.0.1` port. Each server session is bound to one stable workspace ID and one canonical workspace root. KHZ starts it with offline mode, waits on the local health endpoint, keeps the model process resident between requests, captures only bounded runtime logs, and terminates the process tree when the local-AI host is stopped. It uses an ephemeral API key, an exact IPv4 loopback endpoint, and Windows Job Object process-tree lifecycle containment. The session file is stored in the user's local KHZ runtime directory, not in the workspace.

## Reasoning visibility

KHZ requests separated reasoning when the local server supports it and does not persist the server's reasoning field. The visible response path also strips common `<think>`, `<reasoning>`, and `<analysis>` blocks as a compatibility fallback. Chat history stores the visible assistant answer, not hidden reasoning.

This is a presentation/storage boundary. It does not claim that a reasoning-capable model performs no internal reasoning.

## Conversations

Local chat state is stored separately at `%LOCALAPPDATA%\KHZ\state\local-ai.db`.

Conversations are scoped to one context:

- a KHZ workspace uses its stable `workspace_id`;
- folder mode uses a SHA-256-derived context identifier for the normalized folder path.

Changing the active workspace/folder changes the visible conversation set. The raw folder path is not used as a conversation primary key. Long conversations are bounded before inference with a conservative character budget so history cannot grow without limit merely because it is stored locally.

## Context and tools

The local model can request bounded in-app tools when tools are enabled:

- `list_directory`
- `read_file`
- `search_text`
- `inspect_repository`
- `replace_text`
- `run_powershell`

File/search tools accept relative paths only, reject traversal outside the active workspace/folder, reject direct or nested filesystem reparse-point traversal, and do not expose `.khz` internal metadata. `read_file` returns the current SHA-256. `replace_text` accepts one exact old-text occurrence plus that expected SHA-256. A stale hash or non-unique old text is rejected. The proposed old/new text is shown to the user and the write requires explicit confirmation; publication uses a temporary sibling file followed by replacement and reports the resulting SHA-256. `run_powershell` is different: the exact proposed command and working directory are shown to the user and execution requires an explicit Yes/No confirmation for every call. A model tool call alone cannot authorize command execution. Execution then reuses the existing bounded PowerShell runner with timeout/cancellation/output limits and the existing non-elevated-app restriction.

The llama.cpp built-in agent and host tools are disabled. KHZ also supplies one stdio MCP server with four narrower workspace tools:

- `workspace_list`
- `workspace_read_text`
- `workspace_search_text`
- `workspace_propose_write_text`

Direct access to `.khz`, `.git`, dependency/build metadata, absolute paths, traversal paths, and symlink/reparse escapes is denied. There is no MCP shell, Git write, remote network, arbitrary file write, approval, or verification tool.

## Proposal boundary

`workspace_propose_write_text` never modifies the requested target. It captures the observed target SHA-256 and stores a `PENDING` proposal under `.khz/ai-proposals`.

Only the native WPF proposal service can apply or reject it. Application requires an explicit user confirmation, re-reads the proposal, verifies workspace identity and path boundaries, compares the current target digest, stores a version snapshot, writes a flushed sibling temporary file, and atomically replaces the target. A stale proposal fails closed.

- Model self-identification authority: **NO**
- Model approval authority: **NO**
- Model shell authority: **NO**
- Model access outside the active workspace: **NO**
- Model verification authority: **NO**
- Direct unconfirmed file mutation: **NO**
- Direct unconfirmed PowerShell execution: **NO**
- Automatic cloud fallback: **NO**
- Automatic model downloads: **NO**
- AI required for normal workspace use: **NO**

## Existing typed action policy

The earlier `AIPolicy` / `ContextManifest` boundary remains available for structured application actions. It fails when AI is off, denies health-data release by default, and rejects unknown or workspace-mismatched actions. It does not grant the local model an executor.
