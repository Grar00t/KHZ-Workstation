from __future__ import annotations

import csv
import re
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
SHEET_RE = re.compile(r"xl/worksheets/sheet\d+\.xml$")
ERROR_VALUES = (
    "#NAME?", "#CYCLE!", "#REF!", "#DIV/0!", "#VALUE!", "#N/A", "#NULL!", "#NUM!",
)
WHOLE_COL_RE = re.compile(r"(?<![A-Za-z0-9])([A-Z]+):(\1)(?![A-Za-z0-9])")
SHARED_RE = re.compile(r"t=\"shared\"|<f[^>]*t=['\"]shared['\"]")


def shared_strings(zf: zipfile.ZipFile) -> dict[int, str]:
    if "xl/sharedStrings.xml" not in zf.namelist():
        return {}
    root = ET.fromstring(zf.read("xl/sharedStrings.xml"))
    out: dict[int, str] = {}
    for i, si in enumerate(root.findall(f"{NS}si")):
        out[i] = "".join(t.text or "" for t in si.iter(f"{NS}t"))
    return out


def sheet_names(zf: zipfile.ZipFile) -> dict[str, str]:
    root = ET.fromstring(zf.read("xl/workbook.xml"))
    return {s.attrib["name"]: s.attrib["sheetId"] for s in root.iter(f"{NS}sheet")}


def cell_text(cell: ET.Element, strings: dict[int, str]) -> str:
    t = cell.attrib.get("t")
    if t == "s":
        v = cell.find(f"{NS}v")
        return strings.get(int(v.text), "") if v is not None and v.text else ""
    if t == "inlineStr":
        is_el = cell.find(f"{NS}is")
        return "".join(x.text or "" for x in is_el.iter(f"{NS}t")) if is_el is not None else ""
    return ""


def is_whole_column(formula: str) -> bool:
    return bool(WHOLE_COL_RE.search(formula))


def extract_pairs(path: Path) -> list[dict]:
    zf = zipfile.ZipFile(path)
    strings = shared_strings(zf)
    rows: list[dict] = []
    for sn in sorted(zf.namelist()):
        if not SHEET_RE.match(sn):
            continue
        root = ET.fromstring(zf.read(sn))
        sd = root.find(f"{NS}sheetData")
        if sd is None:
            continue
        for row in sd.iter(f"{NS}row"):
            for cell in row.findall(f"{NS}c"):
                f_el = cell.find(f"{NS}f")
                v_el = cell.find(f"{NS}v")
                if f_el is None:
                    continue
                ref = cell.attrib.get("r", "")
                formula = f_el.text or ""
                shared = "1" if f_el.attrib.get("t") == "shared" else "0"
                whole = "1" if is_whole_column(formula) else "0"
                cached = v_el.text if v_el is not None and v_el.text else ""
                label = cell_text(cell, strings)
                rows.append({
                    "sheet_part": sn, "ref": ref, "label": label,
                    "formula": formula, "cached": cached,
                    "shared": shared, "whole_column": whole,
                })
    return rows


def oracle_pairs(pairs: list[dict]) -> list[dict]:
    out = []
    for r in pairs:
        cached = r["cached"]
        if cached == "":
            continue
        if cached.strip() in ERROR_VALUES:
            continue
        out.append(r)
    return out


def declarations(pairs: list[dict]) -> dict:
    shared = sum(1 for r in pairs if r["shared"] == "1")
    whole = sum(1 for r in pairs if r["whole_column"] == "1")
    empty_v = sum(1 for r in pairs if r["cached"] == "")
    errs = sum(1 for r in pairs if r["cached"].strip() in ERROR_VALUES)
    return {
        "SHARED_UNRESOLVED": shared,
        "WHOLE_COLUMN_UNRESOLVED": whole,
        "EMPTY_CACHED_V": empty_v,
        "ERROR_VALUES_CACHED": errs,
    }


