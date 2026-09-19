from __future__ import annotations

import csv
import hashlib
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


def part_hashes(path: Path) -> dict[str, str]:
    zf = zipfile.ZipFile(path)
    return {name: hashlib.sha256(zf.read(name)).hexdigest() for name in sorted(zf.namelist())}


def run_before_after(before: Path, after: Path) -> int:
    bh = part_hashes(before)
    ah = part_hashes(after)
    before_only = sorted(set(bh) - set(ah))
    after_only = sorted(set(ah) - set(bh))
    common = sorted(set(bh) & set(ah))
    mismatched = [n for n in common if bh[n] != ah[n]]
    preserve = "PASS" if not before_only and not after_only and not mismatched else "FAIL"
    print(f"BEFORE_PARTS={len(bh)}")
    print(f"AFTER_PARTS={len(ah)}")
    print(f"BEFORE_ONLY={len(before_only)}")
    print(f"AFTER_ONLY={len(after_only)}")
    print(f"MISMATCHED={len(mismatched)}")
    print(f"PRESERVE_UNKNOWN_XML={preserve}")
    if mismatched:
        for n in mismatched[:10]:
            print(f"MISMATCH {n}")
    return 0 if preserve == "PASS" else 1


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: xlsx_oracle.py -Extract book.xlsx [-OutCsv out.csv]")
        print("       xlsx_oracle.py -Before b.xlsx -After a.xlsx")
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
