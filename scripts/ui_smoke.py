from __future__ import annotations

import tempfile
from pathlib import Path

from khz_workstation.app import KHZApp
from khz_workstation.workspace import Workspace


def main() -> int:
    with tempfile.TemporaryDirectory() as td:
        root = Path(td) / "workspace"; Workspace.create(root, "UI Smoke")
        app = KHZApp(root)
        app.after(1200, app.destroy)
        app.mainloop()
    print("UI_SMOKE=PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