def run_extract(path: Path, out_csv: Path) -> int:
    pairs = extract_pairs(path)
    oracle = oracle_pairs(pairs)
    dec = declarations(pairs)
    with out_csv.open("w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["sheet_part", "ref", "label", "formula", "cached", "shared", "whole_column"])
        for r in oracle:
            w.writerow([r["sheet_part"], r["ref"], r["label"], r["formula"], r["cached"], r["shared"], r["whole_column"]])
    print(f"ORACLE_PAIRS={len(oracle)}")
    for k, v in dec.items():
        print(f"{k}={v}")
    print(f"FORMULAS_TOTAL={len(pairs)}")
    print(f"OUT_CSV={out_csv}")
    return 1 if len(oracle) > 0 else 2


def canonical_formula(formula: str) -> str:
    parts = re.split(r'("(?:""|[^"])*")', formula)
    for i in range(0, len(parts), 2):
        chunk = re.sub(r"\s+", "", parts[i]).upper()
        chunk = re.sub(r"\b(TRUE|FALSE)\(\)", r"\1", chunk)
        parts[i] = chunk
    return "".join(parts)


def workbook_snapshot(path: Path) -> dict:
    with zipfile.ZipFile(path) as zf:
        names = set(zf.namelist())
        workbook = ET.fromstring(zf.read("xl/workbook.xml"))
        ordered_sheets = [s.attrib["name"] for s in workbook.iter(f"{NS}sheet")]
        formulas: dict[str, str] = {}
        validations = conditional = protected = 0
        worksheet_parts = sorted(n for n in names if SHEET_RE.match(n))
        for sheet_part in worksheet_parts:
            sheet = ET.fromstring(zf.read(sheet_part))
            for cell in sheet.iter(f"{NS}c"):
                formula = cell.find(f"{NS}f")
                if formula is not None:
                    formulas[f"{sheet_part}:{cell.attrib.get('r', '')}"] = canonical_formula(formula.text or "")
            validations += len(sheet.findall(f".//{NS}dataValidation"))
            conditional += len(sheet.findall(f".//{NS}conditionalFormatting"))
            protected += int(sheet.find(f"{NS}sheetProtection") is not None)
        return {
            "sheet_names": ordered_sheets,
            "worksheets": len(worksheet_parts),
            "formulas": formulas,
            "defined_names": len(workbook.findall(f".//{NS}definedName")),
            "tables": sum(n.startswith("xl/tables/table") and n.endswith(".xml") for n in names),
            "validations": validations,
            "conditional_formatting_ranges": conditional,
            "protected_sheets": protected,
            "charts": sum(n.startswith("xl/charts/chart") and n.endswith(".xml") for n in names),
            "comments": sum(bool(re.fullmatch(r"xl/comments\d+\.xml", n)) for n in names),
            "pivot_tables": sum(n.startswith("xl/pivotTables/") and n.endswith(".xml") for n in names),
            "pivot_cache": sum(n.startswith("xl/pivotCache/") and n.endswith(".xml") for n in names),
        }


def run_before_after(before: Path, after: Path) -> int:
    before_snapshot = workbook_snapshot(before)
    after_snapshot = workbook_snapshot(after)
    before_formulas = before_snapshot.pop("formulas")
    after_formulas = after_snapshot.pop("formulas")
    coordinates_preserved = set(before_formulas) == set(after_formulas)
    formulas_equivalent = before_formulas == after_formulas
    structure_mismatches = [
        key for key in sorted(before_snapshot)
        if before_snapshot[key] != after_snapshot.get(key)
    ]
    preserve = coordinates_preserved and formulas_equivalent and not structure_mismatches
    print(f"FORMULAS_BEFORE={len(before_formulas)}")
    print(f"FORMULAS_AFTER={len(after_formulas)}")
    print(f"FORMULA_COORDINATES_PRESERVED={'PASS' if coordinates_preserved else 'FAIL'}")
    print(f"FORMULAS_SEMANTIC_EQUIV={'PASS' if formulas_equivalent else 'FAIL'}")
    print(f"STRUCTURE_MISMATCHES={len(structure_mismatches)}")
    for key in structure_mismatches:
        print(f"STRUCTURE_MISMATCH {key}: {before_snapshot[key]!r} -> {after_snapshot.get(key)!r}")
    print(f"ROUNDTRIP_STRUCTURE={'PASS' if preserve else 'FAIL'}")
    return 0 if preserve else 1


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: xlsx_oracle.py -Extract book.xlsx [-OutCsv out.csv]")
        print("       xlsx_oracle.py -Before b.xlsx -After a.xlsx  # semantic round-trip")
        return 2
    mode = argv[1]
    if mode == "-Extract":
        path = Path(argv[2])
        out_csv = Path("oracle.csv")
        if "-OutCsv" in argv:
            out_csv = Path(argv[argv.index("-OutCsv") + 1])
        return run_extract(path, out_csv)
    if mode == "-Before":
        bi = argv.index("-Before") + 1
        ai = argv.index("-After") + 1
        return run_before_after(Path(argv[bi]), Path(argv[ai]))
    print(f"unknown mode: {mode}")
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
