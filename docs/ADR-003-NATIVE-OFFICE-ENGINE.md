# ADR-003: Native in-process Office engine

- **Status:** Proposed. A measurement spike, not a committed direction.
- **Supersedes:** nothing. **Amends:** ADR-002 on one factual point (see *Corrections* below).
- **Scope:** SpreadsheetML read, recalculate and write. Word processing, presentation and
  rendering are explicitly out of scope for this ADR.

## Context

ADR-001 evaluated ten external engines and selected an out-of-process one. ADR-002
recorded that the Office boundary stays replaceable. Two things were found to be untrue
of the code that actually ships:

1. **The boundary does not exist in the built application.** `IOfficeEngine` is defined
   only in `src/khz_workstation/office/base.py`. `windows/KHZ.App/` contains no Office
   folder and no engine interface. The WPF application is what `windows/KHZ.Workstation.sln`
   builds and what CI compiles; the Python engine layer is not on that path. A boundary
   that is not present in the shipping artifact cannot make the shipping artifact
   replaceable.

2. **The old interface cannot express an in-process engine.** `open_for_edit(path) -> int | None`
   returns a process id. That is not an implementation detail leaking through the
   interface; it *is* the interface. Any engine satisfying it must spawn a process. There
   is no signature an in-process engine could implement.

There is also no capability negotiation. `OnlyOfficeDesktopEngine.convert_to_pdf` raises
`NotImplementedError`, yet `OfficeRegistry` can still select that engine. The absence of a
capability is discovered at call time instead of selection time.

## Decision

Build `windows/KHZ.Office.Native`, a class library with **no NuGet dependencies**, that:

- Declares capabilities as data (`OfficeEngineCapabilities`) so a caller can refuse an
  engine before using it. `RequiresExternalProcess` and `RequiresNetworkSocket` are
  first-class, inspectable fields.
- Drops `open_for_edit`. Opening a document returns an `IOfficeDocument`, not a pid.
- Treats **byte preservation as the primary contract**, not a nice-to-have.
  `PreservingXlsxPackage` holds every package part as raw bytes in original order and
  rewrites untouched parts verbatim. It never regenerates a part it did not parse.

The dependency count is a design choice, not an accident. `System.IO.Compression`,
`System.Xml.Linq`, `System.Text.Json` and `System.Security.Cryptography` are enough. Zero
new entries in `SBOM/spdx.json`, nothing new to license-audit, and nothing to restore
from a network feed.

## What this spike is for

To produce a number that can **contradict** the working estimate that a credible
spreadsheet formula engine is 6-10 person-months. It is not here to show that the code
runs.

`windows/KHZ.Office.Spike` measures three things against
`acceptance/corpus/FormulaCompatibility.xlsx` and writes
`acceptance/reports/native-formula-spike.json`:

| # | Measurement | Method |
|---|---|---|
| 1 | Formula agreement | Recompute every formula, compare against the value already cached in the file. The file is the oracle; no expected values are hand-written. |
| 2 | No-op round trip | Open, save, reopen. Every part must be byte-identical. One differing part fails it. |
| 3 | Single-cell mutation | Write one cell. Every unrelated part must stay byte-identical. |

### Falsification criteria

Stated before the run, so the result cannot be reinterpreted after it:

- **Round-trip fidelity fails** if any part changes on a no-op save, or if any part other
  than the edited sheet changes after a one-cell edit. This is the load-bearing claim and
  the only one that gates the exit code. If it fails, the differentiator argument
  collapses and the external engine stays.
- **The effort estimate is too low** if formula agreement on this corpus is far below the
  59/64 that `docs/OFFICE-COMPATIBILITY.md` records for the external engine, or if the
  unsupported-function list is long. A formula core reaching parity in days would mean the
  6-10 month figure was wrong; a core that stalls well short of it confirms the figure and
  argues against building.

A high match rate on one corpus proves **nothing** about the other four layers of an
Office implementation. Layout, typesetting, rendering and pixel-faithful pagination are
not touched here and remain the hard part.

## Known gaps

Listed before any result, so the report is read with them in view:

- **Whole-column and whole-row references** (`A:A`, `1:1`) are not parsed. `CellRef.TryParseA1`
  rejects them deliberately rather than mis-parsing them.
- **Shared-formula followers** (`<f t="shared"/>` with no text) are counted as unresolved,
  not evaluated. Translating a master formula to a follower's offsets is not implemented.
