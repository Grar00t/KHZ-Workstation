from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

try:
    import uno
except ImportError:
    print(json.dumps({"status": "UNVERIFIED", "reason": "pyuno unavailable"}))
    raise SystemExit(2)

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "acceptance" / "corpus"
OUT = ROOT / "acceptance" / "roundtrip"
REPORT = ROOT / "acceptance" / "reports" / "office-roundtrip.json"

FILTERS = {
    ".docx": "Office Open XML Text",
    ".xlsx": "Calc MS Excel 2007 XML",
    ".pptx": "Impress MS PowerPoint 2007 XML",
}


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


def prop(name: str, value):
    p = uno.createUnoStruct("com.sun.star.beans.PropertyValue"); p.Name = name; p.Value = value; return p


def edit_document(doc, suffix: str) -> str:
    marker = "KHZ ROUNDTRIP MARKER 2026-08-31"
    if suffix == ".docx":
        cursor = doc.Text.createTextCursor(); cursor.gotoEnd(False); doc.Text.insertControlCharacter(cursor, 0, False); doc.Text.insertString(cursor, marker, False)
    elif suffix == ".xlsx":
        sheet = doc.Sheets.getByName("Notes"); sheet.getCellRangeByName("A3").String = marker
    elif suffix == ".pptx":
        page = doc.getDrawPages().getByIndex(0)
        done = False
        for i in range(page.getCount()):
            shape = page.getByIndex(i)
            if hasattr(shape, "String") and str(shape.String).strip():
                shape.String = str(shape.String) + " | " + marker; done = True; break
        if not done:
            raise RuntimeError("No editable text shape found")
    return marker


def verify_marker(doc, suffix: str, marker: str) -> bool:
    if suffix == ".docx":
        return marker in str(doc.Text.String)
    if suffix == ".xlsx":
        return doc.Sheets.getByName("Notes").getCellRangeByName("A3").String == marker
    if suffix == ".pptx":
        for p in range(doc.getDrawPages().getCount()):
            page = doc.getDrawPages().getByIndex(p)
            for i in range(page.getCount()):
                shape = page.getByIndex(i)
                if hasattr(shape, "String") and marker in str(shape.String): return True
    return False


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True); REPORT.parent.mkdir(parents=True, exist_ok=True)
    soffice = shutil.which("soffice") or shutil.which("libreoffice")
    if not soffice:
        REPORT.write_text(json.dumps({"status": "UNVERIFIED", "reason": "LibreOffice not found"}, indent=2), encoding="utf-8")
        return 2
    version = subprocess.run([soffice, "--version"], capture_output=True, text=True, timeout=10).stdout.strip()
    profile = Path(tempfile.gettempdir()) / ("khz-lo-rt-" + uuid.uuid4().hex)
    port = 24000 + (uuid.uuid4().int % 1000)
    proc = subprocess.Popen([soffice, "--headless", "--norestore", "--nofirststartwizard", f"-env:UserInstallation={profile.as_uri()}", f"--accept=socket,host=127.0.0.1,port={port};urp;StarOffice.ServiceManager"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    results = {"engine": "LibreOffice", "version": version, "platform": sys.platform, "files": {}}
    try:
        desktop = connect(port)
        for name in ("InstitutionalReport.docx", "InstitutionalWorkbook.xlsx", "InstitutionalPresentation.pptx"):
            source = CORPUS / name; suffix = source.suffix.lower(); target = OUT / name
            item = {"open": False, "edit": False, "save": False, "reopen": False, "marker_verified": False, "error": None}
            try:
                doc = desktop.loadComponentFromURL(source.as_uri(), "_blank", 0, (prop("Hidden", True),))
                item["open"] = doc is not None
                marker = edit_document(doc, suffix); item["edit"] = True
                doc.storeAsURL(target.as_uri(), (prop("FilterName", FILTERS[suffix]), prop("Overwrite", True)))
                item["save"] = target.exists(); doc.close(True)
                reopened = desktop.loadComponentFromURL(target.as_uri(), "_blank", 0, (prop("Hidden", True),))
                item["reopen"] = reopened is not None
                item["marker_verified"] = verify_marker(reopened, suffix, marker)
                reopened.close(True)
            except Exception as exc:
                item["error"] = repr(exc)
            results["files"][name] = item
        pdf_item = {"open": False, "pages": None, "error": None}
        try:
            pdf = desktop.loadComponentFromURL((CORPUS / "InstitutionalPacket.pdf").as_uri(), "_blank", 0, (prop("Hidden", True), prop("ReadOnly", True)))
            pdf_item["open"] = pdf is not None
            if hasattr(pdf, "getDrawPages"):
                pdf_item["pages"] = pdf.getDrawPages().getCount()
            pdf.close(True)
        except Exception as exc:
            pdf_item["error"] = repr(exc)
        results["pdf_local_open"] = pdf_item
        office_ok = all(x["open"] and x["edit"] and x["save"] and x["reopen"] and x["marker_verified"] for x in results["files"].values())
        results["status"] = "VERIFIED" if office_ok and pdf_item["open"] else "PARTIAL"
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
        shutil.rmtree(profile, ignore_errors=True)
    REPORT.write_text(json.dumps(results, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps(results, indent=2, sort_keys=True))
    return 0 if results["status"] == "VERIFIED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
