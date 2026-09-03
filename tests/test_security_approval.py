from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from khz_workstation.models import ActionProposal, Classification, ContextManifest
from khz_workstation.security.approval import (
    Approval,
    ApprovalError,
    ApprovalLedger,
    ApprovalReplayError,
    subject_digest,
)
from khz_workstation.terminal import TerminalService


class ApprovalBindingTests(unittest.TestCase):
    def test_approval_is_bound_to_the_exact_subject(self):
        approval = Approval.for_subject("echo safe", proposal_id="p1", granted_by="user")
        self.assertTrue(approval.matches("echo safe"))
        self.assertFalse(approval.matches("echo safe "))
        self.assertFalse(approval.matches("rm -rf /"))
        with self.assertRaises(ApprovalError):
            approval.verify("rm -rf /")

    def test_digest_is_nfc_normalized(self):
        composed = "echo \u0623"
        decomposed = "echo \u0627\u0654"
        self.assertNotEqual(composed, decomposed)
        self.assertEqual(subject_digest(composed), subject_digest(decomposed))

    def test_approval_requires_a_grantor_and_proposal(self):
        with self.assertRaises(ValueError):
            Approval.for_subject("echo x", proposal_id="", granted_by="user")
        with self.assertRaises(ValueError):
            Approval.for_subject("echo x", proposal_id="p1", granted_by="")

    def test_ledger_refuses_replay(self):
        ledger = ApprovalLedger()
        approval = Approval.for_subject("echo once", proposal_id="p1", granted_by="user")
        ledger.consume(approval, "echo once")
        self.assertTrue(ledger.is_spent(approval))
        with self.assertRaises(ApprovalReplayError):
            ledger.consume(approval, "echo once")
        self.assertEqual(ledger.spent_count, 1)


class TerminalApprovalTests(unittest.TestCase):
    def test_substituted_command_is_refused(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True, require_approval=True)
            approval = Approval.for_subject("echo approved", proposal_id="p1", granted_by="user")
            with self.assertRaises(ApprovalError):
                term.run("echo substituted", approval=approval)

    def test_boolean_alone_cannot_satisfy_a_bound_terminal(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True, require_approval=True)
            with self.assertRaises(PermissionError):
                term.run("echo blocked", authorized=True)

    def test_disabled_terminal_refuses_even_a_valid_approval(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=False, require_approval=True)
            approval = Approval.for_subject("echo x", proposal_id="p1", granted_by="user")
            with self.assertRaises(PermissionError):
                term.run("echo x", approval=approval)

    def test_bound_approval_executes_and_is_recorded(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True, require_approval=True)
            approval = Approval.for_subject("echo 24680", proposal_id="p1", granted_by="user")
            result = term.run("echo 24680", approval=approval, timeout=20)
            self.assertEqual(result.exit_code, 0)
            self.assertIn("24680", result.stdout)
            self.assertEqual(result.command_sha256, subject_digest("echo 24680"))
            self.assertEqual(result.approval_binding, "DIGEST_BOUND")

    def test_legacy_boolean_path_still_works_but_is_labelled(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True)
            result = term.run("echo 13579", authorized=True, timeout=20)
            self.assertEqual(result.exit_code, 0)
            self.assertEqual(result.approval_binding, "LEGACY_BOOLEAN")

    def test_unauthorized_call_is_still_refused(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True)
            with self.assertRaises(PermissionError):
                term.run("echo blocked", authorized=False)


class TerminalIsolationTests(unittest.TestCase):
    def test_parent_secrets_are_not_inherited(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True)
            with mock.patch.dict(os.environ, {"HF_TOKEN": "must-not-leak", "AZURE_OPENAI_KEY": "must-not-leak"}, clear=False):
                env = term.build_env()
            self.assertNotIn("HF_TOKEN", env)
            self.assertNotIn("AZURE_OPENAI_KEY", env)
            self.assertTrue(any(key.upper() == "PATH" for key in env))

    def test_explicit_env_is_still_honoured(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True)
            env = term.build_env({"KHZ_EXPLICIT": "1"})
            self.assertEqual(env["KHZ_EXPLICIT"], "1")

    def test_output_is_capped_and_flagged(self):
        with tempfile.TemporaryDirectory() as td:
            term = TerminalService(Path(td), enabled=True, max_output_bytes=8)
            result = term.run("echo 0123456789ABCDEF", authorized=True, timeout=20)
            self.assertTrue(result.truncated)
            self.assertIn("truncated", result.stdout)

    def test_working_subdirectory_cannot_escape_the_workspace(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td) / "ws"
            root.mkdir()
            term = TerminalService(root, enabled=True)
            with self.assertRaises(ValueError):
                term.run("echo x", authorized=True, working_subdirectory="../..")

    def test_missing_workspace_root_fails_at_construction(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(OSError):
                TerminalService(Path(td) / "does-not-exist", enabled=True)


class ModelHardeningTests(unittest.TestCase):
    def test_unclassified_context_is_rejected(self):
        with self.assertRaises(ValueError):
            ContextManifest("w", "a", "A1:B2", 4, None)  # type: ignore[arg-type]

    def test_classified_context_is_accepted(self):
        manifest = ContextManifest("w", "a", "A1:B2", 4, Classification.INTERNAL)
        self.assertEqual(manifest.classification, Classification.INTERNAL)

    def test_proposal_args_snapshot_the_caller_dict(self):
        raw = {"formula": "=E2*0.05"}
        proposal = ActionProposal(action="SetFormula", workspace_id="w", target="F2", args=raw)
        raw["formula"] = "=DELETE()"
        self.assertEqual(proposal.args["formula"], "=E2*0.05")

    def test_proposal_args_are_read_only(self):
        proposal = ActionProposal(
            action="SetFormula", workspace_id="w", target="F2", args={"formula": "=E2*0.05"}
        )
        with self.assertRaises(TypeError):
            proposal.args["formula"] = "=OTHER()"  # type: ignore[index]

    def test_nested_proposal_args_are_read_only(self):
        proposal = ActionProposal(
            action="SetFormula",
            workspace_id="w",
            target="F2",
            args={"range": {"sheet": "Summary"}},
        )
        with self.assertRaises(TypeError):
            proposal.args["range"]["sheet"] = "Other"  # type: ignore[index]
        self.assertEqual(proposal.args_as_dict()["range"]["sheet"], "Summary")


if __name__ == "__main__":
    unittest.main()
