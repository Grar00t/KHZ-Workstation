# AI Boundary

## Baseline

AI is not required for normal use. Default settings keep AI, remote AI, and embeddings off. No model weights or llama.cpp binaries are bundled.

The optional local path is user initiated:

```text
khz pull <llama|qwen|phi>
khz serve <family> --workspace <path>
```

Pulling requires network access to the recorded upstream model source. Serving uses a verified local GGUF and loopback only.

## Runtime identity

`ModelManager` owns the supported catalog and writes an atomic manifest containing source, license metadata, path, size, SHA-256, and installation time. `LocalAiServer` refuses to start a missing or changed model. Model identity comes from this runtime metadata, never model prose.

Each server session is bound to one stable workspace ID and one canonical workspace root. It uses an ephemeral API key, an exact IPv4 loopback endpoint, and Windows Job Object process-tree lifecycle containment. The session file is stored in the user's local KHZ runtime directory, not in the workspace.

## Context and tools

The llama.cpp built-in agent and host tools are disabled. KHZ supplies one stdio MCP server with four tools:

- bounded workspace directory listing;
- bounded UTF-8 file reads;
- bounded text search;
- creation of a pending text-write proposal.

Direct access to `.khz`, `.git`, dependency/build metadata, absolute paths, traversal paths, and symlink/reparse escapes is denied. There is no shell, Git write, remote network, arbitrary file write, approval, or verification tool.

## Proposal boundary

`workspace_propose_write_text` never modifies the requested target. It captures the observed target SHA-256 and stores a `PENDING` proposal under `.khz/ai-proposals`.

Only the native WPF proposal service can apply or reject it. Application requires an explicit user confirmation, re-reads the proposal, verifies workspace identity and path boundaries, compares the current target digest, stores a version snapshot, writes a flushed sibling temporary file, and atomically replaces the target. A stale proposal fails closed.

- Model approval authority: **NO**
- Model shell authority: **NO**
- Model access outside the active workspace: **NO**
- Automatic cloud fallback: **NO**

## Existing typed action policy

The earlier `AIPolicy` / `ContextManifest` boundary remains available for structured application actions. It fails when AI is off, denies health-data release by default, and rejects unknown or workspace-mismatched actions. It does not grant the local model an executor.
