# KHZ.Office.Native

An in-process SpreadsheetML engine with **no third-party dependencies**.

See `docs/ADR-003-NATIVE-OFFICE-ENGINE.md` for why this exists and what would falsify it.

## Dependency policy

`KHZ.Office.Native.csproj` contains **no `PackageReference`**, and that is enforced by
reading the file. Everything is built on the base class library:

| Need | Used |
|---|---|
| Package container | `System.IO.Compression` |
| XML | `System.Xml.Linq`, `System.Xml` |
| Part hashing | `System.Security.Cryptography` |
| Report output (spike only) | `System.Text.Json` |

Consequences: nothing new in `SBOM/spdx.json`, nothing new to license-audit, nothing to
restore from a network feed, and no transitive supply chain.

## Runtime surface

- No listening socket, no loopback port, no outbound request.
- No external process, no `Process.Start`, no `os.startfile` equivalent.
- No elevation.
- Filesystem access limited to the workbook path, the report path, and a scratch directory
  under `Path.GetTempPath()` that is deleted on exit.

`OpenXmlSpreadsheetEngine.Describe()` reports `RequiresNetworkSocket = false` and
`RequiresExternalProcess = false` as inspectable data, so a policy layer can verify this
rather than trust it.

## Layout

```
IOfficeEngine.cs              contract: capabilities, descriptor, document
Formula/
  CellRef.cs                  1-based A1 addressing
  FormulaValue.cs             immutable value + error codes + display formatting
  Tokenizer.cs                lexer; handles #DIV/0!, 'quoted sheet'!A1, _xlfn. prefixes
  Parser.cs                   precedence-climbing parser -> AST
  Evaluator.cs                coercion, comparison ordering, lazy IF/IFERROR/IFNA/IFS/CHOOSE
  FunctionLibrary.cs          strict worksheet functions
  WorkbookModel.cs            sheets, cells, cached vs computed value
  RecalcEngine.cs             dependency graph + Kahn ordering + cycle detection
Xlsx/
  PreservingXlsxPackage.cs    raw parts, original order, SHA-256 per part
  XlsxWorkbookReader.cs       sheets via workbook.xml.rels, shared strings, cached values
  XlsxWorkbookWriter.cs       single-cell edit, sorted insertion, rest of tree untouched
  OpenXmlSpreadsheetEngine.cs IOfficeEngine implementation
```

## Contract differences from `src/khz_workstation/office/base.py`

| Python | Here | Reason |
|---|---|---|
| `open_for_edit(path) -> int \| None` | `OpenRead(path) -> IOfficeDocument` | A pid is only meaningful for an external editor. The old signature made "launch an application" part of the interface, so no in-process engine could implement it. |
| no capability data | `OfficeEngineCapabilities` | `OnlyOfficeDesktopEngine.convert_to_pdf` raises `NotImplementedError` yet the registry can still select it. Capabilities move that failure from call time to selection time. |
| — | `RequiresNetworkSocket`, `RequiresExternalProcess` | A policy layer can refuse an engine before use. |
| — | `PreservesUnknownParts` | Makes round-trip fidelity a declared, testable property. |
| `version` often `None` | own assembly version | An in-process engine always knows its version. |

## Design notes worth knowing before editing

- **`Evaluator.CompareValues` is `public`** because `FunctionLibrary.MatchesCriteria` calls
  it. Criteria matching and the `=`/`<`/`>` operators must not diverge.
- **The clock is injected.** `RecalcEngine(model, clock)` sets what `TODAY`/`NOW` return, so
  two runs of the same corpus produce identical output. Do not read `DateTime.Now` inside a
  function handler.
- **`FormulaValue.FromArray`, not `Array`.** A member named `Array` would shadow
  `System.Array` inside the class.
- **`ZipArchiveEntry.LastWriteTime` throws for years before 1980.** The fallback is the DOS
  epoch, not the Unix epoch.
- **Cycles are never given a value.** Cells that cannot be topologically ordered get
  `#CYCLE!`. Substituting `0` or a stale cached value would make a broken workbook look
  correct.
- **Numeric comparison is rounded to 10 decimal places** by `FormulaValue.FormatNumber`, so
  IEEE-754 noise is not reported as a compatibility failure.
- Errors propagate through functions **except** for the `IS*` family, `COUNTA`,
  `COUNTBLANK`, `NA`, `TRUE`, `FALSE` and `ERROR.TYPE`, which must observe them.

## Reviewer note

This branch has **not been compiled**. It was written without a build available, so treat
the first `dotnet build` as part of the review. Neither project is referenced by
`windows/KHZ.App/KHZ.App.csproj`, and neither is in `windows/KHZ.Workstation.sln`, so
nothing here can break the existing application build or CI.

```powershell
$dotnet = "$env:LOCALAPPDATA\NiyahTools\dotnet\dotnet.exe"
& $dotnet build .\windows\KHZ.Office.Spike\KHZ.Office.Spike.csproj -c Release
```
