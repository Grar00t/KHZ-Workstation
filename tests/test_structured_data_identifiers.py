"""Identifier parity between the Python and C# Structured Data import paths.

Reference implementation:
windows/KHZ.App/StructuredData/CsvStructuredDataService.cs
    NormalizeIdentifier, NormalizeHeaders, MakeUniqueIdentifier

Shared gate, asserted directly below:
    ^[A-Za-z][A-Za-z0-9_]{0,62}$
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from khz_workstation.data_service import (
    RESERVED_ROW_ID_REPLACEMENT,
    DataWorkspaceService,
    _dedupe_headers,
    _identifier,
    _unique_identifier,
)
from khz_workstation.store import _IDENT
from khz_workstation.workspace import Workspace

ARABIC_HEADER = "\u0625\u062c\u0645\u0627\u0644\u064a"


class IdentifierParityTests(unittest.TestCase):
    def test_spaces_become_underscores(self):
        self.assertEqual(
            _identifier("Total Sales", "Column_1"),
            "Total_Sales",
        )

    def test_leading_digit_gets_column_prefix(self):
        self.assertEqual(
            _identifier("2024", "Column_2"),
            "Column_2024",
        )

    def test_surrounding_underscores_are_trimmed(self):
        self.assertEqual(
            _identifier("_id_", "Column_3"),
            "id",
        )

    def test_non_ascii_header_uses_fallback_verbatim(self):
        self.assertEqual(
            _identifier(ARABIC_HEADER, "Column_5"),
            "Column_5",
        )

    def test_blank_headers_use_fallback_verbatim(self):
        self.assertEqual(_identifier("", "Column_7"), "Column_7")
        self.assertEqual(_identifier("   ", "Column_8"), "Column_8")

    def test_length_is_capped(self):
        self.assertEqual(
            len(_identifier("a" * 70, "Column_6")),
            63,
        )

    def test_prefixed_over_length_name_is_capped(self):
        produced = _identifier("9" * 70, "Column_9")
        self.assertEqual(len(produced), 63)
        self.assertTrue(produced.startswith("Column_9"))

    def test_every_result_satisfies_the_store_gate(self):
        samples = (
            "Total Sales",
            "2024",
            "_id_",
            "___",
            ARABIC_HEADER,
            "",
            "   ",
            "a" * 70,
            "9" * 70,
            "tab\tseparated",
            "semi;colon",
        )
        for index, sample in enumerate(samples, 1):
            with self.subTest(sample=sample):
                self.assertRegex(
                    _identifier(sample, f"Column_{index}"),
                    _IDENT,
                )


class ReservedRowIdTests(unittest.TestCase):
    def test_row_id_header_is_remapped(self):
        self.assertEqual(
            _dedupe_headers(["row_id"]),
            [RESERVED_ROW_ID_REPLACEMENT],
        )

    def test_row_id_header_is_remapped_case_insensitively(self):
        self.assertEqual(
            _dedupe_headers(["ROW_ID"]),
            [RESERVED_ROW_ID_REPLACEMENT],
        )

    def test_csv_with_reserved_header_imports(self):
        with tempfile.TemporaryDirectory() as td:
            base = Path(td)
            workspace = Workspace.create(base / "workspace", "Data")
            source = base / "reserved.csv"
            source.write_text(
                "row_id,Total Sales\nR1,10\n",
                encoding="utf-8",
            )
            table_id = DataWorkspaceService(workspace).import_csv(
                source,
                "Reserved",
            )
            columns, rows = workspace.store.query_data(table_id)
            self.assertEqual(
                list(columns),
                ["row_id", "Source_row_id", "Total_Sales"],
            )
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0]["Source_row_id"], "R1")


class DedupeTests(unittest.TestCase):
    def test_duplicate_headers_get_numeric_suffix(self):
        self.assertEqual(
            _dedupe_headers(["Total Sales", "Total Sales"]),
            ["Total_Sales", "Total_Sales_2"],
        )

    def test_blank_headers_are_positional(self):
        self.assertEqual(
            _dedupe_headers(["", "   "]),
            ["Column_1", "Column_2"],
        )

    def test_mixed_header_row_matches_csharp_shapes(self):
        self.assertEqual(
            _dedupe_headers(
                [
                    "Total Sales",
                    "2024",
                    "_id_",
                    ARABIC_HEADER,
                    "Total Sales",
                ]
            ),
            [
                "Total_Sales",
                "Column_2024",
                "id",
                "Column_4",
                "Total_Sales_2",
            ],
        )

    def test_duplicate_over_length_headers_terminate_within_bounds(self):
        long_header = "a" * 70
        produced = _dedupe_headers(
            [long_header, long_header, long_header]
        )
        self.assertEqual(len(produced), 3)
        self.assertEqual(
            len({name.casefold() for name in produced}),
            3,
        )
        for name in produced:
            self.assertLessEqual(len(name), 63)
            self.assertRegex(name, _IDENT)

    def test_unique_identifier_shortens_prefix_to_fit_suffix(self):
        base = "b" * 63
        produced = _unique_identifier(base, {base.casefold()})
        self.assertEqual(len(produced), 63)
        self.assertNotEqual(produced.casefold(), base.casefold())
        self.assertTrue(produced.endswith("_2"))


if __name__ == "__main__":
    unittest.main()
