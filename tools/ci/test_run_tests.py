"""Small launcher checks, including actual POSIX descriptor isolation."""
import os
from contextlib import redirect_stderr, redirect_stdout
import io
import subprocess
import sys
from pathlib import Path
import tempfile
import re
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

import run_tests
import test_groups


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

    def test_discovery_uses_filterable_fqns_and_preserves_paths_settings_and_exit_status(self):
        with patch.object(run_tests, "run", return_value=12) as launch:
            self.assertEqual(12, run_tests.discover(Path("path with spaces/tests.dll"), Path("output names.txt")))
            self.assertEqual(["dotnet", "vstest", "path with spaces/tests.dll", "/ListFullyQualifiedTests",
                              "/ListTestsTargetPath:output names.txt"], launch.call_args.args[0])
            self.assertEqual(12, run_tests.discover(Path("tests.dll"), Path("output.txt"), Path("exact settings.xml")))
            self.assertEqual("/Settings:exact settings.xml", launch.call_args.args[0][-1])

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


class GroupTests(unittest.TestCase):
    GROUPS = {"fast": ("Weft.Tests.Pure", "Weft.Tests.Outer+Nested"),
              "integration": ("Weft.Tests.Service",)}
    METHODS = ["Weft.Tests.Pure.Theory", "Weft.Tests.Pure.Fact", "Weft.Tests.Outer+Nested.Fact",
               "Weft.Tests.Service.Theory", "Weft.Tests.Service.Fact"]

    def test_actual_fqn_classification_is_exhaustive_disjoint_stable_and_deduplicates_theories(self):
        groups = run_tests.classify(self.METHODS, self.GROUPS)
        self.assertEqual(groups, run_tests.classify([*reversed(self.METHODS), *self.METHODS], self.GROUPS))
        self.assertEqual(sorted(self.METHODS), sorted(method for methods in groups.values() for method in methods))
        self.assertTrue(set(groups["fast"]).isdisjoint(groups["integration"]))
        self.assertIn("Weft.Tests.Pure.Theory", groups["fast"])
        self.assertIn("Weft.Tests.Outer+Nested.Fact", groups["fast"])

    def test_unknown_classes_stale_classes_empty_and_overlapping_rosters_fail_closed(self):
        cases = [([], self.GROUPS), (self.METHODS + ["Weft.Tests.New.Test"], self.GROUPS),
                 (self.METHODS, {**self.GROUPS, "empty": ()}),
                 (self.METHODS, {**self.GROUPS, "overlap": ("Weft.Tests.Pure",)}),
                 (self.METHODS, {**self.GROUPS, "stale": ("Weft.Tests.Removed",)}),
                 (self.METHODS, {**self.GROUPS, "unsafe": ("Weft.Tests.Foo|Other",)}),
                 (self.METHODS + ["Weft.Tests.Pure.Theory(value: 1)"], self.GROUPS)]
        for methods, groups in cases:
            with self.subTest(methods=methods, groups=groups), self.assertRaises(ValueError):
                run_tests.classify(methods, groups)

    def fake_discovery(self, selected=None, failure=0):
        def discover(assembly, target, settings=None):
            self.assertEqual(Path("fixture.dll"), assembly)
            if settings is None:
                target.write_text("\n".join([*self.METHODS, self.METHODS[0]]))
            else:
                if failure:
                    return failure
                names = sorted(method for group in (selected or ["fast"])
                               for method in run_tests.classify(self.METHODS, self.GROUPS)[group])
                self.assertEqual("|".join("FullyQualifiedName=" + name for name in names),
                                 ET.parse(settings).findtext("./RunConfiguration/TestCaseFilter"))
                target.write_text("\n".join(names))
            return 0
        return discover

    def group_patches(self):
        from contextlib import ExitStack
        stack = ExitStack()
        stack.enter_context(patch.object(test_groups, "class_groups", return_value=self.GROUPS))
        stack.enter_context(patch.object(test_groups, "verify_sources"))
        stack.enter_context(patch.object(run_tests, "require_prerequisites"))
        return stack

    def test_group_runner_checks_exact_filtered_fqns_prints_omissions_and_preserves_command_exit(self):
        output = io.StringIO()
        with self.group_patches(), redirect_stdout(output), \
                patch.object(run_tests, "discover", side_effect=self.fake_discovery()), \
                patch.object(run_tests, "run", return_value=13) as launch:
            self.assertEqual(13, run_tests.run_groups(Path("fixture.dll"), ["fast"]))
            command = launch.call_args.args[0]
            self.assertEqual(run_tests.test_command(), command[:-2])
            self.assertEqual("--settings", command[-2])
            self.assertFalse(Path(command[-1]).exists())
        self.assertIn("fast: selected; 3 discovered method identities", output.getvalue())
        self.assertIn("integration: OMITTED; 2 discovered method identities", output.getvalue())
        self.assertIn("not full CI", output.getvalue())

    def test_repeated_explicit_groups_select_the_union_only_once(self):
        with self.group_patches(), redirect_stdout(io.StringIO()), \
                patch.object(run_tests, "discover", side_effect=self.fake_discovery(["fast", "integration"])), \
                patch.object(run_tests, "run", return_value=0):
            self.assertEqual(0, run_tests.run_groups(Path("fixture.dll"), ["fast", "integration", "fast"]))

    def test_failed_or_inexact_discovery_never_executes_tests(self):
        with self.group_patches(), patch.object(run_tests, "run") as launch:
            with patch.object(run_tests, "discover", return_value=9):
                self.assertEqual(9, run_tests.run_groups(Path("fixture.dll"), ["fast"]))
            with redirect_stdout(io.StringIO()), \
                    patch.object(run_tests, "discover", side_effect=self.fake_discovery(failure=8)):
                self.assertEqual(8, run_tests.run_groups(Path("fixture.dll"), ["fast"]))

            def inexact(assembly, target, settings=None):
                target.write_text("\n".join(self.METHODS))
                return 0

            with redirect_stdout(io.StringIO()), patch.object(run_tests, "discover", side_effect=inexact), \
                    self.assertRaisesRegex(ValueError, "exactly preserve"):
                run_tests.run_groups(Path("fixture.dll"), ["fast"])
            launch.assert_not_called()

    def test_source_or_prerequisite_refusal_precedes_any_discovery(self):
        for rejected in ("verify_sources", "require_prerequisites"):
            with self.group_patches(), patch.object(run_tests, "discover") as discover, \
                    patch.object(test_groups if rejected == "verify_sources" else run_tests,
                                 rejected, side_effect=ValueError("refused")), \
                    self.assertRaisesRegex(ValueError, "refused"):
                run_tests.run_groups(Path("fixture.dll"), ["fast"])
            discover.assert_not_called()

    def test_group_cli_preserves_arguments_handles_refusals_and_does_not_join_ci(self):
        with patch.object(run_tests, "run_groups", return_value=6) as launch:
            self.assertEqual(6, run_tests.main(["--group", "fast", "--group", "integration"]))
            launch.assert_called_once_with(run_tests.TEST_ASSEMBLY, ["fast", "integration"])
        with patch.object(run_tests, "run_groups", side_effect=ValueError("missing fixture")), \
                redirect_stderr(io.StringIO()) as error:
            self.assertEqual(2, run_tests.main(["--group", "fast"]))
        self.assertIn("missing fixture", error.getvalue())
        for args in (["--group", "fast", "--shard-index", "0"],
                     ["--group", "fast", "--shard-count", "4"], ["--group", "unknown"]):
            with self.assertRaises(SystemExit), patch.object(run_tests, "run") as launch, redirect_stderr(io.StringIO()):
                run_tests.main(args)
            launch.assert_not_called()

    def test_default_and_ci_do_not_apply_local_groups_or_prerequisite_rules(self):
        with patch.object(test_groups, "verify_sources", side_effect=AssertionError("local audit")), \
                patch.object(run_tests, "require_prerequisites", side_effect=AssertionError("local prerequisites")), \
                patch.object(run_tests, "run", return_value=4), patch.object(run_tests, "run_shard", return_value=5):
            self.assertEqual(4, run_tests.main([]))
            self.assertEqual(5, run_tests.main(["--shard-index", "1", "--shard-count", "4"]))

    def test_current_source_roster_matches_census_without_claiming_runtime_discovery(self):
        # Bounded to this audited source style, NOT a C# discovery implementation.
        # Real execution always proves the roster against VSTest's discovered FQNs.
        classes = set()
        for path in Path("dotnet/tests/Weft.Tests").rglob("*.cs"):
            if {"bin", "obj"}.intersection(path.parts):
                continue
            source = path.read_text()
            namespace = re.search(r"^namespace ([\w.]+);", source, re.M).group(1)
            owner = None
            for line in source.splitlines():
                declaration = re.match(r"public (?:sealed |abstract |static |partial )*class (\w+)", line)
                if declaration:
                    owner = namespace + "." + declaration[1]
                if re.match(r"\s*\[(?:\w+\.)*\w*(?:Fact|Theory)\b", line):
                    self.assertIsNotNone(owner)
                    classes.add(owner)
        groups = test_groups.class_groups()
        names = [name for roster in groups.values() for name in roster]
        self.assertEqual(len(names), len(set(names)))
        self.assertEqual(classes, set(names))
        self.assertEqual({"Weft.Tests.AccountDisplayNameTests", "Weft.Tests.ExternalIdentityKeyTests",
                          "Weft.Tests.MaintenanceServiceTestTimeProviderTests", "Weft.Tests.ProjectPayloadPermissionContractTests"},
                         set(groups["fast"]))
        self.assertIn("Weft.Tests.AiProviderTests", groups["live-provider"], "all partials stay together")
        self.assertIn("Weft.Tests.AuthBrowserHttpsQualificationTests", groups["packaged-browser"])
        self.assertNotIn("cloud", groups, "Azure harness tests use fake tools, not live cloud")
        # Exercise the real roster with fixture identities; this is still mocked
        # input, not evidence that .NET discovered or executed these methods.
        fixtures = [name + ".FixtureMethod" for name in names]
        self.assertEqual(sorted(fixtures), sorted(method for methods in run_tests.classify(fixtures, groups).values()
                                                  for method in methods))

    def test_stale_local_source_audit_does_not_gate_full_suite_or_ci_shards(self):
        with patch.object(test_groups, "AUDITED_SOURCE_SHA256", "stale"), \
                patch.object(run_tests, "run", return_value=0), patch.object(run_tests, "run_shard", return_value=0):
            with self.assertRaisesRegex(ValueError, "stale"):
                test_groups.verify_sources(Path.cwd())
            self.assertEqual(0, run_tests.main([]))
            self.assertEqual(0, run_tests.main(["--shard-index", "0", "--shard-count", "4"]))

    def test_source_binding_detects_changes_to_helpers_production_linked_sources_and_build_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "dotnet/tests/Weft.Tests/Weft.Tests.csproj"
            project.parent.mkdir(parents=True)
            linked = root / "dotnet/tools/Probe/Wire.cs"
            linked.parent.mkdir(parents=True)
            linked.write_text("linked probe")
            project.write_text('<Project><ItemGroup><Compile Include="../../tools/Probe/Wire.cs" /></ItemGroup></Project>')
            (root / "global.json").write_text("pin")
            helper = project.parent / "Helper.cs"
            helper.write_text("initializer")
            production = root / "dotnet/src/Production.cs"
            production.parent.mkdir()
            production.write_text("constructor")
            fingerprint = test_groups.source_fingerprint(root)
            with patch.object(test_groups, "AUDITED_SOURCE_SHA256", fingerprint):
                test_groups.verify_sources(root)
                for path in (helper, production, linked, project, root / "global.json"):
                    original = path.read_text()
                    path.write_text(original + "\n")
                    with self.assertRaises((ValueError, ET.ParseError)):
                        test_groups.verify_sources(root)
                    path.write_text(original)
                new = project.parent / "New.cs"
                new.write_text("new test class")
                with self.assertRaisesRegex(ValueError, "stale"):
                    test_groups.verify_sources(root)
                new.unlink()
                helper.unlink()
                with self.assertRaisesRegex(ValueError, "stale"):
                    test_groups.verify_sources(root)


