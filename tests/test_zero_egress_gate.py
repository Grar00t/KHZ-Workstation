from __future__ import annotations

import os
import re
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "healthcare_zero_egress.py"


class TestZeroEgressGate(unittest.TestCase):
    def test_prints_egress_counter_and_exit_zero_when_no_egress(self) -> None:
        env = os.environ.copy()
        env["PYTHONPATH"] = str(ROOT / "src")
        proc = subprocess.run(
            [sys.executable, str(SCRIPT)],
            capture_output=True, text=True, env=env, timeout=120,
        )
        out = proc.stdout + proc.stderr
        m = re.search(r"^EGRESS=(\d+)", out, re.MULTILINE)
        self.assertIsNotNone(m, f"no EGRESS=N line printed: {out!r}")
        n = int(m.group(1))
        self.assertEqual(n, 0, f"expected EGRESS=0, got {n}")
        self.assertEqual(proc.returncode, 0, f"exit {proc.returncode} with EGRESS=0")


if __name__ == "__main__":
    unittest.main()
