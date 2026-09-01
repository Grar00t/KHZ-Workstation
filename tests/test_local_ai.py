from __future__ import annotations

import hashlib
import json
import tempfile
import unittest

from pathlib import Path

from khz_workstation.ai.local_server import (
    LocalAiServer,
    build_mcp_configuration,
    build_server_environment,
)
from khz_workstation.ai.model_manager import (
    InstalledModel,
    MODEL_CATALOG,
    ModelLicenseAcceptanceRequired,
    ModelManager,
    ModelManagerError,
    build_download_environment,
    normalize_family,
)
from khz_workstation.ai.proposals import ProposalError, create_write_proposal
from khz_workstation.ai.workspace_mcp import WorkspaceMcp
from khz_workstation.workspace import Workspace


class StubModelManager(ModelManager):
    def _download(self, spec):
        destination = self.cache_dir / f"{spec.family}-test.gguf"
        destination.write_bytes(b"GGUF\0test-model")
        return destination


class ModelManagerTests(unittest.TestCase):
    def test_catalog_aliases_and_license_gate(self):
        self.assertEqual(normalize_family("lama"), "llama")
        self.assertEqual(normalize_family("QWEN"), "qwen")
        self.assertEqual(set(MODEL_CATALOG), {"llama", "qwen", "phi"})
        with tempfile.TemporaryDirectory() as temporary:
            manager = StubModelManager(Path(temporary))
            with self.assertRaises(ModelLicenseAcceptanceRequired):
                manager.pull("llama")

    def test_pull_writes_verified_manifest_and_detects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            manager = StubModelManager(Path(temporary))
            installed = manager.pull("qwen")
            self.assertEqual(installed.family, "qwen")
            self.assertEqual(
                installed.sha256,
                hashlib.sha256(b"GGUF\0test-model").hexdigest(),
            )
            self.assertEqual(manager.require_verified("qwen"), installed)
            Path(installed.path).write_bytes(b"tampered-model!")
            with self.assertRaises(ModelManagerError):
                manager.require_verified("qwen")

    def test_download_environment_requires_explicit_proxy_and_drops_credentials(self):
        environment = build_download_environment(
            Path("/models/cache"),
            {
                "PATH": "/tools",
                "AWS_SECRET_ACCESS_KEY": "do-not-inherit",
                "HF_TOKEN": "do-not-inherit",
                "HTTPS_PROXY": "https://implicit.invalid",
                "KHZ_MODEL_DOWNLOAD_PROXY": "https://explicit.invalid",
            },
        )
        self.assertEqual(environment["PATH"], "/tools")
        self.assertEqual(environment["LLAMA_CACHE"], "/models/cache")
        self.assertEqual(
            environment["HTTPS_PROXY"],
            "https://explicit.invalid",
        )
        self.assertNotIn("AWS_SECRET_ACCESS_KEY", environment)
        self.assertNotIn("HF_TOKEN", environment)


class WorkspaceMcpTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "workspace"
        self.workspace = Workspace.create(self.root, "AI Test")
        (self.root / "notes.txt").write_text("alpha\nbeta alpha\n", encoding="utf-8")
        (self.root / ".git").mkdir()
        (self.root / ".git" / "config").write_text("secret", encoding="utf-8")
        self.server = WorkspaceMcp(self.workspace)

    def tearDown(self):
        self.temporary.cleanup()

    def test_read_search_and_metadata_boundaries(self):
        read = self.server.workspace_read_text({"path": "notes.txt"})
        self.assertIn("beta alpha", read["content"])
        search = self.server.workspace_search_text({"query": "alpha"})
        self.assertEqual(len(search["matches"]), 2)
        with self.assertRaises(PermissionError):
            self.server.workspace_read_text({"path": ".khz/workspace.json"})
        with self.assertRaises(PermissionError):
            self.server.workspace_read_text({"path": ".git/config"})

    def test_symlink_escape_is_not_listed_or_read(self):
        outside = Path(self.temporary.name) / "outside.txt"
        outside.write_text("outside secret", encoding="utf-8")
        link = self.root / "linked.txt"
        try:
            link.symlink_to(outside)
        except (OSError, NotImplementedError):
            self.skipTest("Symlinks are unavailable")
        listing = self.server.workspace_list({})
        self.assertNotIn("linked.txt", [item["path"] for item in listing["entries"]])
        with self.assertRaises(ValueError):
            self.server.workspace_read_text({"path": "linked.txt"})
        search = self.server.workspace_search_text({"query": "outside secret"})
        self.assertEqual(search["matches"], [])

    def test_write_tool_only_creates_pending_proposal(self):
        original = (self.root / "notes.txt").read_text(encoding="utf-8")
        expected = hashlib.sha256(original.encode("utf-8")).hexdigest()
        result = self.server.workspace_propose_write_text(
            {
                "path": "notes.txt",
                "content": "replacement\n",
                "expected_sha256": expected,
            }
        )
        self.assertEqual(result["status"], "PENDING")
        self.assertFalse(result["target_modified"])
        self.assertEqual(
            (self.root / "notes.txt").read_text(encoding="utf-8"),
            original,
        )
        proposal_path = (
            self.root / ".khz" / "ai-proposals" / f"{result['proposal_id']}.json"
        )
        proposal = json.loads(proposal_path.read_text(encoding="utf-8"))
        self.assertEqual(proposal["status"], "PENDING")

    def test_proposal_rejects_stale_hash_and_protected_paths(self):
        with self.assertRaises(ProposalError):
            create_write_proposal(
                self.workspace,
                target="notes.txt",
                content="new",
                expected_sha256="0" * 64,
            )
        with self.assertRaises(ProposalError):
            create_write_proposal(
                self.workspace,
                target=".git/config",
                content="new",
            )

    def test_json_rpc_lists_only_bounded_tools(self):
        response = self.server.handle(
            {"jsonrpc": "2.0", "id": 1, "method": "tools/list"}
        )
        names = {
            item["name"] for item in response["result"]["tools"]  # type: ignore[index]
        }
        self.assertEqual(
            names,
            {
                "workspace_list",
                "workspace_read_text",
                "workspace_search_text",
                "workspace_propose_write_text",
            },
        )

    def test_mcp_configuration_binds_identity_and_has_no_shell_tool(self):
        config = build_mcp_configuration(self.workspace)
        definition = config["mcpServers"]["khz-workspace"]
        self.assertIn(self.workspace.info.workspace_id, definition["args"])
        self.assertEqual(definition["command"], __import__("sys").executable)
        rendered = json.dumps(config)
        self.assertNotIn("exec_shell_command", rendered)


class LocalServerCommandTests(unittest.TestCase):
    def test_server_enables_only_workspace_mcp_and_loopback(self):
        installed = InstalledModel(
            family="qwen",
            display_name="Qwen",
            source="source",
            source_url="https://example.invalid",
            quantization="Q4_K_M",
            context_size=4096,
            license_name="Apache-2.0",
            license_url="https://example.invalid/license",
            license_accepted=True,
            path="/models/qwen.gguf",
            size_bytes=1,
            sha256="a" * 64,
            installed_utc="now",
        )
        command = LocalAiServer._command(
            Path("/bin/llama-server"),
            installed,
            Path("/runtime/mcp.json"),
            8091,
            4096,
        )
        self.assertIn("127.0.0.1", command)
        self.assertIn("--no-agent", command)
        self.assertIn("--mcp-servers-config", command)
        self.assertNotIn("--tools", command)

    def test_server_environment_drops_parent_credentials_and_flag_overrides(self):
        environment = build_server_environment(
            "session-token",
            {
                "PATH": "/tools",
                "HOME": "/home/test",
                "AWS_SECRET_ACCESS_KEY": "do-not-inherit",
                "LLAMA_ARG_AGENT": "true",
                "LLAMA_API_KEY": "parent-token",
            },
        )
        self.assertEqual(environment["LLAMA_API_KEY"], "session-token")
        self.assertEqual(environment["PATH"], "/tools")
        self.assertNotIn("AWS_SECRET_ACCESS_KEY", environment)
        self.assertNotIn("LLAMA_ARG_AGENT", environment)


if __name__ == "__main__":
    unittest.main()
