#!/usr/bin/env python3

import hashlib
import html
import ipaddress
import json
import os
import tempfile
import time
import urllib.parse
import urllib.request

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


ROOT = Path(
    os.environ.get(
        "KHZ_ROOT",
        str(Path(__file__).resolve().parents[2])
    )
).resolve()

DOCUMENTS = {
    "document": {
        "path": (
            ROOT / "acceptance" / "roundtrip"
            / "OnlyOffice-InstitutionalReport.docx"
        ).resolve(),
        "document_type": "word",
        "file_type": "docx",
        "content_type": (
            "application/vnd.openxmlformats-officedocument."
            "wordprocessingml.document"
        ),
    },
    "sheet": {
        "path": (
            ROOT / "acceptance" / "roundtrip"
            / "OnlyOffice-InstitutionalWorkbook.xlsx"
        ).resolve(),
        "document_type": "cell",
        "file_type": "xlsx",
        "content_type": (
            "application/vnd.openxmlformats-officedocument."
            "spreadsheetml.sheet"
        ),
    },
    "slide": {
        "path": (
            ROOT / "acceptance" / "roundtrip"
            / "OnlyOffice-InstitutionalPresentation.pptx"
        ).resolve(),
        "document_type": "slide",
        "file_type": "pptx",
        "content_type": (
            "application/vnd.openxmlformats-officedocument."
            "presentationml.presentation"
        ),
    },
    "pdf": {
        "path": (
            ROOT / "acceptance" / "roundtrip"
            / "OnlyOffice-InstitutionalPacket.pdf"
        ).resolve(),
        "document_type": "pdf",
        "file_type": "pdf",
        "content_type": "application/pdf",
    },
}

DEFAULT_DOCUMENT = "sheet"

# Temporary compatibility alias until routing is generalized.
DOC = DOCUMENTS[DEFAULT_DOCUMENT]["path"]

REPORT = (
    ROOT
    / "acceptance"
    / "reports"
    / "onlyoffice-callbacks.jsonl"
).resolve()

BIND = os.environ["KHZ_GATEWAY_BIND"]
PORT = int(os.environ.get("KHZ_GATEWAY_PORT", "8090"))

DOCS_BROWSER = os.environ.get(
    "KHZ_DOCS_BROWSER",
    "http://localhost:8088"
).rstrip("/")

INTERNAL_BASE = f"http://{BIND}:{PORT}"

OFFICE_NET = ipaddress.ip_network(
    os.environ.get(
        "KHZ_OFFICE_NET",
        "172.18.0.0/16"
    )
)


def get_document(kind):
    return DOCUMENTS.get(kind)


def document_key(path=DOC):
    h = hashlib.sha256()
    with path.open("rb") as f:
        while True:
            chunk = f.read(1024 * 1024)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()[:40]


def send_json(handler, code, obj):
    raw = json.dumps(
        obj,
        ensure_ascii=False
    ).encode("utf-8")

    handler.send_response(code)
    handler.send_header(
        "Content-Type",
        "application/json; charset=utf-8"
    )
    handler.send_header(
        "Content-Length",
        str(len(raw))
    )
    handler.end_headers()
    handler.wfile.write(raw)


def allowed_save_url(url):
    parsed = urllib.parse.urlparse(url)

    if parsed.scheme != "http":
        return False

    host = parsed.hostname

    if not host:
        return False

    if host in {"localhost", "127.0.0.1"}:
        return True

    try:
        ip = ipaddress.ip_address(host)
    except ValueError:
        return False

    return ip in OFFICE_NET


