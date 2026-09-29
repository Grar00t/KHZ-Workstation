"""G8 UI accessibility gate test (LAW 2: failing test first).

Asserts the audit's gate-deciding lines against the directive's three G8
criteria: contrast, keyboard reachability, no text baked into images.
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "g8_ui_audit.py"


def _run() -> str:
    out = subprocess.run(
        [sys.executable, str(SCRIPT)], capture_output=True, text=True, check=False,
    )
    return out.stdout


def _line(stdout: str, key: str) -> str:
    m = re.search(rf"^{re.escape(key)}=(.*)$", stdout, re.M)
    assert m, f"missing {key}= line in audit output:\n{stdout}"
    return m.group(1)


def test_gate_g8_passes():
    stdout = _run()
    assert _line(stdout, "GATE_G8") == "PASS", stdout


def test_contrast_pairs_nonzero_and_subgate_pass():
    stdout = _run()
    assert int(_line(stdout, "CONTRAST_PAIRS")) > 0, stdout
    assert _line(stdout, "GATE_CONTRAST") == "PASS", stdout
    assert int(_line(stdout, "BODY_BELOW_4_5")) == 0, stdout
    assert int(_line(stdout, "LARGE_BELOW_3_0")) == 0, stdout


def test_keyboard_subgate_pass():
    stdout = _run()
    assert _line(stdout, "GATE_KEYBOARD") == "PASS", stdout
    assert int(_line(stdout, "IS_TABSTOP_FALSE")) == 0, stdout
    assert int(_line(stdout, "AUTOMATION_IDS")) > 0, stdout


def test_no_text_in_images_subgate_pass():
    stdout = _run()
    assert _line(stdout, "GATE_NO_TEXT_IN_IMAGES") == "PASS", stdout
    assert int(_line(stdout, "IMAGE_COUNT")) >= 0, stdout


def test_audit_exits_zero():
    rc = subprocess.run(
        [sys.executable, str(SCRIPT)], capture_output=True, text=True, check=False,
    ).returncode
    assert rc == 0, f"audit exited {rc}"
