"""Launch tests use mocks or a Python executable fixture, never Xvfb or npm."""

import contextlib
import io
import json
import os
from pathlib import Path
import resource
import selectors
import shlex
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import launch


ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = Path(launch.__file__).resolve()


class LaunchTests(unittest.TestCase):
    def test_default_arguments_and_literal_forwarding(self):
        arguments = ["--config", "tests/example config.ts", "--grep", "$(touch nope); *"]
        self.assertEqual(launch.launch_arguments(arguments, {}), [
            "xvfb-run", "-a", "-w", "10", "npm", "--workspace", "@braid/web",
            "run", "test:e2e", "--", *arguments,
        ])
        self.assertEqual(launch.launch_arguments([], {})[-1], "test:e2e")

    def test_auth_display_override_and_default(self):
        for value in ("0", "91", "00102"):
            with self.subTest(value=value):
                self.assertEqual(launch.launch_arguments([], {
                    "BRAID_XVFB_SERVER_NUM": value,
                }, True)[1], "--server-num=" + value)
        for environment in ({}, {"BRAID_XVFB_SERVER_NUM": ""}):
            self.assertEqual(launch.launch_arguments([], environment, True)[1], "-a")
        self.assertEqual(launch.launch_arguments([], {
            "BRAID_XVFB_SERVER_NUM": "123",
        })[1], "-a", "other aliases must keep automatic display selection")

    def test_invalid_display_is_rejected_without_launching_or_changing_limits(self):
        for value in ("-1", ":91", "1.0", "1 2", " 91", "91\n", "١", "91;false", "--auto-servernum"):
            with self.subTest(value=value), patch.dict(os.environ, {
                "BRAID_XVFB_SERVER_NUM": value,
            }), patch.object(launch.os, "execvpe") as execute, patch.object(
                launch.resource, "setrlimit",
            ) as limits, contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as exit:
                    launch.main(["--display-from-env", "--no-core-dumps", "--"])
                self.assertEqual(exit.exception.code, 2)
                execute.assert_not_called()
                limits.assert_not_called()

    def test_environment_is_copied_and_only_desktop_variables_are_removed(self):
        original = {
            **{name: "user-desktop" for name in launch.DESKTOP_VARIABLES},
            "PATH": "/tools", "DISPLAY": ":17", "BRAID_TEST": "kept", "HOME": "/home/test",
        }
        with patch.dict(os.environ, original, clear=True), patch.object(
            launch.os, "execvpe",
        ) as execute, patch.object(launch.resource, "setrlimit") as limits:
            launch.main(["--", "--config", "literal.ts"])
            self.assertEqual(dict(os.environ), original)
            self.assertEqual(execute.call_args.args[2], {
                key: value for key, value in original.items()
                if key not in launch.DESKTOP_VARIABLES
            })
            self.assertEqual(execute.call_args.args[:2], (
                "xvfb-run", launch.launch_arguments(["--config", "literal.ts"], original),
            ))
            self.assertEqual(execute.call_count, 1)
            limits.assert_not_called()

    def test_auth_disables_both_core_limits_before_exec(self):
        calls = []
        with patch.object(launch.resource, "setrlimit", side_effect=lambda *args: calls.append(args)), patch.object(
            launch.os, "execvpe", side_effect=lambda *args: calls.append("exec"),
        ):
            launch.main(["--no-core-dumps", "--"])
        self.assertEqual(calls, [(resource.RLIMIT_CORE, (0, 0)), "exec"])

    def test_core_limit_failure_refuses_launch(self):
        for error in (OSError("denied"), ValueError("unsupported")):
            with self.subTest(error=error), patch.object(
                launch.resource, "setrlimit", side_effect=error,
            ), patch.object(launch.os, "execvpe") as execute, contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(launch.main(["--no-core-dumps", "--"]), 1)
                execute.assert_not_called()

    def test_launch_failures_keep_shell_exit_codes(self):
        for error, expected in ((FileNotFoundError(), 127), (PermissionError(), 126), (OSError(), 126)):
            with self.subTest(error=error), patch.object(
                launch.os, "execvpe", side_effect=error,
            ), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(launch.main(["--"]), expected)

    def test_package_aliases_keep_their_scenarios(self):
        scripts = json.loads((ROOT / "package.json").read_text())["scripts"]
        scenarios = {
            "test:browser": None,
            "test:browser-session": "playwright.browser-session.config.ts",
            "test:auth-https-browser": "playwright.auth-https.config.ts",
            "test:catalog-browser": "playwright.catalog.config.ts",
            "test:reports-browser": "playwright.reports.config.ts",
            "test:mapping-viewer-browser": "tests/mapping-viewer.config.ts",
            "test:project-creation-browser": "playwright.project-creation.config.ts",
            "test:mapping-metadata-browser": "playwright.mapping-metadata.config.ts",
            "test:native-mapping-upload-browser": "playwright.native-mapping-upload.config.ts",
            "test:native-revision-append-browser": "playwright.native-revision-append.config.ts",
        }
        for alias, config in scenarios.items():
            expected = ["python3", "tools/browser-fixture/launch.py"]
            if alias == "test:auth-https-browser":
                expected += ["--no-core-dumps", "--display-from-env"]
            expected += ["--"]
            if config:
                expected += ["--config", config]
            with self.subTest(alias=alias):
                self.assertEqual(shlex.split(scripts[alias]), expected)
        for alias in ("test:spec-intake-browser", "test:auth-https-source", "test:sandbox-browser"):
            self.assertNotIn("launch.py", scripts[alias])


