# KHZ Local AI Runtime

Status: **IMPLEMENTED AND UNIT-TESTED; REAL MODEL/WINDOWS RUNTIME SMOKE TEST PENDING**

KHZ uses `llama.cpp` directly and does not require Ollama. AI remains optional and off during normal workspace operations. Model weights are not bundled with KHZ.

## Requirements

- Python 3.11 or newer with KHZ installed (`python -m pip install -e .` for development).
- Current `llama-download` and `llama-server` binaries from `llama.cpp` on `PATH`, or explicit `KHZ_LLAMA_DOWNLOAD` / `KHZ_LLAMA_SERVER` paths.
- A KHZ workspace containing `.khz/workspace.json`.
- Enough local storage for the selected GGUF file. The current Q4 models are multi-gigabyte downloads; verify the exact upstream file size before downloading on a metered connection.

## Commands

```powershell
khz catalog

khz pull qwen
khz pull phi
khz pull llama --accept-license

# Common misspelling retained as a friendly alias:
khz pull lama --accept-license

khz models
khz serve qwen --workspace C:\Work\ProjectA
```

The catalog currently maps to:

| Command | Model source | Quantization | License metadata |
|---|---|---|---|
| `llama` / `lama` | Llama 3.2 3B Instruct GGUF | Q4_K_M | Llama 3.2 Community License; explicit CLI acceptance required |
| `qwen` | Qwen3 4B GGUF | Q4_K_M | Apache-2.0 |
| `phi` | Phi-4 Mini Instruct GGUF | Q4_K_M | MIT model license metadata |

KHZ invokes `llama-download -hf` rather than implementing a second downloader. The downloader receives a credential-minimized environment: inherited cloud/provider tokens and generic proxy settings are removed. Set `KHZ_MODEL_DOWNLOAD_PROXY` only when an explicit download proxy is required. After download KHZ requires a GGUF inside its model cache, calculates SHA-256, and writes an atomic manifest. Every `serve` invocation rechecks the recorded file size and digest before execution.

## Runtime boundary

`khz serve`:

1. opens one explicit KHZ workspace and verifies its stable workspace ID;
2. verifies the installed model manifest and SHA-256;
3. starts `llama-server` on an explicit IPv4 loopback address;
4. passes a random API key through the child environment, never the command line;
5. builds a credential-minimized child environment and drops inherited provider/cloud credentials and llama.cpp flag overrides;
6. places the process tree in a Windows Job Object (or a POSIX process group for development);
7. writes a user-local, mode-restricted session file for the WPF client;
8. gives `llama-server` one stdio MCP server bound to that exact workspace ID;
9. removes the session and MCP configuration when the server exits.

The `--no-agent` option keeps llama.cpp built-in host tools disabled. MCP tools are independent of that option and remain the only tool surface.

## Workspace MCP tools

| Tool | Access |
|---|---|
| `workspace_list` | Bounded directory listing; protected metadata and links are excluded |
| `workspace_read_text` | UTF-8 text only, maximum 1 MiB / 100,000 returned characters |
| `workspace_search_text` | Bounded local text search; `.khz`, `.git`, build and dependency directories excluded |
| `workspace_propose_write_text` | Writes a pending JSON proposal under `.khz/ai-proposals`; never changes the target |

There is no model shell tool, direct network tool, arbitrary filesystem root, or self-approval operation.

The WPF Local Assistant embeds llama.cpp's local web UI and injects the ephemeral bearer token only for the exact session origin. External WebView navigation and resources are blocked. Pending proposals appear in a separate panel. Applying one requires an explicit confirmation, a fresh target hash comparison, a pre-change version snapshot, and an atomic replacement. The model cannot press or call the approval action.

## Remaining deployment limits

- A Windows Job Object controls process-tree lifetime but is not an AppContainer and does not restrict filesystem or socket access.
- The MCP support in llama.cpp is currently experimental upstream.
- No model or llama.cpp binary is bundled in the KHZ installer; the SBOM records them only as external optional components with separate provenance and licenses.
- Real Windows GPU/CPU performance, model quality, and complete WebView interaction still require runtime acceptance evidence.
