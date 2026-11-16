import argparse
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

import privacy_negative_probe as probe


class ProbeTests(unittest.TestCase):
    def test_whole_operation_deadline_does_not_reset_per_read(self):
        with patch.object(probe.time, "monotonic", side_effect=[100, 110, 129, 131]):
            deadline = probe.Deadline(30)
            self.assertEqual(20, deadline.remaining())
            self.assertEqual(1, deadline.remaining())
            with self.assertRaises(TimeoutError):
                deadline.remaining()

    def test_fixed_environment_excludes_injection_and_secrets(self):
        environment = probe.environment(Path("/dotnet"), Path("/operation"))
        self.assertEqual("false", environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"])
        self.assertEqual("0x20000000", environment["DOTNET_GCHeapHardLimit"])
        for name in ("LD_PRELOAD", "LD_LIBRARY_PATH", "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING",
                     "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "NUGET_PACKAGES", "SECRET"):
            self.assertNotIn(name, environment)

    def test_provenance_uses_the_surviving_image_not_host_path_metadata(self):
        root = Path("/dotnet")
        self.assertEqual(Path("/launcher"), probe.managed_image("/launcher"))
        self.assertEqual(root / "dotnet", probe.managed_image("/dotnet/dotnet"))
        child = Mock(pid=12)
        with self.assertRaisesRegex(RuntimeError, "provenance"):
            probe.observe(None, child, {"pid": 12, "exe": "/dotnet/dotnet"}, Path("/launcher"), {}, probe.Deadline(30))

    def test_offline_fixture_emission_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "mock-sdk"
            compiler = root / f"sdk/{probe.SDK}/Roslyn/bincore/csc.dll"
            compiler.parent.mkdir(parents=True)
            compiler.write_bytes(b"mock, never execute")
            refs = root / f"packs/Microsoft.NETCore.App.Ref/{probe.RUNTIME}/ref/net10.0"
            refs.mkdir(parents=True)
            (refs / "System.Runtime.dll").write_bytes(b"mock")
            directory = Path(temporary) / "fixture"
            args = argparse.Namespace(dotnet_root=root, directory=directory)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                probe.prepare(args)
            command = json.loads(output.getvalue())["compile_command"]
            self.assertEqual("exec", command[1])
            self.assertEqual("-noconfig", command[-2])
            config = json.loads((directory / "braid-privacy-probe.runtimeconfig.json").read_text())
            self.assertEqual(probe.RUNTIME, config["runtimeOptions"]["framework"]["version"])
            self.assertEqual("Disable", config["runtimeOptions"]["rollForward"])
            with self.assertRaises(RuntimeError):
                probe.prepare(args)

    def test_saved_or_filesystem_credentials_and_capabilities_cannot_be_hidden(self):
        normal = {"Uid:": "65532 65532 65532 65532", "Gid:": "65532 65532 65532 65532",
                  "CapInh:": "0", "CapPrm:": "0", "CapEff:": "0", "CapAmb:": "0", "TracerPid:": "0"}
        probe.credentials(normal, normal)
        for key, value in (("Uid:", "65532 65532 0 65532"), ("Gid:", "65532 65532 65532 1"),
                           ("CapEff:", "1"), ("CapAmb:", "1"), ("TracerPid:", "123")):
            changed = normal | {key: value}
            with self.assertRaises(RuntimeError):
                probe.credentials(changed, normal)
        with self.assertRaises(RuntimeError):
            probe.credentials(normal | {"Uid:": "1 2 3 4"}, normal | {"Uid:": "1 2 3 4"})

    def test_one_aggregate_cleanup_budget_and_uncertainty_is_reported(self):
        child = Mock()
        child.poll.return_value = None
        child.wait.side_effect = probe.subprocess.TimeoutExpired("synthetic", 1)
        deadline = Mock()
        deadline.remaining.return_value = 0.5
        self.assertFalse(probe.settle([child], deadline))
        child.kill.assert_called_once()
        child.wait.assert_called_once_with(timeout=0.5)
        child.stdout.close.assert_not_called()

    def test_successful_settlement_reaps_then_closes_all_pipes(self):
        child = Mock()
        child.poll.return_value = 0
        self.assertTrue(probe.settle([child], probe.Deadline(2)))
        child.kill.assert_not_called()
        child.wait.assert_called_once()
        for stream in (child.stdin, child.stdout, child.stderr):
            stream.close.assert_called_once()

    def test_windows_cannot_accidentally_run_linux_probe(self):
        with patch.object(probe.sys, "platform", "win32"):
            with self.assertRaisesRegex(RuntimeError, "Linux x86-64 non-root"):
                probe.run(argparse.Namespace())

    def test_uncertain_settlement_retains_owned_directory(self):
        self.run_mocked_failure(uncertain=True)

    def test_blocked_positive_control_is_inconclusive_not_a_negative_pass(self):
        self.run_mocked_failure(uncertain=False)

    def run_mocked_failure(self, uncertain):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assembly, launcher = root / "braid-privacy-probe.dll", root / "launcher"
            assembly.touch(); launcher.touch()
            operation = root / "operation"; operation.mkdir()
            args = argparse.Namespace(dotnet_root=root, probe_assembly=assembly, launcher=launcher,
                                      work_root=root, expect="protected")
            values = {"Uid": "65532 65532 65532 65532", "Gid": "65532 65532 65532 65532",
                      "CapInh": "0", "CapPrm": "0", "CapEff": "0", "CapAmb": "0", "TracerPid": "0"}
            blocked = {"self": {"dump": 1}} | {name: {"allowed": False} for name in ("attach", "environ", "maps", "mem", "memory")}
            with patch.object(probe.sys, "platform", "linux"), patch.object(probe.platform, "machine", return_value="x86_64"), \
                    patch.object(probe.os, "getuid", return_value=65532, create=True), patch.object(probe, "status", return_value=values), \
                    patch.object(probe.ctypes, "CDLL", return_value=Mock()), patch.object(probe, "provenance", return_value={}), \
                    patch.object(probe.tempfile, "mkdtemp", return_value=str(operation)), patch.object(probe, "sample", return_value=[blocked]), \
                    patch.object(probe, "settle", return_value=not uncertain), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, "cleanup was not confirmed" if uncertain else "Positive control blocked: INCONCLUSIVE"):
                    probe.run(args)
            self.assertEqual(uncertain, operation.exists())


if __name__ == "__main__":
    unittest.main()
