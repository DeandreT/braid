"""Run the full solution, CI shards, or explicit audited local test groups."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from collections.abc import Sequence
import xml.etree.ElementTree as ET

import test_groups


SOLUTION = Path("dotnet/Braid.slnx")
TEST_PROJECT = "tests/Braid.Tests/Braid.Tests.csproj"
TEST_ASSEMBLY = Path("dotnet/tests/Braid.Tests/bin/Release/net10.0/Braid.Tests.dll")
METHOD_NAME = re.compile(r"[A-Za-z_][A-Za-z_0-9]*(?:[.+][A-Za-z_][A-Za-z_0-9]*)+")


def run(command: Sequence[str]) -> int:
    environment = os.environ.copy()
    environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false"
    # .NET forwards unrelated inheritable descriptors to Process.Start children.
    # Start its test host with only standard streams, not the CI runner's handles.
    return subprocess.run(command, env=environment, close_fds=True, check=False).returncode


def test_command() -> list[str]:
    return [
        "dotnet", "test", "dotnet/Braid.slnx", "--configuration", "Release", "--no-build", "--no-restore",
        "--logger", "console;verbosity=normal",
    ]


def verify_solution_projects() -> None:
    # Fail closed if the solution grows another test project. It must join discovery,
    # not silently receive the other assembly's filter and run zero tests.
    projects = []
    for entry in ET.parse(SOLUTION).iter("Project"):
        project = entry.attrib["Path"]
        tree = ET.parse(SOLUTION.parent / project)
        if (any(node.text == "true" for node in tree.iter("IsTestProject"))
                or any(node.attrib.get("Include") == "Microsoft.NET.Test.Sdk" for node in tree.iter("PackageReference"))):
            projects.append(project)
    if projects != [TEST_PROJECT]:
        raise ValueError("Update CI discovery to include every solution test project: " + repr(projects))


def read_methods(path: Path) -> list[str]:
    if not path.is_file() or path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("VSTest did not produce a bounded discovery file.")
    methods = path.read_text(encoding="utf-8-sig").splitlines()
    if not methods or any(not METHOD_NAME.fullmatch(method) for method in methods):
        raise ValueError("VSTest discovery was empty or contained an unsafe fully qualified method name.")
    # Theories/overloads can repeat a method identity. Its exact FQN filter selects
    # all data rows, including runtime-discovered rows, together on the same shard.
    return sorted(set(methods))


def partition(methods: Sequence[str], count: int) -> list[list[str]]:
    if count < 1:
        raise ValueError("Shard count must be positive.")
    shards: list[list[str]] = [[] for _ in range(count)]
    for method in sorted(set(methods)):
        if not METHOD_NAME.fullmatch(method):
            raise ValueError("Unsafe fully qualified method name.")
        test_class = method.rsplit(".", 1)[0]
        index = int.from_bytes(hashlib.sha256(test_class.encode("utf-8")).digest()[:8], "big") % count
        shards[index].append(method)
    if any(not shard for shard in shards):
        raise ValueError("Every requested test shard must be nonempty.")
    return shards


def write_settings(path: Path, methods: Sequence[str]) -> None:
    root = ET.Element("RunSettings")
    configuration = ET.SubElement(root, "RunConfiguration")
    ET.SubElement(configuration, "TestCaseFilter").text = "|".join("FullyQualifiedName=" + method for method in methods)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def discover(assembly: Path, target: Path, settings: Path | None = None) -> int:
    # Supported by the pinned VSTest console. Unlike --list-tests (display names),
    # this writes filterable FQNs without parsing localized console output.
    command = ["dotnet", "vstest", str(assembly), "/ListFullyQualifiedTests", "/ListTestsTargetPath:" + str(target)]
    if settings is not None:
        command.append("/Settings:" + str(settings))
    return run(command)


def run_shard(assembly: Path, index: int, count: int) -> int:
    if not 0 <= index < count:
        raise ValueError("Shard index must be within the positive shard count.")
    with tempfile.TemporaryDirectory(prefix="braid-ci-tests-") as directory:
        temporary = Path(directory)
        methods_file = temporary / "all-tests.txt"
        code = discover(assembly, methods_file)
        if code:
            return code
        methods = read_methods(methods_file)
        selected = partition(methods, count)[index]
        settings = temporary / "shard.runsettings"
        write_settings(settings, selected)
        filtered = temporary / "selected-tests.txt"
        code = discover(assembly, filtered, settings)
        if code:
            return code
        if read_methods(filtered) != selected:
            raise ValueError("VSTest filtering did not exactly preserve the planned shard.")
        print(f"CI shard {index + 1}/{count}: {len(selected)} of {len(methods)} discovered method identities", flush=True)
        return run([*test_command(), "--settings", str(settings)])


def classify(methods: Sequence[str], groups: dict[str, tuple[str, ...]]) -> dict[str, list[str]]:
    """Prove union/disjointness against actual FQNs, never guessed case names."""
    owners: dict[str, str] = {}
    for group, classes in groups.items():
        if not classes:
            raise ValueError(f"Empty test-group class roster: {group}")
        for name in classes:
            if not METHOD_NAME.fullmatch(name) or name in owners:
                raise ValueError(f"Unsafe or duplicate test-group class: {name}")
            owners[name] = group
    classified: dict[str, list[str]] = {group: [] for group in groups}
    observed = set()
    if not methods:
        raise ValueError("Test-group discovery must be nonempty.")
    for method in sorted(set(methods)):
        if not METHOD_NAME.fullmatch(method):
            raise ValueError("Unsafe fully qualified method name.")
        name = method.rsplit(".", 1)[0]
        if name not in owners:
            raise ValueError(f"Unclassified discovered test class: {name}. Review its prerequisites.")
        observed.add(name)
        classified[owners[name]].append(method)
    stale = owners.keys() - observed
    if stale:
        raise ValueError("Stale test-group classes absent from discovery: " + ", ".join(sorted(stale)))
    return classified


def require_prerequisites(groups: Sequence[str], assembly: Path) -> None:
    """Read-only admission checks; never start/repair services or create approval.

    Presence is not runtime qualification. Tests retain their own image, custody,
    protected-installation and admission-record validation.
    """
    failures = []

    def require(condition: bool, message: str) -> None:
        if not condition:
            failures.append(message)

    def value(name: str, expected: str | None = None) -> str:
        result = os.environ.get(name, "")
        require(result == expected if expected is not None else bool(result.strip()),
                f"set {name}" + (f"={expected}" if expected is not None else ""))
        return result

    def file(name: str, executable: bool = False) -> None:
        raw = value(name)
        path = Path(raw)
        require(bool(raw) and path.is_absolute() and path.is_file()
                and (not executable or os.access(path, os.X_OK)),
                f"{name} must name an existing absolute " + ("executable" if executable else "file"))

    def directory(name: str) -> None:
        raw = value(name)
        require(bool(raw) and Path(raw).is_absolute() and Path(raw).is_dir(),
                f"{name} must name an existing absolute directory")

    def tool(name: str) -> None:
        require(shutil.which(name) is not None, f"install/locate prerequisite tool {name}")

    def postgres() -> None:
        # The local Linux groups deliberately use the pinned Docker fixture. Native
        # Windows fixtures remain a separate opt-in, not a launcher composition.
        value("BRAID_POSTGRES_IMAGE", "docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc")
        require(not os.environ.get("BRAID_POSTGRES_BIN") and not os.environ.get("BRAID_TEST_CLIENT_CONTAINER"),
                "remove native/client-container PostgreSQL settings for this local Docker group")
        tool(os.environ.get("BRAID_DOCKER_EXECUTABLE", "docker"))

    def installed_documents() -> None:
        value("BRAID_TEST_DOCUMENT_HOST_LINUX", "1")
        value("BRAID_SPEC_DOCUMENT_WORKERS", "optional")
        for suffix in ("OFFICE_ASSEMBLY", "OFFICE_LAUNCHER", "PDF_EXECUTABLE"):
            file("BRAID_SPEC_DOCUMENT_" + suffix, executable=suffix != "OFFICE_ASSEMBLY")
        for suffix in ("DOTNET_ROOT", "OFFICE_WORK_ROOT", "PDF_WORK_ROOT"):
            directory("BRAID_SPEC_DOCUMENT_" + suffix)
        for suffix in ("OFFICE_PROFILE_SHA256", "OFFICE_BUILD_SHA256", "OFFICE_RUNTIME_SHA256",
                       "PDF_BUILD_SHA256", "PDF_PROFILE_SHA256"):
            require(re.fullmatch(r"[0-9a-f]{64}", value("BRAID_SPEC_DOCUMENT_" + suffix)) is not None,
                    "BRAID_SPEC_DOCUMENT_" + suffix + " must be a canonical SHA-256 digest")
        file("BRAID_TEST_PDF_EXECUTABLE", executable=True)
        file("BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD")
        require(re.fullmatch(r"[0-9a-f]{64}", value("BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD_SHA256")) is not None,
                "BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD_SHA256 must be a canonical SHA-256 digest")

    require(assembly.is_file(), "build the current solution in Release first: missing test assembly")
    # Whole-assembly discovery evaluates BrowserReturnPathTests.MemberData, even
    # when that class will be omitted. Its initializer only reads these vectors.
    require((assembly.parent / "Contracts/browser-return-paths.json").is_file(),
            "missing build-copied discovery fixture Contracts/browser-return-paths.json")
    tool("dotnet")
    requested = set(groups)
    if requested - {"fast", "live-provider", "native-windows", "incremental-build"}:
        require(sys.platform == "linux", "the selected groups require Linux")
    if requested & {"integration", "packaged-browser", "host-replica-pilot", "sftp-loopback", "trigger-emulators"}:
        postgres()
    if "host-replica-pilot" in requested:
        value("BRAID_HOST_REPLICA_PILOT", "1")
    if "sftp-loopback" in requested:
        file("BRAID_SFTP_TEST_PYTHON", executable=True)
    if "trigger-emulators" in requested:
        tool(os.environ.get("BRAID_DOCKER_EXECUTABLE") or "docker")
        value("BRAID_TRIGGER_EMULATORS", "compose")
        value("BRAID_TRIGGERS_ACCEPT_EULA", "Y")
    if "integration" in requested:
        tool("bash")
        value("BRAID_AZURITE_IMAGE", "mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5")
        require(not os.environ.get("BRAID_AZURITE_LOOPBACK_ENDPOINT"),
                "remove BRAID_AZURITE_LOOPBACK_ENDPOINT when selecting the pinned Azurite image")
        for name in ("BRAID_FERRULE_WORKER_IMAGE", "BRAID_BUILD_WORKER_IMAGE", "BRAID_COMPILED_RUNTIME_IMAGE",
                     "BRAID_FERRULE_SERVICE_IMAGE"):
            value(name)
        require(os.environ.get("BRAID_BUILD_WORKER_IMAGE") == os.environ.get("BRAID_FERRULE_WORKER_IMAGE"),
                "BRAID_BUILD_WORKER_IMAGE and BRAID_FERRULE_WORKER_IMAGE must name the same combined worker build")
        value("BRAID_AUTOMATION_DOCKER_TESTS", "true")
        for name in ("BRAID_FERRULE_HOST_EXECUTABLE", "BRAID_COMPILE_SANDBOX_EXECUTABLE", "BRAID_TEST_PDF_EXECUTABLE",
                     "BRAID_AUTHORING_TEST_EXECUTABLE", "BRAID_AUTHORED_PREVIEW_TEST_EXECUTABLE", "BRAID_EDIT_TEST_EXECUTABLE"):
            file(name, executable=True)
        for name in ("BRAID_HOST_TEST_SERVER_PATH", "BRAID_HOST_PRIVACY_PROBE_PATH"):
            file(name)
        directory("DOTNET_ROOT")
        root = Path(os.environ.get("DOTNET_ROOT", ""))
        require((root / "sdk/10.0.112/Roslyn/bincore/csc.dll").is_file(), "DOTNET_ROOT needs the pinned 10.0.112 compiler")
        require((root / "packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0").is_dir(),
                "DOTNET_ROOT needs the pinned 10.0.12 reference pack")
    if "packaged-browser" in requested:
        directory("BRAID_AUTH_BROWSER_ADMISSION_ROOT")
        file("BRAID_AUTH_BROWSER_ADMISSION_FILE")
        value("BRAID_WORKSPACE_CHAT_BROWSER", "true")
        require(bool(os.environ.get("DISPLAY")), "use the existing isolated browser owner with an assigned display")
        # The existing admission owner validates the exact published host, probe,
        # node/npm/browser bytes and network custody. Never run admit.mjs here.
    if requested & {"installed-documents", "installed-document-probes"}:
        installed_documents()
    if "installed-document-probes" in requested:
        for prefix in ("BRAID_TEST_DOCUMENT_PROFILE_PROBE_", "BRAID_TEST_DOCUMENT_RESOURCE_PROBE_"):
            file(prefix + "ASSEMBLY")
            for suffix in ("BUILD_SHA256", "RUNTIME_SHA256"):
                require(re.fullmatch(r"[0-9a-f]{64}", value(prefix + suffix)) is not None,
                        prefix + suffix + " must be a canonical SHA-256 digest")
    if "live-provider" in requested:
        value("BRAID_LIVE_OLLAMA_ENDPOINT")
        value("BRAID_LIVE_OLLAMA_MODEL")
    if "native-windows" in requested:
        require(sys.platform == "win32", "native-windows requires Windows")
        directory("BRAID_POSTGRES_BIN")
        root = Path(os.environ.get("BRAID_POSTGRES_BIN", ""))
        for name in ("initdb.exe", "pg_ctl.exe", "postgres.exe", "psql.exe"):
            require((root / name).is_file(), "BRAID_POSTGRES_BIN needs " + name)
        require(not any(os.environ.get(name) for name in ("BRAID_POSTGRES_IMAGE", "BRAID_TEST_CLIENT_CONTAINER", "BRAID_DOCKER_EXECUTABLE")),
                "remove Docker PostgreSQL settings for native-windows")
    if "incremental-build" in requested:
        value("BRAID_OPENAPI_INCREMENTAL_TESTS", "1")
    if failures:
        raise ValueError("Missing/refused local test prerequisites:\n- " + "\n- ".join(dict.fromkeys(failures)))


def run_groups(assembly: Path, requested: Sequence[str]) -> int:
    groups = test_groups.class_groups()
    if not requested or set(requested) - groups.keys():
        raise ValueError("Select at least one known local test group.")
    test_groups.verify_sources(Path.cwd())
    require_prerequisites(requested, assembly)
    with tempfile.TemporaryDirectory(prefix="braid-local-tests-") as directory:
        temporary = Path(directory)
        all_methods = temporary / "all-tests.txt"
        code = discover(assembly, all_methods)
        if code:
            return code
        methods = read_methods(all_methods)
        classified = classify(methods, groups)
        selected = sorted(method for group in set(requested) for method in classified[group])
        if not selected or any(not classified[group] for group in requested):
            raise ValueError("Every requested local test group must select discovered tests.")
        for group in groups:
            status = "selected" if group in requested else "OMITTED"
            print(f"Local group {group}: {status}; {len(classified[group])} discovered method identities", flush=True)
        print("Local selection only; not full CI or native/browser/provider/cloud acceptance.", flush=True)
        settings = temporary / "groups.runsettings"
        write_settings(settings, selected)
        filtered = temporary / "selected-tests.txt"
        code = discover(assembly, filtered, settings)
        if code:
            return code
        if read_methods(filtered) != selected:
            raise ValueError("VSTest filtering did not exactly preserve the planned local groups.")
        return run([*test_command(), "--settings", str(settings)])


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shard-index", type=int)
    parser.add_argument("--shard-count", type=int)
    parser.add_argument("--group", action="append", choices=tuple(test_groups.GROUP_CLASSES),
                        help="explicit local selection; repeat to combine groups (never implicit in CI)")
    options = parser.parse_args(argv)
    if options.group:
        if options.shard_index is not None or options.shard_count is not None:
            parser.error("local --group selection cannot be combined with CI shard flags")
        verify_solution_projects()
        try:
            return run_groups(TEST_ASSEMBLY, options.group)
        except (ValueError, OSError) as error:
            print(str(error), file=sys.stderr)
            return 2
    if options.shard_index is None and options.shard_count is None:
        return run(test_command())
    if options.shard_index is None or options.shard_count is None or not 0 <= options.shard_index < options.shard_count:
        parser.error("supply both --shard-index and --shard-count with 0 <= index < count")
    verify_solution_projects()
    return run_shard(TEST_ASSEMBLY, options.shard_index, options.shard_count)


if __name__ == "__main__":
    raise SystemExit(main())
