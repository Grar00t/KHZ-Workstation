# KHZ Workstation

**Your work, on your computer.**

KHZ Workstation is an open-source, local-first Windows workspace for real files, Office documents, structured data, tasks, repositories, terminal workflows, search, audit history, and backup/restore.

The workspace is the product. AI is optional and **OFF by default**.

> KHZ does not try to rebuild Word, Excel, or PowerPoint from scratch. Office-class editing is treated as a replaceable engine behind the workspace.

## What makes KHZ different

Most productivity tools create a new universe that your work must be imported into.

KHZ starts from the opposite direction:

```text
Your real workspace folder
|
+-- Contract.docx
+-- Budget.xlsx
+-- Board.pptx
+-- Evidence.pdf
+-- source repository
+-- ordinary folders/files
|
+-- KHZ metadata in .khz/
    +-- workspace identity
    +-- structured local data
    +-- audit/activity
    +-- versions
    +-- backup state
```

Files remain ordinary files. The workspace remains usable outside KHZ.

## Current Windows surfaces

The WPF application currently exposes implemented surfaces for:

- Workspace / Files
- Documents / Sheets / Slides / PDF
- Structured Data
- Search
- Tasks
- Repositories
- Terminal
- Activity
- Security
- Integrations
- Settings
- Backup & Restore

A Workspace Composer is under active development to bring those capabilities together into one project surface instead of a collection of disconnected tools.

## Office engine status

KHZ keeps the Office layer replaceable.

There are currently two distinct pieces of evidence in this repository, and they should not be confused:

1. **LibreOffice acceptance baseline** — the original compatibility corpus and deterministic round-trip tests used an unmodified external LibreOffice process. This remains historical verification evidence.
2. **ONLYOFFICE embedded spike** — the current Windows embedding direction includes a local ONLYOFFICE Document Server prototype behind a loopback gateway used by the WPF shell.

The ONLYOFFICE spike is **not production-ready**. Its launcher explicitly marks JWT as disabled for the spike and binds the KHZ gateway to local/internal networking. It is an integration experiment, not a claim of a hardened deployment.

See:

- [`docs/ADR-002-OFFICE-INTEGRATION-DIRECTION.md`](docs/ADR-002-OFFICE-INTEGRATION-DIRECTION.md)
- [`tools/office-spike/start-spike.sh`](tools/office-spike/start-spike.sh)
- [`docs/ADR-001-OFFICE-ENGINE.md`](docs/ADR-001-OFFICE-ENGINE.md) — historical LibreOffice baseline decision
- [`docs/OFFICE-LICENSING.md`](docs/OFFICE-LICENSING.md) — licensing review baseline; re-review required before distribution of an embedded Office engine

## Local-first boundary

KHZ is designed so normal workspace use does not depend on an AI provider or mandatory cloud service.

Current architectural goals:

- real filesystem workspaces
- local SQLite state and structured data
- no mandatory AI
- explicit capabilities for sensitive actions
- bounded terminal execution
- local read-only Git inspection by default
- deterministic backup manifests and hashes
- replaceable integrations instead of vendor lock-in

`local-first` does **not** mean every third-party child process is magically sandboxed. OS-level network and process isolation still matter for hardened institutional deployment.

## Verification status

Verification claims in KHZ are intentionally narrow.

| Area | Current evidence |
|---|---|
| Windows .NET 9 restore/build | VERIFIED in GitHub Actions |
| Python core tests | VERIFIED on Windows and Ubuntu, Python 3.11/3.13 |
| `NO_AI_BASELINE` | VERIFIED in CI |
| LibreOffice compatibility corpus | VERIFIED/PARTIAL per individual fixture reports |
| WPF Workspace Composer source/build | VERIFIED on its development branch; interactive UI proof still separate |
| ONLYOFFICE local embedding spike | IMPLEMENTED AS A SPIKE; not a production security claim |
| Windows installer/MSI | UNVERIFIED |
| Hardened process sandbox | NOT IMPLEMENTED as an OS-enforced sandbox |

`VERIFIED` never means certified, compliant, secure-by-default, or production-ready.

## Run the Windows application

Requirements:

- Windows 10/11 x64
- .NET 9 SDK for source builds
- Microsoft Edge WebView2 Runtime

```powershell
cd windows
dotnet restore .\KHZ.Workstation.sln
dotnet build .\KHZ.Workstation.sln -c Release
```

The Office integration has separate runtime requirements depending on the engine/spike being tested. Do not treat the ONLYOFFICE spike launcher as a production deployment recipe.

## Run the Python host / acceptance baseline

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python -m pip install --upgrade pip
.\.venv\Scripts\python -m pip install -e .
.\scripts\run.ps1
```

Verification:

```bash
export PYTHONPATH="$PWD/src"
python -m compileall -q src tests scripts
python -m unittest discover -s tests -v
python scripts/no_ai_baseline.py
```

## Demo path

The launch demo is intentionally simple:

```text
Open KHZ
  -> enter a real workspace
  -> open Budget.xlsx through the Office layer
  -> save the real file
  -> return to Files / Structured Data / Activity
  -> create a workspace backup
```

The point is not "another Office wrapper". The point is that the document editor is one replaceable component inside a local-first workstation that owns the surrounding workflow.

See [`docs/LAUNCH-DEMO.md`](docs/LAUNCH-DEMO.md).

## Repository structure

```text
windows/KHZ.App/              WPF Windows application
src/khz_workstation/          Python host and deterministic services
tests/                        core/security regression tests
scripts/                      run/build/acceptance utilities
tools/office-spike/           ONLYOFFICE local embedding spike
acceptance/                   compatibility fixtures and evidence
docs/                         architecture, licensing, deployment docs
SBOM/                         software bill of materials
LICENSES/                     third-party licensing material
```

## Project principles

1. Real files stay real files.
2. The workspace must remain useful without AI.
3. Integrations must be replaceable.
4. Sensitive execution should be explicit and observable.
5. Claims require evidence.
6. Open source should not become another form of lock-in.

## License

KHZ Workstation source is licensed under Apache-2.0 unless a file states otherwise. Third-party components and Office engines retain their own licenses, notices, and trademarks.

**KHZ Workstation is independent software and is not affiliated with Microsoft, ONLYOFFICE, LibreOffice, Notion, or other referenced vendors unless an explicit agreement says otherwise.**
