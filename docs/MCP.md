# MCP in KHZ Workstation

KHZ is both an **MCP server** (it exposes its tools to any MCP host) and an
**MCP host** (it consumes tools from servers you configure). Both directions
run locally over stdio. No network listener is opened for either.

## 1. KHZ as an MCP server

`windows/KHZ.Mcp.Server` builds `khz-mcp-server`, a stdio JSON-RPC 2.0 server
speaking MCP protocol version `2025-06-18`. It exposes the twelve tools in
`docs/OFFICE-TOOLS.md`.

### Command line

| Flag | Meaning |
| --- | --- |
| `--root <path>` | Workspace root. Every tool path is resolved inside it. |
| `--allow-writes` | Enables the mutating tools. Omitted by default. |
| `--read-only` | Explicit read-only mode; overrides `--allow-writes`. |

When `--root` is absent, `KHZ_ROOT` is used, then the current directory.
A missing or unreadable root is a fatal configuration error (exit code 2)
rather than a silent fallback, because a server pointed at the wrong directory
is worse than a server that does not start.

### Read-only by default

The server starts read-only. `--allow-writes` is required before
`replace_text`, `office_edit_document`, `office_write_cells`,
`office_write_slide_text`, `office_convert_to_pdf`, or `run_powershell` will
execute. In read-only mode those tools are not advertised at all, so a host
cannot call a tool it was never shown.

MCP has no interactive confirmation channel. A host cannot be asked to prompt
the user mid-call. `--allow-writes` therefore **is** the consent: granting it
authorises the whole session in advance. Grant it per project, not globally.

### Host configuration

```json
{
  "mcpServers": {
    "khz": {
      "command": "C:\\Program Files\\KHZ\\khz-mcp-server.exe",
      "args": ["--root", "C:\\Workspaces\\clinic", "--allow-writes"]
    }
  }
}
```

stdout carries protocol traffic only. Diagnostics go to stderr, so a host that
mixes the two will not corrupt the stream.

## 2. KHZ as an MCP host

The app reads `%LOCALAPPDATA%\KHZ\mcp-servers.json`. A documented, disabled
example is written on first run.

```json
{
  "servers": [
    {
      "name": "khz-local",
      "command": "C:\\Program Files\\KHZ\\khz-mcp-server.exe",
      "args": ["--root", "C:\\Workspaces\\clinic"],
      "enabled": true,
      "description": "Files, Office, PDF export"
    }
  ]
}
```

`command` must be an absolute path. A relative path would resolve against
whatever working directory the app happens to have, which is not a reviewable
grant, so such entries are skipped and reported in the sidebar.

### How remote tools are treated

- **Namespaced.** A remote tool appears to the model as
  `mcp__<server>__<tool>`. A third-party server therefore cannot register
  `run_powershell` and intercept calls meant for the built-in tool.
- **Confirmation floor.** A remote tool requires approval unless it declares
  `readOnlyHint`. The server's own claim can raise the requirement, never lower
  it, because the server sits outside the trust boundary.
- **Bounded.** Each call has a three-minute timeout; a hung server degrades to
  a failed tool call, not a frozen chat turn.

### Limits

- stdio transport only. HTTP/SSE transports are not implemented.
- MCP resources and prompts are not consumed; only `tools/*` is used.
- The server implements `resources/list` and `prompts/list` as empty responses
  so strict hosts complete discovery without error.
