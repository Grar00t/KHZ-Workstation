# Known Limitations

## Platform

- Windows 11 runtime has not been executed in this environment.
- No Windows `.exe`/MSI is included; the ZIP is the source repository.
- Tk/ttk is functional but not a native Fluent/Ribbon implementation.

## Office

- LibreOffice editing remains external; the ONLYOFFICE Document Server path is embedded in WebView2 but remains an experimental spike.
- LibreOffice 25.2.3.2 was tested on Linux; Windows behavior is unverified here.
- DOCX synthetic TOC field did not survive the tested round-trip in the same structure.
- PPTX object animation editing is unverified.
- Formula matrix in this exact LibreOffice version returned unsupported-name errors for `SORTBY`, `TAKE`, `DROP`, `HSTACK`, and `VSTACK`.
- VBA, Power Query, Power Pivot, Office Scripts, and COM add-ins are not claimed.
- XLSM preservation/execution was not tested.

## PDF

- KHZ does not ship a PDF editing/rendering library in this build.
- PDF is opened with the selected/registered local engine.
- In-app annotations/forms/redaction are unsupported.
- No secure-redaction claim is made.

## Cross-Office workflows

- Implemented deterministic workflows are Sheet range → DOCX table, DOCX table → XLSX, DOCX outline → PPTX draft, and Office → PDF export.
- Linked live artifacts, chart-source refresh, mail merge, and Sheet → templated report are not implemented.
- The first three workflows require the pinned optional automation dependencies; they fail clearly if those packages are absent.

## Data

- Structured Data supports typed local tables, CSV/XLSX import/export, exact-match filters, and sort controls; advanced relational/query-builder/report designer features are not implemented.
- `.accdb` is unsupported.
- PostgreSQL is not implemented in this standalone package.

## Git / terminal

- Git stage/unstage/commit methods exist but the primary UI is read-only.
- Git network is policy controlled and not exposed as background behavior.
- Terminal is a bounded subprocess runner with WPF Job Object process-tree lifecycle containment, not an AppContainer/container or filesystem/network sandbox.
- Multiple interactive terminal sessions/PTY emulation are not implemented.

## Security

- Windows Job Object lifecycle containment is implemented for WPF terminal and local-model process trees but has not been runtime-verified in this Linux environment.
- No AppContainer/restricted-token filesystem or network sandbox.
- No DPAPI/Credential Manager integration.
- Windows session locking is implemented through `LockWorkStation` for manual/Healthcare idle lock, but the Windows path is UNVERIFIED in this Linux environment.
- No BitLocker state validation.
- No Windows Firewall automation.
- No process-wide third-party egress enforcement.
- Hash-chained audit detects simple mutation but does not prove event truth and is not tamper-proof against a privileged attacker replacing all audit files.

## AI

- Direct llama.cpp model pull/verification/serve and a workspace-bound MCP path are implemented; Ollama is not required.
- No model weights or llama.cpp binaries are bundled, and a real Windows model/WebView smoke test has not been captured.
- Model quality, hardware sizing, and upstream experimental MCP behavior vary by model/runtime version.
- The local model can propose bounded text writes only; other typed application actions are not connected to the live runtime.

## Localization

- English is canonical.
- Locale architecture and RTL metadata exist, but Arabic resource parity and full RTL widget layout have not been implemented.
