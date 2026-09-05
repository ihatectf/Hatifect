"""Launch behavior: borrowed stdio, explicit builds, failures and paths with spaces."""
from __future__ import annotations

import contextlib
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ui_language_server as server


class UiLanguageServerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-authoring-tests.")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "repository with spaces"
        self.root.mkdir()
        self.assembly = self.root / server.PROJECT.parent / "bin/Release/net6.0/Hatifect.UI.Tooling.Server.dll"
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        self.dotnet = "/fixture SDK/dotnet"

    def run_server(self, *args: str):
        with mock.patch.object(server, "resolve_dotnet", return_value=self.dotnet), \
                mock.patch.object(server.os, "execv") as execute, \
                contextlib.redirect_stdout(self.stdout), contextlib.redirect_stderr(self.stderr):
            code = server.main(list(args), root=self.root)
        return code, execute

    def write_assembly(self) -> None:
        self.assembly.parent.mkdir(parents=True, exist_ok=True)
        self.assembly.write_bytes(b"fixture")

    def test_existing_server_executes_directly_with_no_protocol_noise_or_build(self) -> None:
        self.write_assembly()
        with mock.patch.object(server.subprocess, "run") as build:
            code, execute = self.run_server()
        self.assertEqual(code, 0)
        execute.assert_called_once_with(self.dotnet, [self.dotnet, str(self.assembly)])
        build.assert_not_called()
        self.assertEqual(self.stdout.getvalue(), "")
        self.assertEqual(self.stderr.getvalue(), "")

    def test_missing_artifact_explains_build_step_on_stderr_and_does_not_launch(self) -> None:
        code, execute = self.run_server()
        self.assertEqual(code, 2)
        execute.assert_not_called()
        self.assertIn("--build", self.stderr.getvalue())
        self.assertEqual(self.stdout.getvalue(), "")

    def test_failed_explicit_build_preserves_exit_code_and_never_launches_old_artifact(self) -> None:
        self.write_assembly()
        with mock.patch.object(server.subprocess, "run", return_value=subprocess.CompletedProcess([], 23)):
            code, execute = self.run_server("--build")
        self.assertEqual(code, 23)
        execute.assert_not_called()
        self.assertEqual(self.stdout.getvalue(), "")

    def test_explicit_build_sends_all_logs_to_stderr_and_disables_deployment(self) -> None:
        def build(command, **kwargs):
            self.assertIs(kwargs["stdout"], self.stderr)
            self.assertIs(kwargs["stderr"], self.stderr)
            self.assertIn("-p:EnableModDeploy=false", command)
            self.assertIn("-p:HatifectDeploySuite=false", command)
            print("build output", file=kwargs["stdout"])
            self.write_assembly()
            return subprocess.CompletedProcess(command, 0)

        with mock.patch.object(server.subprocess, "run", side_effect=build):
            code, execute = self.run_server("--build")
        self.assertEqual(code, 0)
        execute.assert_called_once()
        self.assertEqual(self.stdout.getvalue(), "")
        self.assertEqual(self.stderr.getvalue(), "build output\n")


if __name__ == "__main__":
    unittest.main()
