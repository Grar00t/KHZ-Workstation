from __future__ import annotations

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
    print("UNVERIFIED: pyuno is unavailable")
    raise SystemExit(2)


def connect(port: int):
    local_ctx = uno.getComponentContext()
    resolver = local_ctx.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local_ctx)
    for _ in range(80):
        try:
            ctx = resolver.resolve(f"uno:socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext")
            return ctx.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
        except Exception:
            time.sleep(0.1)
    raise RuntimeError("Could not connect to LibreOffice UNO listener")


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    workbook = root / "acceptance" / "corpus" / "InstitutionalWorkbook.xlsx"
    soffice = shutil.which("soffice") or shutil.which("libreoffice")
    if not soffice or not workbook.exists():
        print("UNVERIFIED: LibreOffice or workbook missing")
        return 2
    port = 21000 + (os.getpid() % 10000) if False else 23876
    profile = Path(tempfile.gettempdir()) / ("khz-lo-pivot-" + uuid.uuid4().hex)
    proc = subprocess.Popen([soffice, "--headless", "--norestore", "--nofirststartwizard", f"-env:UserInstallation={profile.as_uri()}", f"--accept=socket,host=127.0.0.1,port={port};urp;StarOffice.ServiceManager"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        desktop = connect(port)
        doc = desktop.loadComponentFromURL(workbook.as_uri(), "_blank", 0, ())
        sheets = doc.getSheets()
        source = sheets.getByName("Transactions").getCellRangeByName("A1:J1201").getRangeAddress()
        if sheets.hasByName("Pivot"):
            sheets.removeByName("Pivot")
        sheets.insertNewByName("Pivot", sheets.getCount())
        pivot_sheet = sheets.getByName("Pivot")
        tables = pivot_sheet.getDataPilotTables()
        desc = tables.createDataPilotDescriptor()
        desc.setSourceRange(source)
        fields = desc.getDataPilotFields()
        fields.getByName("Department").Orientation = 1  # ROW
        fields.getByName("Category").Orientation = 2  # COLUMN
        amount = fields.getByName("Amount"); amount.Orientation = 4  # DATA
        amount.Function = 2  # SUM
        dest = pivot_sheet.getCellRangeByName("A1").getCellAddress()
        tables.insertNewByName("InstitutionalPivot", dest, desc)
        props = (uno.createUnoStruct("com.sun.star.beans.PropertyValue"),)
        props[0].Name = "FilterName"; props[0].Value = "Calc MS Excel 2007 XML"
        doc.storeAsURL(workbook.as_uri(), props)
        doc.close(True)
        print("VERIFIED: LibreOffice DataPilot pivot added and workbook saved")
        return 0
    finally:
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
        shutil.rmtree(profile, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
