from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import time
import uuid
from pathlib import Path

try:
    import uno
except ImportError:
    raise SystemExit("pyuno unavailable")

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "acceptance" / "corpus" / "FormulaCompatibility.xlsx"
OUT = ROOT / "acceptance" / "roundtrip" / "FormulaCompatibility.xlsx"


def prop(name, value):
    p=uno.createUnoStruct("com.sun.star.beans.PropertyValue"); p.Name=name; p.Value=value; return p


def connect(port):
    ctx=uno.getComponentContext(); r=ctx.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver",ctx)
    for _ in range(100):
        try:
            rc=r.resolve(f"uno:socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext")
            return rc.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop",rc)
        except Exception: time.sleep(.1)
    raise RuntimeError("UNO connect failed")


def main():
    soffice=shutil.which("soffice") or shutil.which("libreoffice")
    if not soffice: return 2
    OUT.parent.mkdir(parents=True,exist_ok=True)
    profile=Path(tempfile.gettempdir())/("khz-lo-formula-"+uuid.uuid4().hex); port=25000+(uuid.uuid4().int%1000)
    proc=subprocess.Popen([soffice,"--headless","--norestore","--nofirststartwizard",f"-env:UserInstallation={profile.as_uri()}",f"--accept=socket,host=127.0.0.1,port={port};urp;StarOffice.ServiceManager"],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    try:
        desktop=connect(port); doc=desktop.loadComponentFromURL(SRC.as_uri(),"_blank",0,(prop("Hidden",True),))
        doc.calculateAll(); doc.storeAsURL(OUT.as_uri(),(prop("FilterName","Calc MS Excel 2007 XML"),prop("Overwrite",True))); doc.close(True)
    finally:
        proc.terminate();
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
        shutil.rmtree(profile,ignore_errors=True)
    print(OUT); return 0

if __name__=="__main__": raise SystemExit(main())
