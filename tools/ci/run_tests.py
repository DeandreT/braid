"""Run all solution tests, optionally in exhaustive class-stable CI shards."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import re
import subprocess
import tempfile
from collections.abc import Sequence
import xml.etree.ElementTree as ET


SOLUTION = Path("dotnet/Weft.slnx")
TEST_PROJECT = "tests/Weft.Tests/Weft.Tests.csproj"
TEST_ASSEMBLY = Path("dotnet/tests/Weft.Tests/bin/Release/net10.0/Weft.Tests.dll")
METHOD_NAME = re.compile(r"[A-Za-z_][A-Za-z_0-9]*(?:[.+][A-Za-z_][A-Za-z_0-9]*)+")


def run(command: Sequence[str]) -> int:
    environment = os.environ.copy()
    environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false"
    # .NET forwards unrelated inheritable descriptors to Process.Start children.
    # Start its test host with only standard streams, not the CI runner's handles.
    return subprocess.run(command, env=environment, close_fds=True, check=False).returncode


def test_command() -> list[str]:
    return [
        "dotnet", "test", "dotnet/Weft.slnx", "--configuration", "Release", "--no-build", "--no-restore",
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
    with tempfile.TemporaryDirectory(prefix="weft-ci-tests-") as directory:
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


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shard-index", type=int)
    parser.add_argument("--shard-count", type=int)
    options = parser.parse_args(argv)
    if options.shard_index is None and options.shard_count is None:
        return run(test_command())
    if options.shard_index is None or options.shard_count is None or not 0 <= options.shard_index < options.shard_count:
        parser.error("supply both --shard-index and --shard-count with 0 <= index < count")
    verify_solution_projects()
    return run_shard(TEST_ASSEMBLY, options.shard_index, options.shard_count)


if __name__ == "__main__":
    raise SystemExit(main())