class Handler(BaseHTTPRequestHandler):

    server_version = "KHZOfficeGateway/0.1"

    def log_message(self, fmt, *args):
        print(
            time.strftime("%Y-%m-%dT%H:%M:%S"),
            self.client_address[0],
            fmt % args,
            flush=True,
        )

    def do_GET(self):
        path = urllib.parse.urlparse(self.path).path

        if path == "/health":
            send_json(
                self,
                200,
                {
                    "status": "ok",
                    "documents": {
                        kind: {
                            "name": info["path"].name,
                            "exists": info["path"].exists(),
                            "document_type": info["document_type"],
                            "file_type": info["file_type"],
                        }
                        for kind, info in DOCUMENTS.items()
                    },
                    "document_server": DOCS_BROWSER,
                    "gateway_internal": INTERNAL_BASE,
                },
            )
            return

        if path == "/file/InstitutionalWorkbook.xlsx":
            kind = DEFAULT_DOCUMENT
        elif path.startswith("/file/"):
            kind = path.removeprefix("/file/").strip("/")
        else:
            kind = None

        if kind is not None:
            document = get_document(kind)

            if document is None:
                send_json(
                    self,
                    404,
                    {"error": "unknown document type"}
                )
                return

            target = document["path"]

            if not target.exists():
                send_json(
                    self,
                    404,
                    {"error": "document not found"}
                )
                return

            size = target.stat().st_size
            title = target.name.removeprefix("OnlyOffice-")

            self.send_response(200)
            self.send_header(
                "Content-Type",
                document["content_type"]
            )
            self.send_header(
                "Content-Length",
                str(size)
            )
            self.send_header(
                "Content-Disposition",
                f'inline; filename="{title}"'
            )
            self.end_headers()

            with target.open("rb") as f:
                while True:
                    chunk = f.read(1024 * 1024)
                    if not chunk:
                        break
                    self.wfile.write(chunk)

            return

        if path == "/editor":
            kind = DEFAULT_DOCUMENT
        elif path.startswith("/editor/"):
            kind = path.removeprefix("/editor/").strip("/")
        else:
            kind = None

        if kind is not None:
            document = get_document(kind)

            if document is None:
                send_json(
                    self,
                    404,
                    {"error": "unknown document type"}
                )
                return

            target = document["path"]

            if not target.exists():
                send_json(
                    self,
                    404,
                    {"error": "document not found"}
                )
                return

            title = target.name.removeprefix("OnlyOffice-")

            config = {
                "documentType": document["document_type"],

                "document": {
                    "fileType": document["file_type"],
                    "key": document_key(target),
                    "title": title,

                    "url": (
                        INTERNAL_BASE
                        + f"/file/{kind}"
                    ),

                    "permissions": {
                        "edit": True,
                        "download": True,
                        "print": True,
                    },
                },

                "editorConfig": {
                    "mode": "edit",
                    "lang": "en",

                    "callbackUrl": (
                        INTERNAL_BASE
                        + f"/callback/{kind}"
                    ),

                    "user": {
                        "id": "khz-local-user",
                        "name": "KHZ Local User",
                    },

                    "customization": {
                        "forcesave": True,
                    },
                },

                "type": "desktop",
                "width": "100%",
                "height": "100%",
            }

            config_json = json.dumps(
                config,
                separators=(",", ":")
            ).replace("</", "<\\/")

            page = f"""<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta
  name="viewport"
  content="width=device-width,initial-scale=1">
<title>KHZ Office - {html.escape(kind)}</title>

<style>
html, body, #editor {{
    width: 100%;
    height: 100%;
    margin: 0;
    padding: 0;
    overflow: hidden;
}}
</style>

<script src="{html.escape(DOCS_BROWSER)}/web-apps/apps/api/documents/api.js"></script>
</head>

<body>
<div id="editor"></div>

<script>
const config = {config_json};

window.khzeditor =
    new DocsAPI.DocEditor(
        "editor",
        config
    );
</script>

</body>
</html>
"""

            raw = page.encode("utf-8")

            self.send_response(200)
            self.send_header(
                "Content-Type",
                "text/html; charset=utf-8"
            )
            self.send_header(
                "Content-Length",
                str(len(raw))
            )
            self.end_headers()
            self.wfile.write(raw)
            return

        send_json(
            self,
            404,
            {"error": "not found"}
        )

    def do_POST(self):
        path = urllib.parse.urlparse(self.path).path

        if path == "/callback":
            kind = DEFAULT_DOCUMENT
        elif path.startswith("/callback/"):
            kind = path.removeprefix("/callback/").strip("/")
        else:
            send_json(
                self,
                404,
                {"error": 1}
            )
            return

        document = get_document(kind)

        if document is None:
            send_json(
                self,
                404,
                {"error": 1}
            )
            return

        target = document["path"]

        try:
            length = int(
                self.headers.get(
                    "Content-Length",
                    "0"
                )
            )

            if length < 1 or length > 1024 * 1024:
                raise ValueError(
                    "invalid callback size"
                )

            body = self.rfile.read(length)

            payload = json.loads(
                body.decode("utf-8")
            )

            REPORT.parent.mkdir(
                parents=True,
                exist_ok=True
            )

            with REPORT.open(
                "a",
                encoding="utf-8"
            ) as f:
                f.write(
                    json.dumps(
                        {
                            "time": time.time(),
                            "kind": kind,
                            "payload": payload,
                        },
                        ensure_ascii=False,
                    )
                    + "\n"
                )

            status = int(
                payload.get(
                    "status",
                    0
                )
            )

            if status in {2, 6}:
                save_url = payload.get("url")

                if not save_url:
                    raise ValueError(
                        "save callback missing url"
                    )

                if not allowed_save_url(
                    save_url
                ):
                    raise ValueError(
                        "refusing callback URL outside local Office network"
                    )

                with urllib.request.urlopen(
                    save_url,
                    timeout=60
                ) as response:

                    with tempfile.NamedTemporaryFile(
                        mode="wb",
                        prefix="khz-onlyoffice-",
                        suffix=target.suffix,
                        dir=str(target.parent),
                        delete=False,
                    ) as temp:

                        while True:
                            chunk = response.read(
                                1024 * 1024
                            )
                            if not chunk:
                                break

                            temp.write(chunk)

                        temp_path = Path(
                            temp.name
                        )

                os.replace(
                    temp_path,
                    target
                )

                print(
                    f"SAVED kind={kind} status={status} path={target}",
                    flush=True,
                )

            send_json(
                self,
                200,
                {"error": 0}
            )

        except Exception as exc:
            print(
                f"CALLBACK ERROR kind={kind}: {exc}",
                flush=True,
            )

            send_json(
                self,
                500,
                {"error": 1}
            )


if __name__ == "__main__":
    if not DOC.exists():
        raise SystemExit(
            f"missing document: {DOC}"
        )

    print(
        f"KHZ Office Gateway listening on http://{BIND}:{PORT}",
        flush=True,
    )

    print(
        f"Serving: {DOC}",
        flush=True,
    )

    ThreadingHTTPServer(
        (BIND, PORT),
        Handler
    ).serve_forever()
