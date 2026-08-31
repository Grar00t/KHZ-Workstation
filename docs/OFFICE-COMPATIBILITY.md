# Office Compatibility Evidence

Engine: `LibreOffice 25.2.3.2 520(Build:2)`

Test platform: Linux build environment.

Windows 11: **UNVERIFIED** in this package.

## Corpus

Synthetic fixtures only:

- `acceptance/corpus/InstitutionalReport.docx`
- `acceptance/corpus/InstitutionalWorkbook.xlsx`
- `acceptance/corpus/InstitutionalPresentation.pptx`
- `acceptance/corpus/InstitutionalPacket.pdf`
- `acceptance/corpus/FormulaCompatibility.xlsx`

All are neutral synthetic data.

## Round-trip result

| Format / feature | Status | Evidence |
|---|---|---|
| DOCX open/edit/save/reopen | VERIFIED | marker read after reopen |
| DOCX comments | PRESERVED | comment part count 1 -> 1 |
| DOCX tracked change insertion | PRESERVED | tracked insertion count 1 -> 1 |
| DOCX headers/footers | PARTIAL | content remained but LibreOffice rewrote section/header/footer part structure |
| DOCX TOC | **PARTIAL** | source synthetic TOC field was not present after round-trip inspection |
| XLSX open/edit/save/reopen | VERIFIED | marker read after reopen |
| XLSX worksheets | VERIFIED | 6 -> 6, names unchanged |
| XLSX formulas | VERIFIED for corpus | 4,807 -> 4,807 |
| XLSX table | VERIFIED | 1 -> 1 |
| XLSX named range | VERIFIED | 1 -> 1 |
| XLSX validation | VERIFIED | 1 -> 1 |
| XLSX conditional-format ranges | VERIFIED | 2 -> 2 |
| XLSX chart | VERIFIED | 1 -> 1 |
| XLSX comments | VERIFIED | 1 -> 1 |
| XLSX protection | VERIFIED | protected sheet 1 -> 1 |
| XLSX pivot table | VERIFIED | pivot table XML 1 -> 1; cache XML count 2 -> 2 |
| PPTX open/edit/save/reopen | VERIFIED | marker read after reopen |
| PPTX chart | PRESERVED | 1 -> 1 |
| PPTX slide master | PRESERVED | 1 -> 1 |
| PPTX transitions | PRESERVED | 6 -> 6 |
| PPTX speaker notes | PRESERVED | note-slide parts 6 -> 6 |
| PPTX object animations | UNVERIFIED | fixture does not contain a validated object-animation timing tree |
| PDF export from Office adapter | VERIFIED on Linux test environment | DOCX, XLSX, and PPTX each converted to a valid PDF; see `acceptance/reports/pdf-export.json` |
| In-app PDF annotation/forms | UNSUPPORTED | no fake editor surface |
| Secure PDF redaction | UNSUPPORTED | no black-box/redaction claim |

Machine-readable details: `acceptance/reports/compatibility-structure.json` and `acceptance/reports/office-roundtrip.json`.

## Formula matrix

`acceptance/reports/formula-compatibility.json` contains 64 formula rows. In the tested engine/version, 59 returned a non-error cached value after LibreOffice calculation and XLSX save.

### VERIFIED in the test matrix

The test includes successful examples of:

`SUM`, `SUMIF`, `SUMIFS`, `AVERAGE`, `AVERAGEIF`, `AVERAGEIFS`, `MIN`, `MAX`, `COUNT`, `COUNTA`, `COUNTIF`, `COUNTIFS`, `ROUND`, `ROUNDUP`, `ROUNDDOWN`, `SUBTOTAL`, `IF`, `IFS`, `AND`, `OR`, `NOT`, `IFERROR`, `IFNA`, `XLOOKUP`, `VLOOKUP`, `HLOOKUP`, `INDEX`, `MATCH`, `XMATCH`, `OFFSET`, `INDIRECT`, `LEFT`, `RIGHT`, `MID`, `LEN`, `TRIM`, `CONCAT`, `TEXTJOIN`, `SUBSTITUTE`, `FIND`, `SEARCH`, `TEXT`, `DATE`, `TODAY`, `NOW`, `YEAR`, `MONTH`, `DAY`, `WORKDAY`, `NETWORKDAYS`, `FILTER`, `SORT`, `UNIQUE`, `SEQUENCE`, relative/absolute/mixed references, cross-sheet references, and a named range.

Modern Excel formulas were serialized with `_xlfn` / `_xlws` prefixes where appropriate.

### PARTIAL / unsupported in tested engine 25.2.3.2

The following returned `#NAME?` after calculation:

- `SORTBY`
- `TAKE`
- `DROP`
- `HSTACK`
- `VSTACK`

Do not advertise those functions as compatible for this exact tested engine version.

## XLSM / Microsoft-specific capability

| Capability | Status |
|---|---|
| XLSM open | UNVERIFIED |
| XLSM edit | UNVERIFIED |
| Macro preservation | UNVERIFIED |
| VBA execution | UNSUPPORTED by KHZ policy integration; engine behavior not certified |
| Power Query | UNSUPPORTED / UNVERIFIED |
| Power Pivot | UNSUPPORTED / UNVERIFIED |
| Office Scripts | UNSUPPORTED |
| COM add-ins | UNSUPPORTED as KHZ feature |
| External workbook links | PARTIAL; KHZ does not auto-refresh them |

Healthcare Hardened mode policy is to keep macros disabled unless separately permitted and controlled at the Office-engine/OS layer.
