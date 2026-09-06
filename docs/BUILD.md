# Build and Verification

## Target

Primary runtime target: Windows 11 x64. This source package was assembled and exercised on Linux; Windows runtime status remains **UNVERIFIED** until the Windows workflow or an administrator runs the same checks on Windows 11.

## Core runtime

The Windows client requires .NET 9 and Microsoft Edge WebView2. The Python core has no mandatory third-party runtime dependency beyond Python 3.11+ with Tk. AI and Office engines remain optional external runtimes.

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
python -m compileall -q src tests scripts tools/office-spike
python -m pip install -r requirements-automation.txt
python -W error::ResourceWarning -m unittest discover -s tests -v
python scripts\no_ai_baseline.py
```

Office corpus tests additionally require a locally installed LibreOffice and the acceptance dependencies. See `docs/OFFICE-INSTALLATION.md` and `docs/OFFICE-COMPATIBILITY.md`.

## CI status

`.github/workflows/ci.yml` builds/tests Python on Windows and Ubuntu with Python 3.11/3.13, runs `NO_AI_BASELINE`, validates the Office launcher syntax on Linux, and restores/builds the .NET 9 WPF solution on Windows. Treat the Actions result for the exact commit as the verification record; it does not replace an interactive Windows runtime test.
