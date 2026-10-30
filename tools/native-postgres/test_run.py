"""Pure launcher tests; real PostgreSQL qualification is a separate explicit run."""
from dataclasses import replace
from contextlib import ExitStack
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("weft_native_postgres_launcher", Path(__file__).with_name("run.py"))
launcher = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = launcher
spec.loader.exec_module(launcher)


def options():
    return launcher.Options(Path("C:/repo"), Path("C:/q"), Path("C:/results"), Path("C:/sdk/dotnet.exe"),
                            Path("C:/pg"), Path("C:/packages"), "FullyQualifiedName~Example")


class AdmissionTests(unittest.TestCase):
    def test_explicit_short_paths_are_accepted(self):
        with patch.object(launcher, "ordinary"):
            launcher.validate(options())

    def test_long_work_path_fails_before_any_process(self):
        with patch.object(launcher, "ordinary"), self.assertRaises(launcher.QualificationError):
            launcher.validate(replace(options(), work_root=Path("C:/" + "x" * 90)))

    def test_filters_are_explicit_bounded_and_single_line(self):
        for value in ("", "  ", "x\nrun", "x\0y", "x" * 4097):
            with self.subTest(value=value[:10]), patch.object(launcher, "ordinary"), self.assertRaises(launcher.QualificationError):
                launcher.validate(replace(options(), selection=value))

    def test_results_must_not_be_deleted_with_work(self):
        for path in (Path("C:/q"), Path("C:/q/results")):
            with self.subTest(path=path), patch.object(launcher, "ordinary"), self.assertRaises(launcher.QualificationError):
                launcher.validate(replace(options(), results_root=path))

    def test_invalid_deadline_count_or_executable_is_refused(self):
        for changed in (replace(options(), phase_seconds=0), replace(options(), phase_seconds=86401),
                        replace(options(), expected_tests=0), replace(options(), dotnet=Path("C:/sdk/dotnet.cmd"))):
            with patch.object(launcher, "ordinary"), self.assertRaises(launcher.QualificationError):
                launcher.validate(changed)

    def test_links_are_not_adopted(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            with patch.object(Path, "is_symlink", return_value=True), self.assertRaises(launcher.QualificationError):
                launcher.ordinary(path, directory=True)


class EnvironmentTests(unittest.TestCase):
    def test_credentials_hooks_and_conflicting_test_modes_do_not_flow_to_children(self):
        inherited = {"SystemRoot": "C:/Windows", "USERPROFILE": "C:/Users/test", "PATH": "untrusted",
                     "WEFT_POSTGRES_IMAGE": "other", "PGPASSWORD": "private", "AZURE_CLIENT_SECRET": "private",
                     "DOTNET_STARTUP_HOOKS": "hook.dll", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "true"}
        env = launcher.child_environment(options(), Path("C:/q/run"), Path("C:/q/run/clusters"), inherited)
        self.assertEqual(env["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"], "false")
        self.assertEqual(env["WEFT_POSTGRES_BIN"], str(options().postgres_bin))
        self.assertEqual(env["WEFT_POSTGRES_WORK_ROOT"], str(Path("C:/q/run/clusters")))
        self.assertNotIn("untrusted", env["PATH"])
        for key in ("WEFT_POSTGRES_IMAGE", "PGPASSWORD", "AZURE_CLIENT_SECRET", "DOTNET_STARTUP_HOOKS"):
            self.assertNotIn(key, env)
        self.assertEqual(env["USERPROFILE"], "C:/Users/test")


class ResultTests(unittest.TestCase):
    def result(self, outcomes, counts=None, status="Completed", expected=None):
        if counts is None:
            counts = dict(total=len(outcomes), executed=len(outcomes), passed=outcomes.count("Passed"),
                          failed=outcomes.count("Failed"), notExecuted=outcomes.count("NotExecuted"))
        counters = " ".join(f'{key}="{value}"' for key, value in counts.items())
        rows = "".join(f'<UnitTestResult testName="case-{i}" outcome="{value}"/>' for i, value in enumerate(outcomes))
        xml = f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{rows}</Results><ResultSummary outcome="{status}"><Counters {counters}/></ResultSummary></TestRun>'
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "tests.trx"
            path.write_text(xml)
            return launcher.test_result(path, expected)

    def test_complete_passing_result(self):
        result = self.result(["Passed", "Passed"], expected=2)
        self.assertTrue(result["passed"])
        self.assertEqual(result["counts"]["total"], 2)

    def test_failure_skip_empty_and_wrong_count_cannot_pass(self):
        for result in (self.result(["Failed"]), self.result(["NotExecuted"]), self.result([]),
                       self.result(["Passed"], expected=2), self.result(["Passed"], status="Aborted")):
            self.assertFalse(result["passed"])

    def test_counters_alone_cannot_hide_missing_or_failed_rows(self):
        counts = dict(total=1, executed=1, passed=1, failed=0, notExecuted=0)
        self.assertFalse(self.result([], counts)["passed"])
        self.assertFalse(self.result(["Failed"], counts)["passed"])

    def test_missing_or_malformed_result_is_not_success(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "tests.trx"
            with self.assertRaises(launcher.QualificationError): launcher.test_result(path)
            path.write_text("<broken>")
            with self.assertRaises(launcher.QualificationError): launcher.test_result(path)


class SourceTests(unittest.TestCase):
    def test_new_checkout_preserves_original_text_line_endings(self):
        with tempfile.TemporaryDirectory() as root:
            original, checkout = Path(root) / "original", Path(root) / "checkout"
            original.mkdir()
            checkout.mkdir()
            content = b'{\r\n  "version": 1\r\n}\r\n'
            (original / "packages.lock.json").write_bytes(content)
            (checkout / "packages.lock.json").write_bytes(content.replace(b"\r\n", b"\n"))
            expected = {"packages.lock.json": hashlib.sha256(content).hexdigest()}
            self.assertEqual(launcher.align_checkout_bytes(original, checkout, expected), ["packages.lock.json"])
            self.assertEqual((original / "packages.lock.json").read_bytes(), content)
            self.assertEqual((checkout / "packages.lock.json").read_bytes(), content)

    def test_semantic_or_binary_difference_prevents_all_snapshot_changes(self):
        for replacement in (b"different\n", b"same\0\n", b"\xff\n"):
            with self.subTest(replacement=replacement), tempfile.TemporaryDirectory() as root:
                original, checkout = Path(root) / "original", Path(root) / "checkout"
                original.mkdir()
                checkout.mkdir()
                (original / "first.txt").write_bytes(b"first\r\n")
                (checkout / "first.txt").write_bytes(b"first\n")
                (original / "second.txt").write_bytes(b"same\r\n")
                (checkout / "second.txt").write_bytes(replacement)
                expected = {name: launcher.digest(original / name) for name in ("first.txt", "second.txt")}
                with self.assertRaises(launcher.QualificationError):
                    launcher.align_checkout_bytes(original, checkout, expected)
                self.assertEqual((checkout / "first.txt").read_bytes(), b"first\n")
                self.assertEqual((checkout / "second.txt").read_bytes(), replacement)

    def test_changed_original_or_missing_snapshot_file_is_not_repaired(self):
        with tempfile.TemporaryDirectory() as root:
            original, checkout = Path(root) / "original", Path(root) / "checkout"
            original.mkdir()
            checkout.mkdir()
            (original / "file.txt").write_bytes(b"changed\r\n")
            (checkout / "file.txt").write_bytes(b"before\n")
            with self.assertRaises(launcher.QualificationError):
                launcher.align_checkout_bytes(original, checkout, {"file.txt": hashlib.sha256(b"before\r\n").hexdigest()})
            (checkout / "file.txt").unlink()
            with self.assertRaises(launcher.QualificationError):
                launcher.align_checkout_bytes(original, checkout, {"file.txt": launcher.digest(original / "file.txt")})
            self.assertFalse((checkout / "file.txt").exists())

    def test_manifest_reads_only_tracked_files_and_detects_changes(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            (root / "a.txt").write_bytes(b"exact\r\n")
            (root / "private.env").write_bytes(b"not tracked")
            listing = b"100644 blob deadbeef\ta.txt\0"
            with patch.object(launcher.subprocess, "check_output", return_value=listing):
                result = launcher.manifest(root, "head")
            self.assertEqual(result, {"a.txt": hashlib.sha256(b"exact\r\n").hexdigest()})
            self.assertTrue(launcher.unchanged(root, result))
            (root / "a.txt").write_bytes(b"changed")
            self.assertFalse(launcher.unchanged(root, result))

    def test_symlinks_submodules_and_escaping_paths_are_rejected(self):
        for entry in (b"120000 blob a\tlink\0", b"160000 commit a\tsubmodule\0", b"100644 blob a\t../escape\0"):
            with patch.object(launcher.subprocess, "check_output", return_value=entry), self.assertRaises(launcher.QualificationError):
                launcher.manifest(Path("C:/repo"), "head")


class ProcessTests(unittest.TestCase):
    def phase(self, waits, free=100 * 2**30):
        process = Mock(pid=123)
        process.wait.side_effect = waits
        disk = Mock(free=free)
        with tempfile.TemporaryDirectory() as root, patch.object(launcher.subprocess, "Popen", return_value=process), patch.object(launcher.shutil, "disk_usage", return_value=disk):
            result = launcher.phase(["tool"], Path(root), {}, Path(root) / "log", 60)
        return result, process

    def test_exit_status_is_preserved(self):
        self.assertTrue(self.phase([0])[0]["passed"])
        result, _ = self.phase([2])
        self.assertFalse(result["passed"])
        self.assertEqual(result["exitCode"], 2)

    def test_low_space_interrupts_only_the_owned_process(self):
        result, process = self.phase([subprocess.TimeoutExpired("tool", 2), 130], free=1)
        self.assertFalse(result["passed"])
        self.assertTrue(result["settled"])
        self.assertEqual(result["reason"], "freeSpace")
        process.send_signal.assert_called_once()

    def test_interruption_is_not_success_even_if_child_returns_zero(self):
        result, _ = self.phase([KeyboardInterrupt(), 0])
        self.assertFalse(result["passed"])
        self.assertTrue(result["interrupted"])

    def test_unsettled_child_retains_its_exact_identity(self):
        result, _ = self.phase([KeyboardInterrupt(), subprocess.TimeoutExpired("tool", 120)])
        self.assertFalse(result["settled"])
        self.assertEqual(result["pid"], 123)


@unittest.skipUnless(os.name == "nt", "The lifecycle wrapper is Windows-only.")
class LifecycleTests(unittest.TestCase):
    def run_case(self, *, failure=None, keep=False, leftover=False, user_paths=None, empty=False):
        with tempfile.TemporaryDirectory() as root, ExitStack() as mocks:
            root = Path(root)
            repo, work_root, results_root = (root / name for name in ("repo", "work", "results"))
            for path in (repo, work_root, results_root):
                path.mkdir()
            (repo / "global.json").write_text('{"sdk":{"version":"10.0.112"}}')
            settings = replace(options(), repo=repo, work_root=work_root, results_root=results_root, keep_work=keep)
            calls = []

            def git(_repo, *args):
                calls.append(args)
                if args[0] == "rev-parse":
                    return "head"
                if args[:2] == ("worktree", "add"):
                    source = Path(args[3])
                    (source / "artifacts/w/bin").mkdir(parents=True)
                    (source / "artifacts/w/bin/built.dll").write_bytes(b"built")
                if args[:2] == ("worktree", "remove"):
                    shutil.rmtree(args[2])
                return ""

            def phase(_command, source, _environment, log, _seconds):
                name = log.stem
                if name == "test":
                    total = 0 if empty else 1
                    rows = '' if empty else '<UnitTestResult testName="test" outcome="Passed"/>'
                    (log.parent / "tests.trx").write_text(
                        f'<TestRun><Results>{rows}</Results><ResultSummary outcome="Completed">'
                        f'<Counters total="{total}" executed="{total}" passed="{total}" failed="0" notExecuted="0"/>'
                        '</ResultSummary></TestRun>')
                    if leftover:
                        (source.parent / "clusters/remaining").mkdir()
                return {"passed": name != failure, "settled": name != failure}

            mocks.enter_context(patch.object(launcher, "validate"))
            mocks.enter_context(patch.object(launcher, "git", side_effect=git))
            mocks.enter_context(patch.object(launcher, "manifest", return_value={}))
            mocks.enter_context(patch.object(launcher, "unchanged", return_value=True))
            mocks.enter_context(patch.object(launcher, "digest", return_value="sha256"))
            mocks.enter_context(patch.object(launcher, "phase", side_effect=phase))
            mocks.enter_context(patch.object(launcher, "user_path", side_effect=user_paths or ["before", "before"]))
            mocks.enter_context(patch.object(launcher.subprocess, "check_output", side_effect=["10.0.112"] + ["tool (PostgreSQL) 16.15"] * 4))
            mocks.enter_context(patch.object(launcher.shutil, "disk_usage", return_value=Mock(free=100 * 2**30)))
            code, results = launcher.qualify(settings)
            summary = json.loads((results / "summary.json").read_text())
            work_exists = Path(summary["work"]).exists()
            source_receipt = (results / "source.json").is_file()
            return code, summary, calls, work_exists, source_receipt

    def test_complete_success_removes_only_owned_work_and_keeps_receipts(self):
        code, summary, calls, work_exists, source_receipt = self.run_case()
        self.assertEqual(code, 0)
        self.assertEqual(summary["status"], "passed")
        self.assertFalse(work_exists)
        self.assertFalse(summary["retainedWork"])
        self.assertTrue(source_receipt)
        self.assertEqual(sum(args[:2] == ("worktree", "remove") for args in calls), 1)

    def test_failed_phase_never_removes_work_or_runs_next_phase(self):
        for name, phases in (("restore", ["restore"]), ("build", ["restore", "build"]),
                             ("test", ["restore", "build", "test"])):
            with self.subTest(name=name):
                code, summary, calls, work_exists, _ = self.run_case(failure=name)
                self.assertEqual(code, 1)
                self.assertTrue(work_exists)
                self.assertEqual(list(summary["phases"]), phases)
                self.assertFalse(any(args[:2] == ("worktree", "remove") for args in calls))

    def test_empty_selection_cannot_pass_or_remove_work(self):
        code, summary, _, work_exists, _ = self.run_case(empty=True)
        self.assertEqual(code, 1)
        self.assertFalse(summary["tests"]["passed"])
        self.assertTrue(work_exists)

    def test_keep_work_still_requires_settled_empty_clusters(self):
        for keep in (False, True):
            with self.subTest(keep=keep):
                code, summary, _, work_exists, _ = self.run_case(keep=keep, leftover=True)
                self.assertEqual(code, 1)
                self.assertEqual(summary["status"], "cleanupIncomplete")
                self.assertTrue(work_exists)
        code, summary, _, work_exists, _ = self.run_case(keep=True)
        self.assertEqual(code, 0)
        self.assertTrue(work_exists)
        self.assertTrue(summary["retainedWork"])

    def test_changed_or_unreadable_user_path_is_a_retained_failure(self):
        for after in ("changed", OSError("unavailable")):
            with self.subTest(after=after):
                code, summary, _, work_exists, _ = self.run_case(user_paths=["before", after])
                self.assertEqual(code, 1)
                self.assertFalse(summary["userPathUnchanged"])
                self.assertTrue(work_exists)


if __name__ == "__main__":
    unittest.main()
