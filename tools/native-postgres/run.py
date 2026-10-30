"""Run an explicit native Windows PostgreSQL test selection from a clean, short checkout."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET


class QualificationError(RuntimeError):
    pass


@dataclass(frozen=True)
class Options:
    repo: Path
    work_root: Path
    results_root: Path
    dotnet: Path
    postgres_bin: Path
    packages: Path
    selection: str
    phase_seconds: int = 1800
    keep_work: bool = False
    expected_tests: int | None = None


def ordinary(path: Path, *, directory: bool) -> None:
    if not path.is_absolute() or (not path.is_dir() if directory else not path.is_file()):
        raise QualificationError("An explicit existing absolute path is required.")
    for item in (path, *path.parents):
        if item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()):
            raise QualificationError("Qualification paths must not traverse links or junctions.")


def validate(options: Options) -> None:
    for path in (options.repo, options.work_root, options.results_root, options.postgres_bin, options.packages):
        ordinary(path, directory=True)
    ordinary(options.dotnet, directory=False)
    for name in ("postgres.exe", "initdb.exe", "pg_ctl.exe", "psql.exe"):
        ordinary(options.postgres_bin / name, directory=False)
    if options.dotnet.name.lower() != "dotnet.exe":
        raise QualificationError("Select the native dotnet.exe, not a shell launcher.")
    # Leave room for nested runtime dependencies copied into the test output.
    if len(str(options.work_root / "native-0123456789ab/source").encode("utf-16-le")) // 2 > 80:
        raise QualificationError("Choose a shorter work root, for example C:/example/weft-native-checks.")
    if not options.selection.strip() or len(options.selection) > 4096 or any(c in options.selection for c in "\0\r\n"):
        raise QualificationError("A bounded, nonempty test filter is required.")
    if not 30 <= options.phase_seconds <= 86400:
        raise QualificationError("Phase timeout must be between 30 and 86400 seconds.")
    if options.expected_tests is not None and options.expected_tests < 1:
        raise QualificationError("Expected test count must be positive.")
    if options.work_root == options.results_root or options.results_root.is_relative_to(options.work_root):
        raise QualificationError("Keep retained results outside the disposable work root.")


def child_environment(options: Options, run_root: Path, clusters: Path, inherited=None) -> dict[str, str]:
    parent = os.environ if inherited is None else inherited
    allowed = {"systemroot", "windir", "comspec", "pathext", "userprofile", "appdata", "localappdata",
               "programfiles", "programfiles(x86)", "programdata", "allusersprofile"}
    environment = {key: value for key, value in parent.items() if key.lower() in allowed}
    windows = parent.get("SystemRoot", parent.get("SYSTEMROOT", "C:/Windows"))
    environment.update({
        "PATH": os.pathsep.join((str(options.dotnet.parent), str(options.postgres_bin), str(Path(windows) / "System32"), windows)),
        "TEMP": str(run_root / "tmp"), "TMP": str(run_root / "tmp"),
        "DOTNET_ROOT": str(options.dotnet.parent), "DOTNET_HOST_PATH": str(options.dotnet),
        "DOTNET_CLI_HOME": str(run_root / "home"), "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "false",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1", "DOTNET_PROCESSOR_COUNT": "2",
        "MSBUILDDISABLENODEREUSE": "1", "NUGET_PACKAGES": str(options.packages),
        "NUGET_HTTP_CACHE_PATH": str(run_root / "http-cache"),
        "WEFT_POSTGRES_BIN": str(options.postgres_bin), "WEFT_POSTGRES_WORK_ROOT": str(clusters),
    })
    return environment


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path: Path, value) -> None:
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    with temporary.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def git(repo: Path, *arguments: str) -> str:
    return subprocess.check_output(["git", "-C", str(repo), *arguments], text=True, timeout=60).strip()


def manifest(repo: Path, head: str) -> dict[str, str]:
    result = {}
    listing = subprocess.check_output(["git", "-C", str(repo), "ls-tree", "-r", "--full-tree", "-z", head], timeout=60)
    for entry in listing.split(b"\0"):
        if not entry:
            continue
        metadata, name = entry.split(b"\t", 1)
        mode, kind, _ = metadata.decode().split()
        relative = Path(name.decode("utf-8"))
        if mode not in ("100644", "100755") or kind != "blob" or relative.is_absolute() or ".." in relative.parts:
            raise QualificationError("Only ordinary committed source files are admitted.")
        path = repo / relative
        ordinary(path, directory=False)
        result[relative.as_posix()] = digest(path)
    return result


def unchanged(source: Path, expected: dict[str, str]) -> bool:
    return all((source / name).is_file() and digest(source / name) == value for name, value in expected.items())


def align_checkout_bytes(original: Path, checkout: Path, expected: dict[str, str]) -> list[str]:
    """Preserve captured Windows text bytes without changing the original checkout."""
    ordinary(original, directory=True)
    ordinary(checkout, directory=True)
    copies = {}
    for name, wanted in expected.items():
        source, target = original / name, checkout / name
        ordinary(source, directory=False)
        ordinary(target, directory=False)
        if digest(target) == wanted:
            continue
        before, current = source.read_bytes(), target.read_bytes()
        if hashlib.sha256(before).hexdigest() != wanted:
            raise QualificationError("The original source changed after capture.")
        try:
            before.decode("utf-8", "strict")
            current.decode("utf-8", "strict")
        except UnicodeDecodeError as error:
            raise QualificationError("The detached checkout differs from the captured source.") from error
        if b"\0" in before or b"\0" in current or before.replace(b"\r\n", b"\n") != current.replace(b"\r\n", b"\n"):
            raise QualificationError("The detached checkout differs beyond text line endings.")
        copies[name] = before
    # Admit every difference before changing any file in the new owned snapshot.
    for name, content in copies.items():
        target = checkout / name
        mode = stat.S_IMODE(target.stat().st_mode)
        temporary = target.with_name(target.name + "." + uuid.uuid4().hex + ".tmp")
        with temporary.open("xb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.chmod(mode)
        os.replace(temporary, target)
    if not unchanged(checkout, expected):
        raise QualificationError("The detached checkout does not preserve the captured bytes.")
    return sorted(copies)


def reconcile_checkout_index(checkout: Path, head: str, expected: dict[str, str], copied: list[str]) -> None:
    """Refresh only copied paths in the owned index without changing its tree."""
    tree = git(checkout, "rev-parse", head + "^{tree}")
    if git(checkout, "rev-parse", "HEAD") != head or git(checkout, "write-tree") != tree:
        raise QualificationError("The detached checkout HEAD or index changed before alignment.")
    if copied:
        subprocess.run(["git", "--literal-pathspecs", "-C", str(checkout), "add",
                        "--pathspec-from-file=-", "--pathspec-file-nul"],
                       input=b"".join(name.encode("utf-8") + b"\0" for name in copied),
                       check=True, capture_output=True, timeout=60)
    if (git(checkout, "rev-parse", "HEAD") != head or git(checkout, "write-tree") != tree
            or not unchanged(checkout, expected) or git(checkout, "status", "--porcelain")):
        raise QualificationError("The detached checkout alignment changed its tree or captured bytes.")


def test_result(path: Path, expected_tests: int | None = None) -> dict:
    if not path.is_file() or path.stat().st_size > 64 * 1024 * 1024:
        raise QualificationError("A complete bounded TRX result is required.")
    try:
        root = ET.parse(path)
        summary = root.find(".//{*}ResultSummary")
        counter = root.find(".//{*}Counters")
        if summary is None or counter is None:
            raise ValueError()
        counts = {name: int(counter.attrib[name]) for name in ("total", "executed", "passed", "failed", "notExecuted")}
        rows = root.findall(".//{*}UnitTestResult")
        failed = [row.attrib["testName"] for row in rows if row.attrib.get("outcome") == "Failed"]
    except (ET.ParseError, ValueError, KeyError) as error:
        raise QualificationError("The test result is incomplete or malformed.") from error
    passed = (summary.attrib.get("outcome") in ("Completed", "Passed") and counts["total"] > 0
              and counts["total"] == counts["executed"] == counts["passed"]
              and counts["failed"] == counts["notExecuted"] == 0
              and len(rows) == counts["total"] and all(row.attrib.get("outcome") == "Passed" for row in rows)
              and (expected_tests is None or counts["total"] == expected_tests))
    return {"passed": passed, "counts": counts, "failedTests": failed}


def user_path():
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
        try:
            return winreg.QueryValueEx(key, "Path")
        except FileNotFoundError:
            return None


def phase(command: list[str], cwd: Path, environment: dict[str, str], log: Path, seconds: int) -> dict:
    started = time.monotonic()
    with log.open("xb") as output:
        process = subprocess.Popen(command, cwd=cwd, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                   creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))
        interrupted = False
        reason = None
        try:
            while True:
                remaining = seconds - (time.monotonic() - started)
                if remaining <= 0:
                    reason = "deadline"
                    raise subprocess.TimeoutExpired(command, seconds)
                try:
                    code = process.wait(timeout=min(2, remaining))
                    break
                except subprocess.TimeoutExpired:
                    if shutil.disk_usage(cwd).free < 25 * 2**30:
                        reason = "freeSpace"
                        raise
        except (subprocess.TimeoutExpired, KeyboardInterrupt, OSError):
            interrupted = True
            reason = reason or "interrupted"
            try:
                process.send_signal(getattr(signal, "CTRL_BREAK_EVENT", signal.SIGINT))
                code = process.wait(timeout=120)
            except (OSError, subprocess.TimeoutExpired, KeyboardInterrupt):
                # Retain the entire checkout/cluster area. Never claim this owner settled.
                return {"passed": False, "settled": False, "interrupted": True, "reason": reason, "pid": process.pid}
    return {"passed": code == 0 and not interrupted, "settled": True, "interrupted": interrupted,
            "reason": reason, "exitCode": code, "seconds": round(time.monotonic() - started, 3)}


def qualify(options: Options) -> tuple[int, Path]:
    if os.name != "nt":
        raise QualificationError("This launcher requires native Windows; Linux qualification is separate.")
    validate(options)
    if git(options.repo, "status", "--porcelain"):
        raise QualificationError("Commit or move source changes before qualification.")
    head = git(options.repo, "rev-parse", "HEAD")
    source_files = manifest(options.repo, head)
    identity = uuid.uuid4().hex[:12]
    work = options.work_root / ("native-" + identity)
    source = work / "source"
    results = options.results_root / ("weft-native-" + identity)
    work.mkdir()
    results.mkdir()
    marker = {"id": identity, "sourceHead": head, "source": str(source)}
    save(work / "owner.json", marker)
    save(results / "source.json", {**marker, "files": source_files, "filter": options.selection})
    for name in ("tmp", "home", "http-cache", "clusters"):
        (work / name).mkdir()
    environment = child_environment(options, work, work / "clusters")
    original_path = user_path()
    record = {"status": "failed", "sourceHead": head, "work": str(work), "phases": {}, "retainedWork": True}
    try:
        expected_sdk = json.loads((options.repo / "global.json").read_text())["sdk"]["version"]
        sdk = subprocess.check_output([str(options.dotnet), "--version"], cwd=options.repo, env=environment, text=True, timeout=30).strip()
        if sdk != expected_sdk:
            raise QualificationError("The selected SDK does not match global.json.")
        record["tools"] = {"dotnet": {"path": str(options.dotnet), "version": sdk, "sha256": digest(options.dotnet)}, "postgres": {}}
        for name in ("postgres.exe", "initdb.exe", "pg_ctl.exe", "psql.exe"):
            version = subprocess.check_output([str(options.postgres_bin / name), "--version"], env=environment, text=True, timeout=30)
            match = re.search(r"\(PostgreSQL\)\s+([0-9.]+)", version)
            if not match or match[1] != "16.15":
                raise QualificationError("The PostgreSQL binaries must all be version 16.15.")
            record["tools"]["postgres"][name] = {"version": match[1], "sha256": digest(options.postgres_bin / name)}
        if shutil.disk_usage(options.work_root).free < 25 * 2**30:
            raise QualificationError("The work drive is below the 25 GiB free-space floor.")
        git(options.repo, "worktree", "add", "--detach", str(source), head)
        record["checkoutByteCopies"] = align_checkout_bytes(options.repo, source, source_files)
        reconcile_checkout_index(source, head, source_files, record["checkoutByteCopies"])
        if not unchanged(source, source_files):
            raise QualificationError("The detached checkout differs from the captured source.")
        nuget = work / "NuGet.Config"
        nuget.write_text('<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>', encoding="utf-8")
        project = "dotnet/tests/Weft.Tests/Weft.Tests.csproj"
        artifacts = str(source / "artifacts/w")
        commands = {
            "restore": [str(options.dotnet), "restore", project, "--locked-mode", "--disable-parallel", "--configfile", str(nuget), "--artifacts-path", artifacts, "-p:OpenApiGenerateDocuments=false", "-v:quiet"],
            "build": [str(options.dotnet), "build", project, "--no-restore", "--artifacts-path", artifacts, "-c", "Release", "-p:OpenApiGenerateDocuments=false", "-p:UseSharedCompilation=false", "-m:1", "-nr:false", "-v:quiet"],
            "test": [str(options.dotnet), "test", project, "--no-build", "--no-restore", "--artifacts-path", artifacts, "-c", "Release", "--filter", options.selection, "--results-directory", str(results), "--logger", "trx;LogFileName=tests.trx", "--logger", "console;verbosity=normal", "-v:quiet"],
        }
        for name, command in commands.items():
            if shutil.disk_usage(options.work_root).free < 25 * 2**30:
                raise QualificationError("Free-space floor reached; no further phase started.")
            print(f"{name}: started; log is in {results}", flush=True)
            outcome = phase(command, source, environment, results / (name + ".log"), options.phase_seconds)
            record["phases"][name] = outcome
            if name == "build" and outcome["passed"]:
                files = {}
                for path in (source / "artifacts/w/bin").rglob("*"):
                    if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
                        raise QualificationError("Linked build output is not admitted.")
                    if path.is_file():
                        files[path.relative_to(source).as_posix()] = digest(path)
                save(results / "products.json", files)
            if name == "test" and outcome["settled"] and (results / "tests.trx").is_file():
                record["tests"] = test_result(results / "tests.trx", options.expected_tests)
            if not outcome["passed"]:
                raise QualificationError("A phase failed or did not settle; its work is retained.")
        record["tests"] = test_result(results / "tests.trx", options.expected_tests)
        if not record["tests"]["passed"]:
            raise QualificationError("Tests failed, were skipped, or did not match the expected selection.")
        record["sourceUnchanged"] = unchanged(source, source_files) and not git(source, "status", "--porcelain")
        if not record["sourceUnchanged"]:
            raise QualificationError("The test source changed during qualification.")
        record["originalSourceUnchanged"] = (git(options.repo, "rev-parse", "HEAD") == head
                                              and not git(options.repo, "status", "--porcelain")
                                              and unchanged(options.repo, source_files))
        if not record["originalSourceUnchanged"]:
            raise QualificationError("The original checkout changed; the result cannot qualify its current state.")
        record["status"] = "passed"
    except (QualificationError, OSError, subprocess.SubprocessError, ValueError, KeyError, KeyboardInterrupt) as error:
        record["failure"] = type(error).__name__
        if isinstance(error, QualificationError):
            record["reason"] = str(error)
        print("Qualification did not pass. Read the retained logs; do not treat it as success.", flush=True)
    finally:
        try:
            record["userPathUnchanged"] = user_path() == original_path
        except OSError:
            record["userPathUnchanged"] = False
        if not record["userPathUnchanged"]:
            record["status"] = "failed"
            record["reason"] = "User PATH changed or could not be verified after the run."
        if record["status"] == "passed":
            try:
                ordinary(work, directory=True)
                ordinary(work / "owner.json", directory=False)
                ordinary(work / "clusters", directory=True)
                if any((work / "clusters").iterdir()) or json.loads((work / "owner.json").read_text()) != marker:
                    record["status"] = "cleanupIncomplete"
                    record["reason"] = "Cluster work remains or the ownership marker changed."
                elif not options.keep_work:
                    git(options.repo, "worktree", "remove", str(source))
                    shutil.rmtree(work)
                    record["retainedWork"] = False
            except (QualificationError, OSError, subprocess.SubprocessError, ValueError):
                record["status"] = "cleanupIncomplete"
                record["reason"] = "The owned worktree could not be safely removed."
        save(results / "summary.json", record)
    return (0 if record["status"] == "passed" else 1), results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--results-root", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--postgres-bin", type=Path, required=True)
    parser.add_argument("--nuget-packages", type=Path, required=True)
    parser.add_argument("--filter", dest="selection", required=True)
    parser.add_argument("--phase-seconds", type=int, default=1800)
    parser.add_argument("--expected-tests", type=int)
    parser.add_argument("--keep-work", action="store_true")
    args = parser.parse_args()
    try:
        code, results = qualify(Options(args.repo, args.work_root, args.results_root, args.dotnet, args.postgres_bin,
                                         args.nuget_packages, args.selection, args.phase_seconds, args.keep_work, args.expected_tests))
    except QualificationError as error:
        print(str(error), file=sys.stderr)
        return 2
    except (OSError, subprocess.SubprocessError, ValueError, KeyError):
        print("Setup refused. Check explicit paths, tool versions, clean source and short work root.", file=sys.stderr)
        return 2
    print(f"Result: {results / 'summary.json'}", flush=True)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
