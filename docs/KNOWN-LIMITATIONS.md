# Known Limitations

## Platform

- Windows 11 runtime has not been executed in this environment.
- No Windows `.exe`/MSI is included; the ZIP is the source repository.
- Tk/ttk is functional but not a native Fluent/Ribbon implementation.

## Office

- Office editing is out-of-process, not embedded inside the KHZ window.
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
- Terminal is a bounded subprocess runner, not a sandbox/container.
- Multiple interactive terminal sessions/PTY emulation are not implemented.

## Security

- No Windows Job Object isolation backend.
- No DPAPI/Credential Manager integration.
- Windows session locking is implemented through `LockWorkStation` for manual/Healthcare idle lock, but the Windows path is UNVERIFIED in this Linux environment.
- No BitLocker state validation.
- No Windows Firewall automation.
- No process-wide third-party egress enforcement.
- Hash-chained audit detects simple mutation but does not prove event truth and is not tamper-proof against a privileged attacker replacing all audit files.

## AI

- No llama.cpp or Ollama provider is wired in this package.
- No model process is launched.
- Structured action execution after user approval is not connected to a live provider.

## Localization

- English is canonical.
- Locale architecture and RTL metadata exist, but Arabic resource parity and full RTL widget layout have not been implemented.
