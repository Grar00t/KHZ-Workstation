"""Exercise CI gates with inert failing CLI fixtures, not a real application."""
from __future__ import annotations

import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parents[1] / ".github/workflows/ci.yml"
PWSH = shutil.which("pwsh")


def step(name: str) -> str:
    text = WORKFLOW.read_text(encoding="utf-8")
    match = re.search(
        r"^      - name: " + re.escape(name) + r"\n(.*?)(?=^      - |^  [\w-]+:|\Z)",
        text, re.MULTILINE | re.DOTALL,
    )
    if match is None:
        raise AssertionError(f"Missing workflow step: {name}")
    return match.group(1)


def run_script(name: str) -> str:
    lines = step(name).splitlines()
    start = lines.index("        run: |") + 1
    script = []
    for line in lines[start:]:
        if line.strip() and not line.startswith("          "):
            break
        script.append(line)
    return textwrap.dedent("\n".join(script))


class CiContractTests(unittest.TestCase):
    def test_pytest_matrix_uses_explicit_bash(self) -> None:
        self.assertIn("        shell: bash", step("Tests (pytest)"))

    def test_build_uses_msbuild_warning_gate(self) -> None:
        self.assertIn("-warnaserror", run_script("Build Windows app (0 warnings gate)"))

    @unittest.skipUnless(PWSH, "PowerShell is required to execute Windows CI gates")
    def test_build_failure_without_summary_is_not_success(self) -> None:
        self.assert_native_failure("Build Windows app (0 warnings gate)", "build", "")

    @unittest.skipUnless(PWSH, "PowerShell is required to execute Windows CI gates")
    def test_test_failure_after_passing_summary_is_not_success(self) -> None:
        self.assert_native_failure(
            "C# null-input tests (G3)", "test", "Passed: 3 Failed: 0"
        )

    def assert_native_failure(self, name: str, command: str, output: str) -> None:
        with tempfile.TemporaryDirectory(prefix="khz-ci-contract-") as directory:
            root = Path(directory)
            stub = root / "stub.py"
            stub.write_text(f"print({output!r})\nraise SystemExit(7)\n", encoding="utf-8")
            script = run_script(name)
            executable = sys.executable.replace("'", "''")
            stub_path = str(stub).replace("'", "''")
            replacement = f"& '{executable}' '{stub_path}' 2>&1 | Tee-Object -FilePath {command}.log"
            script, count = re.subn(
                r"^dotnet " + command + r"[^\n]*", lambda _: replacement,
                script, count=1, flags=re.MULTILINE,
            )
            self.assertEqual(count, 1, "Expected one native CLI invocation")
            path = root / "gate.ps1"
            path.write_text(script, encoding="utf-8")
            result = subprocess.run(
                [PWSH, "-NoProfile", "-File", str(path)], cwd=root,
                capture_output=True, text=True, timeout=20,
            )
            self.assertEqual(result.returncode, 7, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
