# KHZ WORKSTATION

KHZ Workstation is a local-first professional workstation shell for filesystem workspaces, Office-family documents, structured local data, search, Git inspection, terminal execution, backup/restore, version history, audit metadata, deterministic tasks, and an optional AI boundary.

The workspace is the product. AI is optional and is **OFF by default**.

## Evidence status for this source package

| Area | Status | Evidence |
|---|---|---|
| Python host compile | VERIFIED | `python -m compileall -q src tests scripts` on the build environment |
| Core unit/security tests | VERIFIED | 22/22 passed across `tests/test_core.py` and `tests/test_workflows.py` |
| UI launch smoke | VERIFIED | Tk UI launched under Xvfb and exited normally; `acceptance/reports/ui-smoke.txt` |
| Real UI captures | VERIFIED on Linux/Xvfb | actual KHZ and LibreOffice screenshots in `acceptance/ui-evidence/`; not mockups |
| `NO_AI_BASELINE` | VERIFIED | `acceptance/reports/no-ai-baseline.json` |
| LibreOffice DOCX/XLSX/PPTX open-edit-save-reopen | VERIFIED on Linux test environment | `acceptance/reports/office-roundtrip.json` |
| XLSX structural round-trip | VERIFIED for tested structures | `acceptance/reports/compatibility-structure.json` |
| DOCX structural round-trip | PARTIAL | comment and tracked insertion preserved; synthetic TOC field was not preserved by the tested round-trip |
| PPTX structural round-trip | PARTIAL | chart, master, transitions, and speaker notes preserved; object animation is not in the current fixture |
| Formula matrix | PARTIAL | 59/64 tested rows calculated; see `acceptance/reports/formula-compatibility.json` |
| Healthcare zero-egress observation | VERIFIED on Linux process-observation test only | no non-loopback connections observed; `acceptance/reports/healthcare-zero-egress.json` |
| Windows 11 runtime | UNVERIFIED | this build environment is Linux, not Windows 11 |
| Windows installer/executable packaging | UNVERIFIED | source repository is delivered; no Windows binary was fabricated |
| PDF in-app editor | UNSUPPORTED | local open/view is delegated to registered local software / Office engine; no fake redaction/editor is presented |
| AI provider | DISABLED | no model is bundled or configured |

`VERIFIED` never means certified, compliant, or production-ready.

## Host requirements

Primary target: Windows 11 x64. Windows 10 x64 is intended where Python and the selected Office engine support it.

Source runtime:

- Python 3.11 or newer from python.org (Tk included in the standard Windows installer).
- Git is optional for repository features.
- LibreOffice is the selected Office adapter for this build and is **not bundled**.

### Run on Windows from source

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python -m pip install --upgrade pip
.\.venv\Scripts\python -m pip install -e .
.\scripts\run.ps1
```

Or:

```cmd
scripts\run.cmd
```

Open a workspace directly:

```powershell
.\scripts\run.ps1 C:\Work\ExampleWorkspace
```

If the folder does not already contain `.khz/workspace.json`, create it through **Open Workspace** in the UI.

## Verify source

Linux/macOS shell:

```bash
export PYTHONPATH="$PWD/src"
python -m compileall -q src tests scripts
python -m unittest discover -s tests -v
python scripts/no_ai_baseline.py
```

Windows PowerShell:

```powershell
.\scripts\build.ps1
$env:PYTHONPATH = "$PWD\src"
python .\scripts\no_ai_baseline.py
```

Office corpus generation uses pinned development packages in `requirements-dev.txt`. Deterministic cross-Office workflows use the separately pinned optional packages in `requirements-automation.txt`. The generated corpus is already included under `acceptance/corpus/`.

## Office engine

Selected adapter: **LibreOffice**, running as an unmodified external local desktop process for interactive editing and through deterministic headless/UNO automation for acceptance tests.

Tested engine in this package:

```text
LibreOffice 25.2.3.2 520(Build:2)
```

Test platform:

```text
Linux (build environment)
```

LibreOffice is detected through `PATH` and the standard Windows Program Files locations. If it is absent, KHZ still launches and reports the missing engine instead of drawing a nonfunctional fake editor.

See:

- `docs/ADR-001-OFFICE-ENGINE.md`
- `docs/OFFICE-COMPATIBILITY.md`
- `docs/OFFICE-LICENSING.md`

## Work surfaces

Implemented host surfaces:

- Home
- Files
- Documents
- Sheets
- Slides
- PDF
- Data
- Search
- Activity
- Repositories
- Terminal
- Tasks
- Assistant
- Settings

The Documents, Sheets, and Slides surfaces manage real workspace files, create real OOXML files from local starter templates, preserve a pre-edit version, and open the file in the detected mature Office engine.

Deterministic no-AI workflows currently implemented are Sheet range → DOCX table, DOCX table → XLSX, DOCX outline → PPTX draft, and Office document → PDF export. These are typed local operations; they are not routed through an LLM.

## No-AI baseline

`AI_ENABLED=false` is the normal default. The acceptance scenario verifies workspace creation, file operations, local Data, local search, read-only Git detection, explicit terminal execution, backup, restore, audit integrity, and the AI kill switch. Office round-trip evidence is generated independently with no AI component.

When AI is OFF, the host does not instantiate a model provider or build/release AI context.

## Healthcare Hardened profile

This is a security profile, **not a certification**. Enabling it forces:

- AI OFF
- remote AI OFF
- embeddings OFF
- telemetry OFF
- updates OFF
- Git network OFF
- plugins OFF
- macros OFF at KHZ policy level
- network policy `LOOPBACK_ONLY`
- Terminal hidden/disabled in the UI

The Healthcare profile also disables Terminal and uses an inactivity timer to request the native Windows session lock when running on Windows; this Windows lock path is **UNVERIFIED** in the Linux build environment. No weaker KHZ password fallback is implemented.

The in-process network policy cannot prove or centrally enforce the behavior of every third-party process. Windows Firewall or equivalent OS controls remain required for institutional deployment. See `docs/HEALTHCARE-DEPLOYMENT.md` and `docs/NETWORK-POLICY.md`.

## Files and versions

Workspace originals stay as ordinary filesystem files. KHZ metadata lives in `.khz/`:

```text
.khz/
  workspace.json
  metadata.db
  audit.jsonl
  versions/
  trash/
