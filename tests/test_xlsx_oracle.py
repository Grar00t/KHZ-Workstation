from __future__ import annotations

import importlib.util
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("xlsx_oracle", ROOT / "scripts" / "xlsx_oracle.py")
assert SPEC and SPEC.loader
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)


def test_formula_canonicalization_matches_libreoffice_normalization() -> None:
    before = '=_xlfn.IFS(Data!A2=1,"one",TRUE,"other")'
    after = '=_xlfn.ifs(Data!A2=1,"one",TRUE(),"other")'
    assert ORACLE.canonical_formula(before) == ORACLE.canonical_formula(after)


def test_formula_canonicalization_preserves_quoted_text() -> None:
    assert ORACLE.canonical_formula('="True() Mixed Case"') == '="True() Mixed Case"'


def test_historical_libreoffice_roundtrip_is_semantically_preserved() -> None:
    for name in ("FormulaCompatibility.xlsx", "InstitutionalWorkbook.xlsx"):
        before = ROOT / "acceptance" / "corpus" / name
        after = ROOT / "acceptance" / "roundtrip" / name
        assert ORACLE.run_before_after(before, after) == 0
