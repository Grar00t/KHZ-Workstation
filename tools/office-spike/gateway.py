#!/usr/bin/env python3
"""Authenticated, loopback-only gateway for the ONLYOFFICE integration spike.

This module deliberately uses only the Python standard library. The browser-facing
surface requires a per-launch bearer token. Requests from ONLYOFFICE Document
Server require both an outbox JWT and a short-lived, route-scoped capability.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import html
import ipaddress
import json
import os
import secrets
import tempfile
import threading
import time
import urllib.parse
import urllib.request

from dataclasses import dataclass, field
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Mapping


DEFAULT_DOCUMENT = "sheet"
SESSION_COOKIE = "khz_office_session"
MAX_CALLBACK_BYTES = 1024 * 1024
DEFAULT_MAX_DOCUMENT_BYTES = 512 * 1024 * 1024
MIN_SECRET_BYTES = 32


@dataclass(frozen=True)
class RuntimeConfig:
    root: Path
    document_dir: Path
    report: Path
    bind: str
    port: int
    docs_browser: str
    internal_base: str
    document_server_ip: ipaddress.IPv4Address
    capability_ttl_seconds: int
    max_document_bytes: int
    jwt_secret: bytes = field(repr=False)
    gateway_token: str = field(repr=False)


def _required_secret(environ: Mapping[str, str], name: str) -> str:
    direct_value = environ.get(name, "")
    file_value = environ.get(f"{name}_FILE", "")
    if direct_value and file_value:
        raise ValueError(f"Set only one of {name} or {name}_FILE")
    if file_value:
        secret_path = Path(file_value).resolve(strict=True)
        if not secret_path.is_file() or secret_path.stat().st_size > 4096:
            raise ValueError(f"{name}_FILE must name a bounded regular file")
        if os.name != "nt":
            metadata = secret_path.stat()
            if metadata.st_uid != os.getuid() or metadata.st_mode & 0o077:
                raise ValueError(
                    f"{name}_FILE must be owned by this user with mode 0600"
                )
        value = secret_path.read_text(encoding="utf-8").rstrip("\r\n")
    else:
        value = direct_value
    if len(value.encode("utf-8")) < MIN_SECRET_BYTES:
        raise ValueError(f"{name} must contain at least {MIN_SECRET_BYTES} bytes")
    if any(character in value for character in "\r\n\0"):
        raise ValueError(f"{name} contains an invalid control character")
    return value


def _bounded_integer(
    environ: Mapping[str, str],
    name: str,
    default: int,
    minimum: int,
    maximum: int,
) -> int:
    value = int(environ.get(name, str(default)))
    if value < minimum or value > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return value


def _validated_loopback_origin(value: str) -> str:
    parsed = urllib.parse.urlsplit(value.rstrip("/"))
    if parsed.scheme != "http" or parsed.username or parsed.password:
        raise ValueError("KHZ_DOCS_BROWSER must be an unauthenticated HTTP URL")
    if parsed.hostname not in {"localhost", "127.0.0.1"}:
        raise ValueError("KHZ_DOCS_BROWSER must use localhost or 127.0.0.1")
    if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
        raise ValueError("KHZ_DOCS_BROWSER must contain only an origin")
    if parsed.port is None:
        raise ValueError("KHZ_DOCS_BROWSER must include an explicit port")
    return f"http://{parsed.hostname}:{parsed.port}"


def load_runtime_config(
    environ: Mapping[str, str] | None = None,
) -> RuntimeConfig:
    env = os.environ if environ is None else environ

    root = Path(
        env.get("KHZ_ROOT", str(Path(__file__).resolve().parents[2]))
    ).resolve()
    document_dir = Path(
        env.get(
            "KHZ_OFFICE_DOCUMENT_DIR",
            str(root / "tools" / "office-spike" / ".runtime" / "documents"),
        )
    ).resolve()
    report = Path(
        env.get(
            "KHZ_OFFICE_CALLBACK_REPORT",
            str(root / "tools" / "office-spike" / ".runtime" / "callbacks.jsonl"),
        )
    ).resolve()

    bind_ip = ipaddress.ip_address(env.get("KHZ_GATEWAY_BIND", ""))
    if not isinstance(bind_ip, ipaddress.IPv4Address):
        raise ValueError("KHZ_GATEWAY_BIND must be an IPv4 address")
    if bind_ip.is_unspecified or bind_ip.is_multicast or bind_ip.is_global:
        raise ValueError("KHZ_GATEWAY_BIND must be a concrete local/private address")

    document_server_ip = ipaddress.ip_address(
        env.get("KHZ_DOCSERVER_IP", "")
    )
    if not isinstance(document_server_ip, ipaddress.IPv4Address):
        raise ValueError("KHZ_DOCSERVER_IP must be an IPv4 address")
    if (
        document_server_ip.is_unspecified
        or document_server_ip.is_multicast
        or document_server_ip.is_global
    ):
        raise ValueError("KHZ_DOCSERVER_IP must be a concrete local/private address")

    port = _bounded_integer(env, "KHZ_GATEWAY_PORT", 8090, 1024, 65535)
    ttl = _bounded_integer(
        env,
        "KHZ_OFFICE_CAPABILITY_TTL_SECONDS",
        14_400,
        300,
        86_400,
    )
    max_document_bytes = _bounded_integer(
        env,
        "KHZ_OFFICE_MAX_DOCUMENT_BYTES",
        DEFAULT_MAX_DOCUMENT_BYTES,
        1024 * 1024,
        2 * 1024 * 1024 * 1024,
    )

    jwt_secret = _required_secret(env, "KHZ_ONLYOFFICE_JWT_SECRET").encode(
        "utf-8"
    )
    gateway_token = _required_secret(env, "KHZ_OFFICE_GATEWAY_TOKEN")
    docs_browser = _validated_loopback_origin(
        env.get("KHZ_DOCS_BROWSER", "http://localhost:8088")
    )

    return RuntimeConfig(
        root=root,
        document_dir=document_dir,
        report=report,
        bind=str(bind_ip),
        port=port,
        docs_browser=docs_browser,
        internal_base=f"http://{bind_ip}:{port}",
        document_server_ip=document_server_ip,
        capability_ttl_seconds=ttl,
        max_document_bytes=max_document_bytes,
        jwt_secret=jwt_secret,
        gateway_token=gateway_token,
    )


def build_documents(document_dir: Path) -> dict[str, dict[str, Any]]:
    return {
        "document": {
            "path": (document_dir / "InstitutionalReport.docx").resolve(),
            "document_type": "word",
            "file_type": "docx",
            "content_type": (
                "application/vnd.openxmlformats-officedocument."
                "wordprocessingml.document"
            ),
        },
        "sheet": {
            "path": (document_dir / "InstitutionalWorkbook.xlsx").resolve(),
            "document_type": "cell",
            "file_type": "xlsx",
            "content_type": (
                "application/vnd.openxmlformats-officedocument."
                "spreadsheetml.sheet"
            ),
        },
        "slide": {
            "path": (document_dir / "InstitutionalPresentation.pptx").resolve(),
            "document_type": "slide",
            "file_type": "pptx",
            "content_type": (
                "application/vnd.openxmlformats-officedocument."
                "presentationml.presentation"
            ),
        },
        "pdf": {
            "path": (document_dir / "InstitutionalPacket.pdf").resolve(),
            "document_type": "pdf",
            "file_type": "pdf",
            "content_type": "application/pdf",
        },
    }


def document_key(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()[:40]


def _base64url_encode(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def _base64url_decode(value: str) -> bytes:
    if not value or any(character.isspace() for character in value):
        raise ValueError("invalid base64url value")
    padding = "=" * (-len(value) % 4)
    return base64.urlsafe_b64decode(value + padding)


def encode_jwt(payload: Mapping[str, Any], secret: bytes) -> str:
    header = {"alg": "HS256", "typ": "JWT"}
    encoded_header = _base64url_encode(
        json.dumps(header, separators=(",", ":"), sort_keys=True).encode("utf-8")
    )
    encoded_payload = _base64url_encode(
        json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    )
    signing_input = f"{encoded_header}.{encoded_payload}".encode("ascii")
    signature = hmac.new(secret, signing_input, hashlib.sha256).digest()
    return f"{encoded_header}.{encoded_payload}.{_base64url_encode(signature)}"


def decode_and_verify_jwt(
    token: str,
    secret: bytes,
    *,
    now: int | None = None,
) -> dict[str, Any]:
    if len(token) > 131_072:
        raise ValueError("JWT is too large")
    parts = token.split(".")
    if len(parts) != 3:
        raise ValueError("malformed JWT")

    encoded_header, encoded_payload, encoded_signature = parts
    header = json.loads(_base64url_decode(encoded_header).decode("utf-8"))
    if not isinstance(header, dict) or header.get("alg") != "HS256":
        raise ValueError("JWT algorithm is not allowed")

    signing_input = f"{encoded_header}.{encoded_payload}".encode("ascii")
    expected = hmac.new(secret, signing_input, hashlib.sha256).digest()
    supplied = _base64url_decode(encoded_signature)
    if not hmac.compare_digest(expected, supplied):
        raise ValueError("JWT signature is invalid")

    payload = json.loads(_base64url_decode(encoded_payload).decode("utf-8"))
    if not isinstance(payload, dict):
        raise ValueError("JWT payload must be an object")

    current_time = int(time.time()) if now is None else now
    expires = payload.get("exp")
    if expires is not None and int(expires) < current_time:
        raise ValueError("JWT has expired")
    not_before = payload.get("nbf")
    if not_before is not None and int(not_before) > current_time + 30:
        raise ValueError("JWT is not active")

    return payload


def create_capability(
    secret: bytes,
    purpose: str,
    kind: str,
    expires_at: int,
) -> str:
    payload = {
        "exp": expires_at,
        "kind": kind,
        "nonce": secrets.token_urlsafe(18),
        "purpose": purpose,
    }
    return encode_jwt(payload, secret)


def verify_capability(
    token: str,
    secret: bytes,
    purpose: str,
    kind: str,
    *,
    now: int | None = None,
) -> bool:
    try:
        payload = decode_and_verify_jwt(token, secret, now=now)
    except (TypeError, ValueError, json.JSONDecodeError):
        return False
    return hmac.compare_digest(
        str(payload.get("purpose", "")), purpose
    ) and hmac.compare_digest(str(payload.get("kind", "")), kind)


def authorization_bearer(headers: Mapping[str, str]) -> str | None:
    value = headers.get("Authorization", "")
    scheme, separator, token = value.partition(" ")
    if separator != " " or scheme.casefold() != "bearer" or not token:
        return None
    if any(character.isspace() for character in token):
        return None
    return token


def browser_authorized(
    headers: Mapping[str, str],
    expected_token: str,
) -> tuple[bool, bool]:
    bearer = authorization_bearer(headers)
    if bearer is not None and hmac.compare_digest(bearer, expected_token):
        return True, True

    cookie_header = headers.get("Cookie", "")
    if not cookie_header:
        return False, False
    try:
        cookie = SimpleCookie(cookie_header)
    except Exception:
        return False, False
    session = cookie.get(SESSION_COOKIE)
    if session is None:
        return False, False
    return hmac.compare_digest(session.value, expected_token), False


def allowed_save_url(url: str, document_server_ip: ipaddress.IPv4Address) -> bool:
    try:
        parsed = urllib.parse.urlsplit(url)
        host = ipaddress.ip_address(parsed.hostname or "")
    except ValueError:
        return False
    return (
        parsed.scheme == "http"
        and parsed.username is None
        and parsed.password is None
        and isinstance(host, ipaddress.IPv4Address)
        and host == document_server_ip
        and parsed.port in {None, 80}
        and parsed.path.startswith("/")
        and not parsed.fragment
    )


def _safe_callback_record(
    kind: str,
    payload: Mapping[str, Any],
    saved: bool,
) -> dict[str, Any]:
    key = str(payload.get("key", ""))
    return {
        "time": time.time(),
        "kind": kind,
        "status": int(payload.get("status", 0)),
        "document_key_sha256": (
            hashlib.sha256(key.encode("utf-8")).hexdigest() if key else None
        ),
        "saved": saved,
    }


class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
        return None


def download_document(
    url: str,
    destination: Path,
    max_bytes: int,
) -> None:
    opener = urllib.request.build_opener(_NoRedirectHandler())
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "KHZOfficeGateway/0.2"},
        method="GET",
    )

    with opener.open(request, timeout=60) as response:
        content_length = response.headers.get("Content-Length")
        if content_length is not None and int(content_length) > max_bytes:
            raise ValueError("saved document exceeds configured size limit")

        total = 0
        with destination.open("wb") as output:
            while chunk := response.read(1024 * 1024):
                total += len(chunk)
                if total > max_bytes:
                    raise ValueError("saved document exceeds configured size limit")
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())


class OfficeGatewayServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(
        self,
        config: RuntimeConfig,
        documents: dict[str, dict[str, Any]],
    ) -> None:
        self.config = config
        self.documents = documents
        self.save_locks = {kind: threading.Lock() for kind in documents}
        super().__init__((config.bind, config.port), Handler)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "KHZOfficeGateway/0.2"
    sys_version = ""

    @property
    def gateway(self) -> OfficeGatewayServer:
        return self.server  # type: ignore[return-value]

    def log_message(self, fmt: str, *args: Any) -> None:
        return

    def log_request(self, code: int | str = "-", size: int | str = "-") -> None:
        safe_path = urllib.parse.urlsplit(self.path).path
        print(
            time.strftime("%Y-%m-%dT%H:%M:%S"),
            self.client_address[0],
            self.command,
            safe_path,
            code,
            size,
            flush=True,
        )

    def _security_headers(self) -> None:
        self.send_header("Cache-Control", "no-store")
        self.send_header("Pragma", "no-cache")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header(
            "Permissions-Policy",
            "camera=(), microphone=(), geolocation=(), payment=()",
        )

    def _session_cookie(self) -> str:
        token = self.gateway.config.gateway_token
        ttl = self.gateway.config.capability_ttl_seconds
        return (
            f"{SESSION_COOKIE}={token}; HttpOnly; SameSite=Strict; "
            f"Path=/; Max-Age={ttl}"
        )

    def _send_json(
        self,
        code: int,
        value: Mapping[str, Any],
        *,
        set_session_cookie: bool = False,
        authenticate: bool = False,
    ) -> None:
        raw = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode(
            "utf-8"
        )
        self.send_response(code)
        self._security_headers()
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        if set_session_cookie:
            self.send_header("Set-Cookie", self._session_cookie())
        if authenticate:
            self.send_header("WWW-Authenticate", 'Bearer realm="KHZ Office"')
        self.end_headers()
        self.wfile.write(raw)

    def _require_browser(self) -> tuple[bool, bool]:
        authorized, set_cookie = browser_authorized(
            self.headers,
            self.gateway.config.gateway_token,
        )
        if not authorized:
            self._send_json(
                HTTPStatus.UNAUTHORIZED,
                {"error": "authentication required"},
                authenticate=True,
            )
        return authorized, set_cookie

    def _document(
        self,
        path: str,
        prefix: str,
    ) -> tuple[str | None, dict[str, Any] | None]:
        if path == f"{prefix}/InstitutionalWorkbook.xlsx":
            kind = DEFAULT_DOCUMENT
        elif path.startswith(prefix + "/"):
            kind = path.removeprefix(prefix + "/").strip("/")
        else:
            return None, None
        return kind, self.gateway.documents.get(kind)

    def _document_server_source_allowed(self) -> bool:
        try:
            source = ipaddress.ip_address(self.client_address[0])
        except ValueError:
            return False
        return source == self.gateway.config.document_server_ip

    def _verify_server_get(
        self,
        query: Mapping[str, list[str]],
        purpose: str,
        kind: str,
    ) -> bool:
        capability = query.get("cap", [""])[0]
        if not verify_capability(
            capability,
            self.gateway.config.jwt_secret,
            purpose,
            kind,
        ):
            return False
        if not self._document_server_source_allowed():
            return False
        token = authorization_bearer(self.headers)
        if token is None:
            return False
        try:
            claims = decode_and_verify_jwt(token, self.gateway.config.jwt_secret)
        except (TypeError, ValueError, json.JSONDecodeError):
            return False
        payload = claims.get("payload")
        expected_url = self.gateway.config.internal_base + self.path
        return isinstance(payload, dict) and hmac.compare_digest(
            str(payload.get("url", "")),
            expected_url,
        )

    def _verify_server_post(
        self,
        query: Mapping[str, list[str]],
        kind: str,
        payload: Mapping[str, Any],
    ) -> bool:
        capability = query.get("cap", [""])[0]
        if not verify_capability(
            capability,
            self.gateway.config.jwt_secret,
            "callback",
            kind,
        ):
            return False
        if not self._document_server_source_allowed():
            return False
        token = authorization_bearer(self.headers)
        if token is None:
            return False
        try:
            claims = decode_and_verify_jwt(token, self.gateway.config.jwt_secret)
        except (TypeError, ValueError, json.JSONDecodeError):
            return False
        signed_payload = claims.get("payload")
        return isinstance(signed_payload, dict) and signed_payload == payload

    def do_GET(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)

        if path == "/health":
            authorized, set_cookie = self._require_browser()
            if not authorized:
                return
            self._send_json(
                HTTPStatus.OK,
                {
                    "status": "ok",
                    "authentication": "required",
                    "documents": {
                        kind: {
                            "name": info["path"].name,
                            "exists": info["path"].exists(),
                            "document_type": info["document_type"],
                            "file_type": info["file_type"],
                        }
                        for kind, info in self.gateway.documents.items()
                    },
                    "document_server": self.gateway.config.docs_browser,
                },
                set_session_cookie=set_cookie,
            )
            return

        kind, document = self._document(path, "/file")
        if kind is not None:
            if document is None:
                self._send_json(
                    HTTPStatus.NOT_FOUND,
                    {"error": "unknown document type"},
                )
                return
            if not self._verify_server_get(query, "file", kind):
                self._send_json(
                    HTTPStatus.UNAUTHORIZED,
                    {"error": "invalid document request"},
                )
                return
            target = document["path"]
            if not target.exists():
                self._send_json(
                    HTTPStatus.NOT_FOUND,
                    {"error": "document not found"},
                )
                return
            size = target.stat().st_size
            if size > self.gateway.config.max_document_bytes:
                self._send_json(
                    HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                    {"error": "document is too large"},
                )
                return
            self.send_response(HTTPStatus.OK)
            self._security_headers()
            self.send_header("Content-Type", document["content_type"])
            self.send_header("Content-Length", str(size))
            self.send_header(
                "Content-Disposition",
                f'inline; filename="{target.name}"',
            )
            self.end_headers()
            with target.open("rb") as source:
                while chunk := source.read(1024 * 1024):
                    self.wfile.write(chunk)
            return

        kind, document = self._document(path, "/editor")
        if path == "/editor":
            kind = DEFAULT_DOCUMENT
            document = self.gateway.documents.get(kind)
        if kind is not None:
            authorized, set_cookie = self._require_browser()
            if not authorized:
                return
            if document is None:
                self._send_json(
                    HTTPStatus.NOT_FOUND,
                    {"error": "unknown document type"},
                )
                return
            target = document["path"]
            if not target.exists():
                self._send_json(
                    HTTPStatus.NOT_FOUND,
                    {"error": "document not found"},
                )
                return
            self._send_editor(kind, document, set_cookie)
            return

        self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def _send_editor(
        self,
        kind: str,
        document: Mapping[str, Any],
        set_session_cookie: bool,
    ) -> None:
        target: Path = document["path"]
        now = int(time.time())
        expires_at = now + self.gateway.config.capability_ttl_seconds
        file_capability = create_capability(
            self.gateway.config.jwt_secret,
            "file",
            kind,
            expires_at,
        )
        callback_capability = create_capability(
            self.gateway.config.jwt_secret,
            "callback",
            kind,
            expires_at,
        )
        file_url = (
            self.gateway.config.internal_base
            + f"/file/{kind}?"
            + urllib.parse.urlencode({"cap": file_capability})
        )
        callback_url = (
            self.gateway.config.internal_base
            + f"/callback/{kind}?"
            + urllib.parse.urlencode({"cap": callback_capability})
        )

        config: dict[str, Any] = {
            "documentType": document["document_type"],
            "document": {
                "fileType": document["file_type"],
                "key": document_key(target),
                "title": target.name,
                "url": file_url,
                "permissions": {"edit": True, "download": True, "print": True},
            },
            "editorConfig": {
                "mode": "edit",
                "lang": "en",
                "callbackUrl": callback_url,
                "user": {"id": "khz-local-user", "name": "KHZ Local User"},
                "customization": {"forcesave": True},
            },
            "type": "desktop",
            "width": "100%",
            "height": "100%",
            "iat": now,
            "exp": expires_at,
        }
        config["token"] = encode_jwt(config, self.gateway.config.jwt_secret)
        config_json = (
            json.dumps(config, separators=(",", ":"))
            .replace("<", "\\u003c")
            .replace(">", "\\u003e")
            .replace("&", "\\u0026")
        )

        nonce = secrets.token_urlsafe(18)
        docs_origin = self.gateway.config.docs_browser
        page = f"""<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>KHZ Office - {html.escape(kind)}</title>