```

Direct writes use temp-file + fsync + atomic replacement where the filesystem supports it. Office launches create a recoverable pre-edit snapshot. The Files surface exposes folders, rename, copy, move, Open With, properties/hash, reveal, and Safe Delete to workspace trash rather than truncating/deleting in place.

## Structured Data

The Data surface uses local SQLite with transactions, foreign-key enforcement, WAL mode, stable IDs, typed columns (`TEXT`, `INTEGER`, `REAL`, `BLOB`), workspace ownership, validated filter/sort queries, and deterministic CSV/XLSX import/export. It is separate from spreadsheets and does not claim `.accdb` compatibility.

## Git and Terminal

Git inspection is local/read-only by default: repository detection, branch/status, diff, and history. Network operations are implemented behind explicit authorization and policy checks but are not exposed as automatic background behavior.

Terminal execution requires a user approval dialog, captures stdout/stderr/exit code, runs in the workspace root, and is disabled by the Healthcare Hardened UI policy.

## Backup and restore

Backups are ZIP archives with `KHZ-BACKUP-MANIFEST.json` containing workspace identity and per-file SHA-256 hashes. Publication is staged and atomically replaced only after validation. Restore extracts into a staging directory, revalidates hashes, preserves an existing destination, then swaps the staged result into place.

See `docs/BACKUP-RESTORE.md`.

## AI boundary

No model weights are shipped. No provider is enabled by default. The code includes:

- `IModelProvider`
- runtime-owned model metadata contract
- `ContextManifest`
- PHI-to-AI deny-by-default check
- typed action allowlist
- workspace match validation
- argument bounds
- explicit rejection of shell actions from model output

There is no direct model filesystem, shell, or network capability.

## Repository structure

```text
src/khz_workstation/        host application and services
tests/                      core/security regression tests
scripts/                    run/build/acceptance utilities
acceptance/corpus/          synthetic Office/PDF fixtures
acceptance/reports/         machine-readable evidence
docs/                       architecture, licensing, security, deployment/localization docs
SBOM/                       software bill of materials
LICENSES/                   third-party license texts/pointers
```

## Known gaps

This package does **not** claim the following are complete:

- embedded Office editing inside the KHZ process;
- Windows 11 runtime verification;
- Microsoft VBA execution;
- Power Query or Power Pivot;
- PowerPoint object animation editing;
- secure PDF redaction;
- full PDF annotation/form editing;
- centralized egress enforcement over arbitrary third-party processes;
- Windows Job Object execution isolation;
- Windows Credential Manager/DPAPI integration;
- packaged Windows `.exe`/MSI;
- compliance certification.

See `docs/KNOWN-LIMITATIONS.md` for the full list.
