from __future__ import annotations

import base64
import importlib.util
import ipaddress
import json
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request

from pathlib import Path


MODULE_PATH = (
    Path(__file__).resolve().parents[1]
    / "tools"
    / "office-spike"
    / "gateway.py"
)
LAUNCHER_PATH = MODULE_PATH.with_name("start-spike.sh")

SPEC = importlib.util.spec_from_file_location(
    "khz_office_gateway",
    MODULE_PATH,
)

if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Unable to load the KHZ Office gateway module.")

gateway = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = gateway
SPEC.loader.exec_module(gateway)


class OfficeGatewaySecurityTests(unittest.TestCase):
    def valid_environment(self, root: Path) -> dict[str, str]:
        return {
            "KHZ_ROOT": str(root),
            "KHZ_OFFICE_DOCUMENT_DIR": str(root / "documents"),
            "KHZ_GATEWAY_BIND": "127.0.0.1",
            "KHZ_GATEWAY_PORT": "8090",
            "KHZ_DOCSERVER_IP": "127.0.0.2",
            "KHZ_DOCS_BROWSER": "http://localhost:8088",
            "KHZ_ONLYOFFICE_JWT_SECRET": "j" * 64,
            "KHZ_OFFICE_GATEWAY_TOKEN": "g" * 64,
        }

    def test_runtime_configuration_requires_strong_separate_secrets(self):
        with tempfile.TemporaryDirectory() as temporary:
            env = self.valid_environment(Path(temporary))
            config = gateway.load_runtime_config(env)
            self.assertEqual(config.bind, "127.0.0.1")
            self.assertEqual(config.document_server_ip, ipaddress.ip_address("127.0.0.2"))

            env["KHZ_ONLYOFFICE_JWT_SECRET"] = "short"
            with self.assertRaises(ValueError):
                gateway.load_runtime_config(env)

            env = self.valid_environment(Path(temporary))
            env["KHZ_OFFICE_GATEWAY_TOKEN"] = "short"
            with self.assertRaises(ValueError):
                gateway.load_runtime_config(env)

    def test_runtime_configuration_reads_secrets_from_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            jwt_file = root / "jwt.secret"
            token_file = root / "token.secret"
            jwt_file.write_text("j" * 64 + "\n", encoding="utf-8")
            token_file.write_text("g" * 64 + "\n", encoding="utf-8")
            jwt_file.chmod(0o600)
            token_file.chmod(0o600)
            env = self.valid_environment(root)
            del env["KHZ_ONLYOFFICE_JWT_SECRET"]
            del env["KHZ_OFFICE_GATEWAY_TOKEN"]
            env["KHZ_ONLYOFFICE_JWT_SECRET_FILE"] = str(jwt_file)
            env["KHZ_OFFICE_GATEWAY_TOKEN_FILE"] = str(token_file)
            config = gateway.load_runtime_config(env)
            self.assertEqual(config.jwt_secret, b"j" * 64)
            self.assertEqual(config.gateway_token, "g" * 64)

    def test_launcher_keeps_secrets_out_of_child_command_arguments(self):
        launcher = LAUNCHER_PATH.read_text(encoding="utf-8")
        self.assertNotIn('-e JWT_SECRET=', launcher)
        self.assertNotIn('nohup env \\', launcher)
        self.assertNotIn('-H "Authorization: Bearer $GATEWAY_TOKEN"', launcher)
        self.assertIn('--env-file "$DOCSERVER_ENV_FILE"', launcher)
        self.assertIn('KHZ_ONLYOFFICE_JWT_SECRET_FILE', launcher)

    def test_configuration_rejects_non_loopback_browser_origin(self):
        with tempfile.TemporaryDirectory() as temporary:
            env = self.valid_environment(Path(temporary))
            env["KHZ_DOCS_BROWSER"] = "https://example.com"
            with self.assertRaises(ValueError):
                gateway.load_runtime_config(env)

    def test_hs256_jwt_rejects_tampering_expiry_and_none_algorithm(self):
        secret = b"s" * 64
        token = gateway.encode_jwt(
            {"payload": {"status": 4}, "exp": 200},
            secret,
        )
        claims = gateway.decode_and_verify_jwt(token, secret, now=100)
        self.assertEqual(claims["payload"]["status"], 4)

        with self.assertRaises(ValueError):
            gateway.decode_and_verify_jwt(token + "x", secret, now=100)

        with self.assertRaises(ValueError):
            gateway.decode_and_verify_jwt(token, secret, now=201)

        header = base64.urlsafe_b64encode(
            json.dumps({"alg": "none", "typ": "JWT"}).encode("utf-8")
        ).rstrip(b"=").decode("ascii")
        payload = base64.urlsafe_b64encode(b"{}").rstrip(b"=").decode("ascii")
        with self.assertRaises(ValueError):
            gateway.decode_and_verify_jwt(f"{header}.{payload}.x", secret)

    def test_capabilities_are_route_kind_and_time_scoped(self):
        secret = b"s" * 64
        token = gateway.create_capability(secret, "file", "sheet", 200)
        self.assertTrue(
            gateway.verify_capability(
                token,
                secret,
                "file",
                "sheet",
                now=100,
            )
        )
        self.assertFalse(
            gateway.verify_capability(
                token,
                secret,
                "callback",
                "sheet",
                now=100,
            )
        )
        self.assertFalse(
            gateway.verify_capability(
                token,
                secret,
                "file",
                "document",
                now=100,
            )
        )
        self.assertFalse(
            gateway.verify_capability(
                token,
                secret,
                "file",
                "sheet",
                now=201,
            )
        )

    def test_browser_session_accepts_bearer_or_http_only_cookie_value(self):
        token = "t" * 64
        self.assertEqual(
            gateway.browser_authorized(
                {"Authorization": f"Bearer {token}"},
                token,
            ),
            (True, True),
        )
        self.assertEqual(
            gateway.browser_authorized(
                {"Cookie": f"other=x; {gateway.SESSION_COOKIE}={token}"},
                token,
            ),
            (True, False),
        )
        self.assertEqual(
            gateway.browser_authorized(
                {"Authorization": "Bearer wrong"},
                token,
            ),
            (False, False),
        )

    def test_save_url_is_restricted_to_exact_document_server(self):
        server = ipaddress.ip_address("172.18.0.4")
        self.assertTrue(
            gateway.allowed_save_url(
                "http://172.18.0.4/cache/files/result.docx?token=x",
                server,
            )
        )
        for url in (
            "http://172.18.0.5/cache/files/result.docx",
            "http://localhost/cache/files/result.docx",
            "http://172.18.0.4:8080/cache/files/result.docx",
            "https://172.18.0.4/cache/files/result.docx",
            "http://user@172.18.0.4/cache/files/result.docx",
        ):
            self.assertFalse(gateway.allowed_save_url(url, server), url)

    def test_callback_audit_record_does_not_persist_download_url_or_token(self):
        record = gateway._safe_callback_record(
            "sheet",
            {
                "status": 2,
                "key": "document-key",
                "url": "http://secret/path?token=secret",
                "token": "secret",
            },
            True,
        )
        serialized = json.dumps(record)
        self.assertNotIn("http://", serialized)
        self.assertNotIn("token", serialized)
        self.assertNotIn("document-key", serialized)
        self.assertTrue(record["saved"])

    def test_browser_endpoints_fail_closed_and_emit_security_headers(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            document_dir = root / "documents"
            document_dir.mkdir()
            for name in (
                "InstitutionalReport.docx",
                "InstitutionalWorkbook.xlsx",
                "InstitutionalPresentation.pptx",
                "InstitutionalPacket.pdf",
            ):
                (document_dir / name).write_bytes(b"fixture")

            env = self.valid_environment(root)
            config = gateway.load_runtime_config(env)
            config = gateway.RuntimeConfig(
                root=config.root,
                document_dir=config.document_dir,
                report=config.report,
                bind=config.bind,
                port=0,
                docs_browser=config.docs_browser,
                internal_base=config.internal_base,
                document_server_ip=config.document_server_ip,
                capability_ttl_seconds=config.capability_ttl_seconds,
                max_document_bytes=config.max_document_bytes,
                jwt_secret=config.jwt_secret,
                gateway_token=config.gateway_token,
            )
            server = gateway.OfficeGatewayServer(
                config,
                gateway.build_documents(document_dir),
            )
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            base = f"http://127.0.0.1:{server.server_address[1]}"

            try:
                with self.assertRaises(urllib.error.HTTPError) as unauthorized:
                    urllib.request.urlopen(base + "/health", timeout=2)
                self.assertEqual(unauthorized.exception.code, 401)

                request = urllib.request.Request(
                    base + "/health",
                    headers={
                        "Authorization": (
                            "Bearer "
                            + env["KHZ_OFFICE_GATEWAY_TOKEN"]
                        )
                    },
                )
                with urllib.request.urlopen(request, timeout=2) as response:
                    body = json.load(response)
                    self.assertEqual(body["status"], "ok")
                    self.assertEqual(response.headers["Cache-Control"], "no-store")
                    self.assertEqual(response.headers["X-Frame-Options"], "DENY")
                    self.assertIn("HttpOnly", response.headers["Set-Cookie"])
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
