# Build and Verification

## Target

Primary runtime target: Windows 11 x64. This source package was assembled and exercised on Linux; Windows runtime status remains **UNVERIFIED** until the Windows workflow or an administrator runs the same checks on Windows 11.

## Core runtime

KHZ Workstation has no mandatory third-party Python runtime dependency beyond Python 3.11+ with Tk.

Windows source setup:

```powershell
py -3 -m venv .venv
.\.venv\Scripts\python -m pip install --upgrade pip
.\.venv\Scripts\python -m pip install -e .
.\scripts\build.ps1
.\scripts\run.ps1
```

Optional deterministic cross-Office workflows:

```powershell
.\.venv\Scripts\python -m pip install -r requirements-automation.txt
```

Acceptance-corpus generation/inspection dependencies:

```powershell
.\.venv\Scripts\python -m pip install -r requirements-dev.txt
```

## Offline / controlled dependency restore

For institutional deployment, mirror and hash approved Python wheels and the approved Office installer in an internal/offline package repository. KHZ does not download dependencies or Office binaries at application startup.

An offline verification machine can restore the optional automation dependencies from a pre-approved wheelhouse, for example:

```powershell
python -m pip install --no-index --find-links C:\ApprovedWheelhouse -r requirements-automation.txt
.\scripts\build.ps1 -SkipDependencyRestore
```

Build the source wheel with already-approved local build tooling:

```powershell
python -m pip wheel . --no-deps --no-build-isolation -w dist
```

## Verification commands

```powershell
$env:PYTHONPATH = "$PWD\src"
python -m compileall -q src tests scripts
python -m pip install -r requirements-automation.txt
python -m pytest tests -v
python scripts\no_ai_baseline.py
```

The same gates run in CI (see "CI status" below). Individual gate oracles can be run directly:

```powershell
python scripts\xlsx_oracle.py -Extract acceptance\corpus\InstitutionalWorkbook.xlsx -OutCsv acceptance\reports\oracle.csv
python scripts\healthcare_zero_egress.py   # prints EGRESS=N
python scripts\g8_ui_audit.py                # prints GATE_G8=PASS|FAIL
```

Office corpus tests additionally require a locally installed LibreOffice and the acceptance dependencies. See `docs/OFFICE-INSTALLATION.md` and `docs/OFFICE-COMPATIBILITY.md`.

## CI status

`.github/workflows/ci.yml` is the reproducible verification definition. It runs an ordered, measured pipeline of eight jobs (G2 Python core, G3 C# build/tests, G4 SBOM + corpus provenance, G1 spreadsheet oracle, I5 zero-egress, G7 NFC/bidi, G8 UI). Every job prints a measured number and derives its exit code from that number. The C# build enforces `0 Warning(s)` / `0 Error(s)` under Release `TreatWarningsAsErrors`.
