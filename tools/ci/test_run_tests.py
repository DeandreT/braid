"""Small launcher checks, including actual POSIX descriptor isolation."""
import os
from contextlib import redirect_stderr
import io
import subprocess
import sys
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import run_tests


class LauncherTests(unittest.TestCase):
    def test_command_exit_and_explicit_global_tools_opt_out_are_preserved(self):
        with patch.dict(os.environ, {"DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "true", "WEFT_TEST_PIN": "retained"}):
            with patch.object(run_tests.subprocess, "run", return_value=subprocess.CompletedProcess(["fixture"], 7)) as launch:
                self.assertEqual(7, run_tests.run(["fixture", "argument"]))
            args, options = launch.call_args
            self.assertEqual((["fixture", "argument"],), args)
            self.assertTrue(options["close_fds"])
            self.assertFalse(options["check"])
            self.assertEqual("false", options["env"]["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"])
            self.assertEqual("retained", options["env"]["WEFT_TEST_PIN"])
            self.assertEqual("true", os.environ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"])

    def test_selection_and_diagnostic_logging_do_not_change_deadlines(self):
        with patch.object(run_tests, "run", return_value=0) as launch:
            self.assertEqual(0, run_tests.main([]))
        self.assertEqual([
            "dotnet", "test", "dotnet/Weft.slnx", "--configuration", "Release", "--no-build", "--no-restore",
            "--logger", "console;verbosity=normal",
        ], launch.call_args.args[0])

    @unittest.skipUnless(os.name == "posix", "POSIX descriptor inheritance")
    def test_an_unrelated_inheritable_descriptor_does_not_reach_the_child(self):
        descriptor = os.open(os.devnull, os.O_RDONLY)
        try:
            os.set_inheritable(descriptor, True)
            child = (
                "import os,sys\n"
                f"try: os.fstat({descriptor})\n"
                "except OSError: sys.exit(0)\n"
                "sys.exit(1)\n"
            )
            self.assertEqual(0, run_tests.run([sys.executable, "-c", child]))
            os.fstat(descriptor)
        finally:
            os.close(descriptor)


class ShardTests(unittest.TestCase):
    METHODS = [f"Weft.Tests.Class{i}.Method{j}" for i in range(40) for j in range(3)]

    def test_partition_is_exhaustive_disjoint_stable_and_keeps_each_class_together(self):
        shards = run_tests.partition(self.METHODS, 4)
        self.assertEqual(shards, run_tests.partition([*reversed(self.METHODS), *self.METHODS], 4))
        self.assertEqual(sorted(self.METHODS), sorted(method for shard in shards for method in shard))
        classes = {}
        for index, shard in enumerate(shards):
            self.assertTrue(shard)
            for method in shard:
                name = method.rsplit(".", 1)[0]
                self.assertEqual(index, classes.setdefault(name, index))

    def test_invalid_empty_or_unsafe_selections_fail_closed(self):
        for methods, count in ((self.METHODS, 0), ([], 4), (["Weft.Tests.One.Test"], 4),
                               (["Weft.Tests.One.Test|FullyQualifiedName~Other"], 1)):
            with self.assertRaises(ValueError):
                run_tests.partition(methods, count)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tests.txt"
            for value in ("", "Weft.Tests.One.Test\n\n", "Warning: no tests"):
                path.write_text(value)
                with self.assertRaises(ValueError):
                    run_tests.read_methods(path)
            path.write_text("\ufeffWeft.Tests.One.Test\nWeft.Tests.One.Test\n", encoding="utf-8")
            self.assertEqual(["Weft.Tests.One.Test"], run_tests.read_methods(path))

    def test_settings_use_exact_fqns_so_theory_rows_and_overloads_stay_selected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.runsettings"
            methods = ["Weft.Tests.Outer+Nested.Test", "Weft.Tests.Other.Theory"]
            run_tests.write_settings(path, methods)
            self.assertEqual("|".join("FullyQualifiedName=" + name for name in methods),
                             ET.parse(path).findtext("./RunConfiguration/TestCaseFilter"))

    def test_shard_checks_real_discovery_filter_before_running_the_solution(self):
        expected = run_tests.partition(self.METHODS, 4)[2]

        def discover(assembly, target, settings=None):
            self.assertEqual(Path("fixture.dll"), assembly)
            if settings is not None:
                actual = ET.parse(settings).findtext("./RunConfiguration/TestCaseFilter")
                self.assertEqual("|".join("FullyQualifiedName=" + name for name in expected), actual)
            target.write_text("\n".join(self.METHODS if settings is None else expected))
            return 0

        with patch.object(run_tests, "discover", side_effect=discover), patch.object(run_tests, "run", return_value=7) as launch:
            self.assertEqual(7, run_tests.run_shard(Path("fixture.dll"), 2, 4))
            command = launch.call_args.args[0]
            self.assertEqual(run_tests.test_command(), command[:-2])
            self.assertEqual("--settings", command[-2])
            self.assertFalse(Path(command[-1]).exists(), "Temporary selection is removed only after the child exits")

    def test_discovery_failure_never_runs_a_test_selection(self):
        with patch.object(run_tests, "discover", return_value=9), patch.object(run_tests, "run") as launch:
            self.assertEqual(9, run_tests.run_shard(Path("fixture.dll"), 0, 4))
            launch.assert_not_called()

    def test_filtered_discovery_failure_never_runs_tests(self):
        def discover(assembly, target, settings=None):
            if settings is not None:
                return 8
            target.write_text("\n".join(self.METHODS))
            return 0

        with patch.object(run_tests, "discover", side_effect=discover), patch.object(run_tests, "run") as launch:
            self.assertEqual(8, run_tests.run_shard(Path("fixture.dll"), 0, 4))
            launch.assert_not_called()

    def test_inexact_filtered_discovery_is_not_accepted(self):
        def discover(assembly, target, settings=None):
            target.write_text("\n".join(self.METHODS))
            return 0

        with patch.object(run_tests, "discover", side_effect=discover), patch.object(run_tests, "run") as launch:
            with self.assertRaisesRegex(ValueError, "exactly preserve"):
                run_tests.run_shard(Path("fixture.dll"), 0, 4)
            launch.assert_not_called()

    def test_shard_arguments_must_be_paired_and_in_range(self):
        for args in (["--shard-index", "0"], ["--shard-count", "4"],
                     ["--shard-index", "-1", "--shard-count", "4"],
                     ["--shard-index", "4", "--shard-count", "4"],
                     ["--shard-index", "0", "--shard-count", "0"]):
            with self.assertRaises(SystemExit), patch.object(run_tests, "run") as launch, redirect_stderr(io.StringIO()):
                run_tests.main(args)
            launch.assert_not_called()

    def test_shard_cli_uses_the_existing_solution_and_pinned_assembly_layout(self):
        with patch.object(run_tests, "run_shard", return_value=6) as launch:
            self.assertEqual(6, run_tests.main(["--shard-index", "1", "--shard-count", "4"]))
            launch.assert_called_once_with(run_tests.TEST_ASSEMBLY, 1, 4)

    def test_current_solution_has_exactly_the_discovered_test_project(self):
        run_tests.verify_solution_projects()

    def test_solution_growth_cannot_silently_omit_a_test_project(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "one.csproj").write_text("<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>")
            (root / "other.csproj").write_text('<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>')
            solution = root / "test.slnx"
            solution.write_text('<Solution><Project Path="one.csproj" /><Project Path="other.csproj" /></Solution>')
            with patch.object(run_tests, "SOLUTION", solution), self.assertRaisesRegex(ValueError, "every solution test project"):
                run_tests.verify_solution_projects()


if __name__ == "__main__":
    unittest.main()
