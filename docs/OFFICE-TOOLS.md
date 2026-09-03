# Agent tools

Twelve tools, shared by the in-app agent and `khz-mcp-server`. All of them run
locally. None of them upload anything.

## Common rules

- **Paths are relative to the workspace root.** Absolute paths,
  volume-qualified paths, `..` escapes, and the internal `.khz` folder are
  rejected before any I/O. Symlinks and junctions that leave the root are
  refused, so a reparse point cannot be used to reach outside.
- **Edits are hash-guarded.** Every write tool takes `expected_sha256` from a
  prior read. A mismatch fails with `stale_hash`, which is what prevents the
  agent from overwriting a change you made while it was thinking.
- **Writes are atomic.** Content is written to a temporary file in the same
  directory and then moved into place, so an interrupted write cannot leave a
  half-written document.
- **Errors are structured.** Failures return `{ "error": ..., "code": ... }`.

## Read tools

| Tool | Purpose |
| --- | --- |
| `list_directory` | Entries, sizes, and modification times. |
| `read_file` | UTF-8 text with its sha256. Refuses binary and Office files. |
| `search_text` | Literal or regex search across text files. |
| `office_read_document` | DOCX paragraphs with indices and the package sha256. |
| `office_read_sheet` | XLSX cells as A1-addressed values. |
| `office_read_slides` | PPTX slides with per-shape text. |

## Write tools

Each requires confirmation in the app, or `--allow-writes` under MCP.

| Tool | Purpose |
| --- | --- |
| `replace_text` | Exact-match replacement in a text file. |
| `office_edit_document` | Replace text within a DOCX paragraph. |
| `office_write_cells` | Write cell values; triggers full recalculation on load. |
| `office_write_slide_text` | Replace text in a PPTX shape. |
| `office_convert_to_pdf` | Headless PDF export. |
| `run_powershell` | Risk-classified command execution. |

## Stated limitations

These are real and deliberate; do not assume otherwise.

1. **Formatting normalisation.** A DOCX or PPTX paragraph containing mixed
   inline formatting is rewritten as a single run using the first run's
   properties when it is edited. Bold or coloured fragments inside an edited
   paragraph lose their distinct formatting. Untouched paragraphs are
   unaffected.
2. **No cross-paragraph edits.** A match that spans a paragraph boundary is
   refused rather than guessed at.
3. **PDF export needs LibreOffice.** `office_convert_to_pdf` shells out to
   `soffice`. Without it the tool fails with `converter_not_found`. Set
   `KHZ_SOFFICE` to override the executable path. ONLYOFFICE Desktop exposes no
   supported headless conversion interface, which is why LibreOffice is used.
4. **Formula results are not computed.** Writing a cell marks the workbook for
   full recalculation on load and deletes the calculation chain. Values are
   recomputed by Excel or LibreOffice when the file is opened, not by KHZ.
5. **Command classification is not a sandbox.** `run_powershell` blocks a known
   set of destructive patterns (disk formatting, shadow-copy deletion, Defender
   and firewall changes, privilege grants, boot configuration, host shutdown).
   Obfuscation, encoded commands, and indirection can evade any pattern set.
   Treat it as defence in depth over user approval, not as containment.
   `KHZ_TOOLS_PS_ALLOWLIST` restricts execution to named commands;
   `KHZ_TOOLS_ALLOW_DESTRUCTIVE` lifts the blocklist and should stay unset.
6. **Size caps.** Text reads stop at 4 MiB; Office packages at 64 MiB.
7. **Concurrency.** There is no cross-process file lock. The hash guard detects
   an external change between read and write, but a write racing an open editor
   can still lose that editor's unsaved buffer.