class ExecFixtureTests(unittest.TestCase):
    def setUp(self):
        # Respect the caller's TMPDIR; no graphical/server executable is used.
        self.directory = tempfile.TemporaryDirectory(prefix="braid-launch-test-")
        self.addCleanup(self.directory.cleanup)
        fixture = Path(self.directory.name) / "xvfb-run"
        fixture.write_text("#!" + sys.executable + "\n" + '''
import json, os, resource, signal, sys

def stop(number, frame):
    os.write(sys.stdout.fileno(), b"fixture cleanup\\n")
    signal.signal(number, signal.SIG_DFL)
    os.kill(os.getpid(), number)

for number in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
    signal.signal(number, stop)
print(json.dumps({"pid": os.getpid(), "arguments": sys.argv[1:],
                  "environment": dict(os.environ),
                  "core": resource.getrlimit(resource.RLIMIT_CORE),
                  "cwd": os.getcwd()}), flush=True)
if os.environ.get("FIXTURE_WAIT"):
    signal.pause()
else:
    sys.exit(int(os.environ.get("FIXTURE_EXIT", "0")))
''')
        fixture.chmod(0o700)
        self.environment = {
            **os.environ, "PATH": self.directory.name,
            **{name: "live-desktop" for name in launch.DESKTOP_VARIABLES},
            "FIXTURE_WAIT": "", "FIXTURE_EXIT": "0",
        }

    def test_exec_preserves_arguments_environment_directory_and_exit_codes(self):
        for code in (0, 3, 19, 126, 127):
            with self.subTest(code=code):
                result = subprocess.run(
                    [sys.executable, str(LAUNCHER), "--", "--grep", "literal $(nope); *"],
                    env={**self.environment, "FIXTURE_EXIT": str(code)},
                    capture_output=True, text=True, timeout=10,
                )
                self.assertEqual(result.returncode, code, result.stderr)
                data = json.loads(result.stdout)
                self.assertEqual(data["arguments"], launch.launch_arguments(
                    ["--grep", "literal $(nope); *"], {},
                )[1:])
                self.assertEqual(data["cwd"], os.getcwd())
                for name in launch.DESKTOP_VARIABLES:
                    self.assertNotIn(name, data["environment"])

    def test_auth_core_suppression_and_explicit_display_reach_exec_target(self):
        before = resource.getrlimit(resource.RLIMIT_CORE)
        result = subprocess.run(
            [sys.executable, str(LAUNCHER), "--display-from-env", "--no-core-dumps", "--"],
            env={**self.environment, "BRAID_XVFB_SERVER_NUM": "109"},
            capture_output=True, text=True, timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(result.stdout)
        self.assertEqual(data["core"], [0, 0])
        self.assertEqual(data["arguments"][0], "--server-num=109")
        self.assertEqual(resource.getrlimit(resource.RLIMIT_CORE), before)

    def test_signals_reach_same_process_and_leave_cleanup_with_exec_target(self):
        for number in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            with self.subTest(signal=number):
                child = subprocess.Popen(
                    [sys.executable, str(LAUNCHER), "--"],
                    env={**self.environment, "FIXTURE_WAIT": "1"},
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                )
                try:
                    with selectors.DefaultSelector() as ready:
                        ready.register(child.stdout, selectors.EVENT_READ)
                        self.assertTrue(ready.select(timeout=10), "fixture did not start")
                    data = json.loads(child.stdout.readline())
                    self.assertEqual(data["pid"], child.pid, "exec must not create a supervisor")
                    child.send_signal(number)
                    stdout, stderr = child.communicate(timeout=10)
                    self.assertEqual(child.returncode, -number, stderr)
                    self.assertEqual(stdout, "fixture cleanup\n")
                finally:
                    if child.poll() is None:
                        child.kill()
                    child.communicate(timeout=10)


if __name__ == "__main__":
    unittest.main()
