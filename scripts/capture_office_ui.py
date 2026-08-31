from __future__ import annotations

import shutil
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

import uno

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "acceptance" / "corpus"
OUT = ROOT / "acceptance" / "ui-evidence"


def prop(name: str, value):
    p = uno.createUnoStruct("com.sun.star.beans.PropertyValue")
    p.Name = name
    p.Value = value
    return p


def connect(port: int):
    local_ctx = uno.getComponentContext()
    resolver = local_ctx.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local_ctx)
    for _ in range(100):
        try:
            ctx = resolver.resolve(f"uno:socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext")
            return ctx.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
        except Exception:
            time.sleep(0.1)
    raise RuntimeError("Could not connect to LibreOffice UNO listener")


def shot(name: str) -> None:
    time.sleep(1.2)
    subprocess.run(["scrot", str(OUT / name)], check=True)


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    soffice = shutil.which("soffice") or shutil.which("libreoffice")
    if not soffice:
        print("OFFICE_UI_CAPTURE=UNVERIFIED: LibreOffice unavailable")
        return 2
    profile = Path(tempfile.gettempdir()) / ("khz-lo-ui-" + uuid.uuid4().hex)
    port = 26000 + (uuid.uuid4().int % 1000)
    proc = subprocess.Popen([
        soffice, "--norestore", "--nofirststartwizard", f"-env:UserInstallation={profile.as_uri()}",
        f"--accept=socket,host=127.0.0.1,port={port};urp;StarOffice.ServiceManager"
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    docs = []
    try:
        desktop = connect(port)
        writer = desktop.loadComponentFromURL((CORPUS / "InstitutionalReport.docx").as_uri(), "_blank", 0, (prop("ReadOnly", True),))
        docs.append(writer); shot("Office-Document.png"); writer.close(True); docs.remove(writer)

        calc = desktop.loadComponentFromURL((CORPUS / "InstitutionalWorkbook.xlsx").as_uri(), "_blank", 0, (prop("ReadOnly", True),))
        docs.append(calc)
        calc.getCurrentController().setActiveSheet(calc.Sheets.getByName("Summary")); shot("Office-Workbook.png")
        calc.getCurrentController().setActiveSheet(calc.Sheets.getByName("Pivot")); shot("Office-Pivot.png")
        calc.close(True); docs.remove(calc)

        impress = desktop.loadComponentFromURL((CORPUS / "InstitutionalPresentation.pptx").as_uri(), "_blank", 0, (prop("ReadOnly", True),))
        docs.append(impress); shot("Office-Slides.png"); impress.close(True); docs.remove(impress)
    finally:
        for doc in docs:
            try: doc.close(True)
            except Exception: pass
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
        shutil.rmtree(profile, ignore_errors=True)
    print("OFFICE_UI_CAPTURES=4")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