- **Defined names** are counted but not resolved. A formula referencing one yields `#NAME?`
  and is counted as a mismatch, which is the accurate outcome.
- **Date serials before 1900-03-01.** Conversion uses `DateTime.FromOADate`, which does not
  reproduce the historical 1900 leap-year defect. Serials below 61 are rejected instead of
  being silently mapped to the wrong day.
- **Array and spill semantics** are not implemented. An array result collapses to its
  top-left value in scalar context.
- **No rendering, no PDF export, no styles, no charts, no pivot tables.** The engine reports
  `CanRender = false` and `CanExportPdf = false` rather than throwing when asked.
- Ranges above 262,144 cells yield `#NUM!` instead of being materialised.

## Corrections to earlier records

- **ADR-002** states the replaceable Office boundary is preserved. It is not present in
  `windows/KHZ.App/`. This ADR does not overturn ADR-002's direction; it corrects that one
  factual claim.
- **RTL.** `docs/KNOWN-LIMITATIONS.md` states that locale architecture and RTL metadata
  exist. A code search across the repository for `FlowDirection`, `RightToLeft` and `ar-SA`
  returns **zero** matches. ADR-001 scores all ten candidates identically (4) on RTL, so
  that criterion did not discriminate between them and no candidate was chosen for it.
  Right-to-left text layout is therefore unimplemented in both the application and every
  evaluated engine.
- **Engine version detection.** The docs require recording the exact engine version.
  `OnlyOfficeDesktopEngine` never detects one. `LibreOfficeEngine` probes `soffice.exe`
  before `soffice.com`; on Windows the `.exe` does not write to the parent console, so
  version capture is expected to yield `None`. An in-process engine reports its own
  assembly version and cannot return null.

## Consequences

**Accepted:**

- Two new projects. Neither is referenced by `windows/KHZ.App/KHZ.App.csproj`, so the
  existing application build is unaffected and cannot regress from this branch.
- They are intentionally **not** added to `windows/KHZ.Workstation.sln` yet, so CI's
  `dotnet restore`/`build` of that solution is unchanged. Build them by path.
- A formula engine is a long-lived maintenance commitment. Function-by-function
  compatibility work has no natural end.

**Gained:**

- No socket, no loopback port, no external process on the spreadsheet path.
- Zero added third-party dependencies and zero SBOM churn.
- Round-trip fidelity becomes a *measured* property with a number attached, rather than an
  assertion.

## Alternatives considered

| Option | Why not chosen for the spike |
|---|---|
| Keep the external engine only | Does not answer whether preservation is achievable, and leaves the process/socket surface in place. |
| ClosedXML (MIT) | Has a real formula engine and would be faster to adopt. ADR-001 scored it 2 on calculation, which looks low. It rebuilds the package on save, so unknown parts are not guaranteed to survive; that is the property under test. Worth a separate comparison. |
| `DocumentFormat.OpenXml` (MIT, already referenced) | Already used by `StructuredData/XlsxStructuredDataService.cs`. It is a typed DOM, not a calculation engine, and gives no byte-preservation guarantee. |
| Extend the Python engine layer | Not on the shipping path. Work there does not reach the built application. |

## How to run

From the repository root:

```powershell
$dotnet = "$env:LOCALAPPDATA\NiyahTools\dotnet\dotnet.exe"
& $dotnet build .\windows\KHZ.Office.Spike\KHZ.Office.Spike.csproj -c Release
& $dotnet run --project .\windows\KHZ.Office.Spike\KHZ.Office.Spike.csproj -c Release -- .\acceptance\corpus\FormulaCompatibility.xlsx
```

Exit codes: `0` fidelity checks passed, `1` unhandled error, `2` workbook not found,
`3` a fidelity check failed. The report is written to
`acceptance/reports/native-formula-spike.json`.

The run needs no network, no listening port, no external process and no elevation.

## Decision point

After the report exists:

- Round trip **PASS** and agreement high: the preservation argument holds; consider a
  bounded `IOfficeEngine` in `windows/KHZ.App/` with this as one implementation.
- Round trip **FAIL**: stop. The differentiator does not exist and the external engine
  stays.
- Round trip PASS but agreement low: preservation is real, calculation is not. Use this
  package layer for fidelity and delegate calculation elsewhere.