class PrerequisiteTests(unittest.TestCase):
    def test_fast_needs_only_the_built_assembly_and_dotnet_without_setting_or_scrubbing_env(self):
        with patch.dict(os.environ, {"WEFT_LIVE_OLLAMA_MODEL": "unchanged"}, clear=True), \
                patch.object(Path, "is_file", return_value=True), \
                patch.object(run_tests.shutil, "which", return_value="fixture") as which:
            before = os.environ.copy()
            run_tests.require_prerequisites(["fast"], Path("fixture.dll"))
            which.assert_called_once_with("dotnet")
            self.assertEqual(before, os.environ)

    def test_missing_common_prerequisites_are_explicit(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(Path, "is_file", return_value=False), \
                patch.object(run_tests.shutil, "which", return_value=None), \
                self.assertRaisesRegex(ValueError, "missing test assembly") as error:
            run_tests.require_prerequisites(["fast"], Path("missing.dll"))
        self.assertIn("dotnet", str(error.exception))
        self.assertIn("Contracts/browser-return-paths.json", str(error.exception))

    def test_each_expensive_group_refuses_its_missing_configuration_without_launching(self):
        expected = {"integration": "WEFT_FERRULE_HOST_EXECUTABLE", "packaged-browser": "WEFT_AUTH_BROWSER_ADMISSION_FILE",
                    "installed-documents": "WEFT_SPEC_DOCUMENT_WORKERS", "installed-document-probes": "WEFT_TEST_DOCUMENT_PROFILE_PROBE_ASSEMBLY",
                    "live-provider": "WEFT_LIVE_OLLAMA_ENDPOINT", "native-windows": "WEFT_POSTGRES_BIN",
                    "incremental-build": "WEFT_OPENAPI_INCREMENTAL_TESTS"}
        for group, message in expected.items():
            with self.subTest(group=group), patch.dict(os.environ, {}, clear=True), \
                    patch.object(Path, "is_file", return_value=True), patch.object(Path, "is_dir", return_value=True), \
                    patch.object(run_tests.shutil, "which", return_value="fixture"), \
                    patch.object(run_tests, "run") as run, self.assertRaisesRegex(ValueError, message):
                run_tests.require_prerequisites([group], Path("fixture.dll"))
            run.assert_not_called()

    def test_live_provider_and_incremental_build_admission_do_not_create_configuration(self):
        for group, environment in (("live-provider", {"WEFT_LIVE_OLLAMA_ENDPOINT": "http://127.0.0.1:1/api/chat", "WEFT_LIVE_OLLAMA_MODEL": "model"}),
                                   ("incremental-build", {"WEFT_OPENAPI_INCREMENTAL_TESTS": "1"})):
            with patch.dict(os.environ, environment, clear=True), patch.object(Path, "is_file", return_value=True), \
                    patch.object(run_tests.shutil, "which", return_value="fixture"):
                run_tests.require_prerequisites([group], Path("fixture.dll"))
                self.assertEqual(environment, os.environ)

    def test_integration_requires_canonical_fixtures_matching_worker_images_and_executable_files(self):
        environment = {
            "WEFT_POSTGRES_IMAGE": "wrong", "WEFT_POSTGRES_BIN": "/native", "WEFT_AZURITE_IMAGE": "wrong",
            "WEFT_AZURITE_LOOPBACK_ENDPOINT": "http://127.0.0.1:1", "WEFT_BUILD_WORKER_IMAGE": "one",
            "WEFT_FERRULE_WORKER_IMAGE": "two", "WEFT_AUTOMATION_DOCKER_TESTS": "false",
            "WEFT_FERRULE_HOST_EXECUTABLE": "relative", "DOTNET_ROOT": "/missing",
        }
        with patch.dict(os.environ, environment, clear=True), patch.object(Path, "is_file", return_value=False), \
                patch.object(Path, "is_dir", return_value=False), patch.object(run_tests.shutil, "which", return_value=None), \
                patch.object(run_tests.sys, "platform", "win32"), self.assertRaises(ValueError) as error:
            run_tests.require_prerequisites(["integration"], Path("fixture.dll"))
        for message in ("require Linux", "WEFT_POSTGRES_IMAGE", "native/client-container", "WEFT_AZURITE_IMAGE",
                        "LOOPBACK_ENDPOINT", "same combined worker", "AUTOMATION_DOCKER_TESTS=true", "existing absolute executable",
                        "10.0.112 compiler", "10.0.12 reference pack"):
            self.assertIn(message, str(error.exception))

    def test_valid_prerequisite_profiles_are_admitted_without_any_process_or_environment_changes(self):
        postgres = "docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc"
        integration = {
            "WEFT_POSTGRES_IMAGE": postgres,
            "WEFT_AZURITE_IMAGE": "mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5",
            "WEFT_FERRULE_WORKER_IMAGE": "same-build", "WEFT_BUILD_WORKER_IMAGE": "same-build",
            "WEFT_COMPILED_RUNTIME_IMAGE": "runtime", "WEFT_FERRULE_SERVICE_IMAGE": "service",
            "WEFT_AUTOMATION_DOCKER_TESTS": "true", "DOTNET_ROOT": "/sdk",
        }
        for name in ("WEFT_FERRULE_HOST_EXECUTABLE", "WEFT_COMPILE_SANDBOX_EXECUTABLE", "WEFT_TEST_PDF_EXECUTABLE",
                     "WEFT_AUTHORING_TEST_EXECUTABLE", "WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE", "WEFT_EDIT_TEST_EXECUTABLE",
                     "WEFT_HOST_TEST_SERVER_PATH", "WEFT_HOST_PRIVACY_PROBE_PATH"):
            integration[name] = "/fixture/" + name
        installed = {"WEFT_TEST_DOCUMENT_HOST_LINUX": "1", "WEFT_SPEC_DOCUMENT_WORKERS": "optional",
                     "WEFT_TEST_PDF_EXECUTABLE": "/fixture/pdf", "WEFT_TEST_DOCUMENT_PID_NAMESPACE_RECORD": "/fixture/record",
                     "WEFT_TEST_DOCUMENT_PID_NAMESPACE_RECORD_SHA256": "a" * 64}
        for suffix in ("OFFICE_ASSEMBLY", "OFFICE_LAUNCHER", "PDF_EXECUTABLE", "DOTNET_ROOT", "OFFICE_WORK_ROOT", "PDF_WORK_ROOT"):
            installed["WEFT_SPEC_DOCUMENT_" + suffix] = "/fixture/" + suffix
        for suffix in ("OFFICE_PROFILE_SHA256", "OFFICE_BUILD_SHA256", "OFFICE_RUNTIME_SHA256", "PDF_BUILD_SHA256", "PDF_PROFILE_SHA256"):
            installed["WEFT_SPEC_DOCUMENT_" + suffix] = "a" * 64
        probes = dict(installed)
        for prefix in ("WEFT_TEST_DOCUMENT_PROFILE_PROBE_", "WEFT_TEST_DOCUMENT_RESOURCE_PROBE_"):
            probes[prefix + "ASSEMBLY"] = "/fixture/probe.dll"
            probes[prefix + "BUILD_SHA256"] = "a" * 64
            probes[prefix + "RUNTIME_SHA256"] = "b" * 64
        profiles = {
            "integration": integration,
            "packaged-browser": {"WEFT_POSTGRES_IMAGE": postgres, "WEFT_AUTH_BROWSER_ADMISSION_ROOT": "/fixture/root",
                                 "WEFT_AUTH_BROWSER_ADMISSION_FILE": "/fixture/record", "WEFT_WORKSPACE_CHAT_BROWSER": "true", "DISPLAY": ":999"},
            "installed-documents": installed, "installed-document-probes": probes,
            "native-windows": {"WEFT_POSTGRES_BIN": "/fixture/postgres"},
        }
        for group, environment in profiles.items():
            with self.subTest(group=group), patch.dict(os.environ, environment, clear=True), \
                    patch.object(Path, "is_file", return_value=True), patch.object(Path, "is_dir", return_value=True), \
                    patch.object(run_tests.shutil, "which", return_value="fixture"), patch.object(os, "access", return_value=True), \
                    patch.object(run_tests.sys, "platform", "win32" if group == "native-windows" else "linux"), \
                    patch.object(run_tests.subprocess, "run") as launch:
                run_tests.require_prerequisites([group], Path("fixture.dll"))
                self.assertEqual(environment, os.environ)
                launch.assert_not_called()

    def test_relative_or_nonexecutable_paths_and_missing_protected_context_are_not_accepted(self):
        environment = {"WEFT_TEST_DOCUMENT_HOST_LINUX": "1", "WEFT_SPEC_DOCUMENT_WORKERS": "optional",
                       "WEFT_SPEC_DOCUMENT_OFFICE_ASSEMBLY": "relative.dll",
                       "WEFT_SPEC_DOCUMENT_OFFICE_LAUNCHER": "/fixture/nonexecutable",
                       "WEFT_SPEC_DOCUMENT_OFFICE_BUILD_SHA256": "A" * 64}
        with patch.dict(os.environ, environment, clear=True), patch.object(Path, "is_file", return_value=True), \
                patch.object(Path, "is_dir", return_value=True), patch.object(os, "access", return_value=False), \
                patch.object(run_tests.shutil, "which", return_value="fixture"), \
                patch.object(run_tests.sys, "platform", "linux"), self.assertRaises(ValueError) as error:
            run_tests.require_prerequisites(["installed-document-probes"], Path("fixture.dll"))
        for message in ("OFFICE_ASSEMBLY must name an existing absolute file", "OFFICE_LAUNCHER must name an existing absolute executable",
                        "OFFICE_BUILD_SHA256 must be a canonical", "PID_NAMESPACE_RECORD", "RESOURCE_PROBE_ASSEMBLY"):
            self.assertIn(message, str(error.exception))
        self.assertNotIn("A" * 64, str(error.exception), "do not echo configured values")


if __name__ == "__main__":
    unittest.main()