<style nonce="{nonce}">
html, body, #editor {{ width: 100%; height: 100%; margin: 0; padding: 0; overflow: hidden; }}
</style>
<script nonce="{nonce}" src="{html.escape(docs_origin, quote=True)}/web-apps/apps/api/documents/api.js"></script>
</head>
<body>
<div id="editor"></div>
<script nonce="{nonce}">
const config = {config_json};
window.khzeditor = new DocsAPI.DocEditor("editor", config);
</script>
</body>
</html>
"""
        raw = page.encode("utf-8")

        self.send_response(HTTPStatus.OK)
        self._security_headers()
        self.send_header(
            "Content-Security-Policy",
            "; ".join(
                [
                    "default-src 'none'",
                    f"script-src 'nonce-{nonce}' {docs_origin}",
                    f"frame-src {docs_origin}",
                    (
                        f"connect-src {docs_origin} "
                        "ws://localhost:8088 ws://127.0.0.1:8088"
                    ),
                    f"img-src {docs_origin} data: blob:",
                    f"font-src {docs_origin} data:",
                    "style-src 'unsafe-inline'",
                    "object-src 'none'",
                    "base-uri 'none'",
                    "form-action 'none'",
                ]
            ),
        )
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        if set_session_cookie:
            self.send_header("Set-Cookie", self._session_cookie())
        self.end_headers()
        self.wfile.write(raw)

    def do_POST(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)

        kind, document = self._document(path, "/callback")
        if path == "/callback":
            kind = DEFAULT_DOCUMENT
            document = self.gateway.documents.get(kind)
        if kind is None:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": 1})
            return
        if document is None:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": 1})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > MAX_CALLBACK_BYTES:
                raise ValueError("invalid callback size")
            body = self.rfile.read(length)
            payload = json.loads(body.decode("utf-8"))
            if not isinstance(payload, dict):
                raise ValueError("callback payload must be an object")
            if not self._verify_server_post(query, kind, payload):
                self._send_json(HTTPStatus.UNAUTHORIZED, {"error": 1})
                return

            status = int(payload.get("status", 0))
            saved = False
            if status in {2, 6}:
                save_url = str(payload.get("url", ""))
                if not allowed_save_url(
                    save_url,
                    self.gateway.config.document_server_ip,
                ):
                    raise ValueError(
                        "refusing callback URL outside Document Server"
                    )
                self._save_document(kind, document["path"], save_url)
                saved = True

            self.gateway.config.report.parent.mkdir(parents=True, exist_ok=True)
            with self.gateway.config.report.open("a", encoding="utf-8") as report:
                report.write(
                    json.dumps(
                        _safe_callback_record(kind, payload, saved),
                        ensure_ascii=False,
                        separators=(",", ":"),
                    )
                    + "\n"
                )
            self._send_json(HTTPStatus.OK, {"error": 0})
        except Exception as exc:
            print(
                f"CALLBACK ERROR kind={kind} type={type(exc).__name__}",
                flush=True,
            )
            self._send_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": 1})

    def _save_document(self, kind: str, target: Path, save_url: str) -> None:
        temp_path: Path | None = None
        with self.gateway.save_locks[kind]:
            try:
                with tempfile.NamedTemporaryFile(
                    mode="wb",
                    prefix="khz-onlyoffice-",
                    suffix=target.suffix,
                    dir=str(target.parent),
                    delete=False,
                ) as temporary:
                    temp_path = Path(temporary.name)
                download_document(
                    save_url,
                    temp_path,
                    self.gateway.config.max_document_bytes,
                )
                if target.exists():
                    os.chmod(temp_path, target.stat().st_mode & 0o777)
                os.replace(temp_path, target)
                temp_path = None
                print(f"SAVED kind={kind} status=verified", flush=True)
            finally:
                if temp_path is not None:
                    temp_path.unlink(missing_ok=True)


def main() -> None:
    try:
        config = load_runtime_config()
    except (TypeError, ValueError) as exc:
        raise SystemExit(
            f"invalid KHZ Office gateway configuration: {exc}"
        ) from exc

    documents = build_documents(config.document_dir)
    missing = [
        str(info["path"])
        for info in documents.values()
        if not info["path"].is_file()
    ]
    if missing:
        raise SystemExit(
            "missing staged Office document(s): " + ", ".join(missing)
        )

    print(
        f"KHZ Office Gateway listening on http://{config.bind}:{config.port}",
        flush=True,
    )
    print("Authentication: REQUIRED", flush=True)
    OfficeGatewayServer(config, documents).serve_forever()


if __name__ == "__main__":
    main()
