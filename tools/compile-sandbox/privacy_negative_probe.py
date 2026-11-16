"""Supplementary Linux privacy probe. No supplied PID or startup tracing is supported."""
from __future__ import annotations

import argparse
import ctypes
import errno
import hashlib
import json
import os
from pathlib import Path
import platform
import select
import shutil
import stat
import subprocess
import sys
import tempfile
import time

SDK, RUNTIME = "10.0.112", "10.0.12"
MAX_OUTPUT = 1 << 20


class Deadline:
    def __init__(self, seconds):
        self.end = time.monotonic() + seconds

    def remaining(self):
        value = self.end - time.monotonic()
        if value <= 0:
            raise TimeoutError("The whole probe operation exceeded its deadline.")
        return value


def environment(root, directory):
    return {
        "PATH": f"{root}:/usr/bin:/bin", "HOME": str(directory), "TMPDIR": str(directory),
        "DOTNET_ROOT": str(root), "DOTNET_CLI_HOME": str(directory),
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "false", "DOTNET_CLI_TELEMETRY_OPTOUT": "true",
        "DOTNET_NOLOGO": "true", "DOTNET_PROCESSOR_COUNT": "1",
        "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "1", "DOTNET_ROLL_FORWARD": "Disable",
        "DOTNET_MULTILEVEL_LOOKUP": "0", "DOTNET_EnableDiagnostics": "0",
        "DOTNET_EnableWriteXorExecute": "0", "DOTNET_GCHeapHardLimit": "0x20000000",
        "DOTNET_GCRegionRange": "0x20000000", "DOTNET_GCNoAffinitize": "1", "MALLOC_ARENA_MAX": "2",
    }


def status(pid):
    return dict(line.split(":", 1) for line in Path(f"/proc/{pid}/status").read_text().splitlines() if ":" in line)


def credentials(value, expected):
    for key in ("Uid:", "Gid:"):
        fields = value[key].split()
        if len(fields) != 4 or len(set(fields)) != 1 or fields != expected[key].split() or "0" in fields:
            raise RuntimeError("Real, effective, saved and filesystem credentials must match the non-root parent.")
    for key in ("CapInh:", "CapPrm:", "CapEff:", "CapAmb:", "TracerPid:"):
        if int(value[key].strip(), 10 if key == "TracerPid:" else 16):
            raise RuntimeError("Probe participants must be capless and untraced.")


def prepare(args):
    root, directory = args.dotnet_root.resolve(strict=True), args.directory.resolve()
    if directory.exists():
        raise RuntimeError("Fixture destination must not exist; nothing will be overwritten.")
    compiler = root / f"sdk/{SDK}/Roslyn/bincore/csc.dll"
    references = root / f"packs/Microsoft.NETCore.App.Ref/{RUNTIME}/ref/net10.0"
    if not compiler.is_file() or not references.is_dir():
        raise RuntimeError("The pinned offline compiler/reference installation is absent.")
    source = Path(__file__).with_name("PrivacyProbe.cs")
    options = ["-target:exe", "-nologo", "-nostdlib+", "-langversion:14", "-nullable:enable",
               "-deterministic+", "-unsafe-", "-debug-", f"-out:{directory / 'braid-privacy-probe.dll'}"]
    options += [f"-reference:{path}" for path in sorted(references.glob("*.dll"))] + [str(directory / source.name)]
    if any(any(character in value for character in ('"', "\n", "\r")) for value in options):
        raise RuntimeError("A fixture path contains an unsupported response-file character.")
    directory.mkdir(mode=0o700)
    (directory / source.name).write_bytes(source.read_bytes())
    (directory / "compiler.rsp").write_text("\n".join(f'"{value}"' for value in options) + "\n", encoding="utf-8")
    (directory / "braid-privacy-probe.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
        "tfm": "net10.0", "rollForward": "Disable", "framework": {"name": "Microsoft.NETCore.App", "version": RUNTIME},
    }}) + "\n", encoding="utf-8")
    print(json.dumps({"compile_command": [str(root / "dotnet"), "exec", str(compiler), "-noconfig", "@" + str(directory / "compiler.rsp")]}))


class Output:
    def __init__(self, child, deadline):
        self.child, self.deadline, self.data, self.errors = child, deadline, bytearray(), bytearray()
        self.streams = [child.stdout, child.stderr]

    def receive(self, size=None):
        while True:
            self.deadline.remaining()
            end = size if size is not None else self.data.find(b"\n") + 1
            if end and len(self.data) >= end:
                result = bytes(self.data[:end]); del self.data[:end]
                return result
            ready, _, _ = select.select(self.streams, [], [], self.deadline.remaining())
            for stream in ready:
                chunk = os.read(stream.fileno(), 8192)
                if not chunk:
                    if stream is self.child.stdout:
                        raise RuntimeError("Synthetic child ended before its report.")
                    self.streams.remove(stream)
                    continue
                target = self.data if stream is self.child.stdout else self.errors
                target.extend(chunk)
                if len(target) > MAX_OUTPUT:
                    raise RuntimeError("Synthetic child exceeded its output bound.")


class Iovec(ctypes.Structure):
    _fields_ = [("base", ctypes.c_void_p), ("size", ctypes.c_size_t)]


def attach(libc, pid, deadline):
    def trace(request):
        deadline.remaining()
        return libc.ptrace(request, pid, None, None)
    ctypes.set_errno(0)
    if trace(0x4206) < 0:  # SEIZE only after the managed-start report, never during startup.
        return {"allowed": False, "errno": ctypes.get_errno()}
    if trace(0x4207) < 0:  # INTERRUPT, then detach without changing registers.
        raise RuntimeError("Could not interrupt the owned synthetic tracee.")
    while True:
        deadline.remaining()
        waited, state = os.waitpid(pid, os.WNOHANG | os.WUNTRACED)
        if waited:
            if not os.WIFSTOPPED(state) or trace(17) < 0:
                raise RuntimeError("Could not detach the owned synthetic child.")
            return {"allowed": True, "errno": 0}
        time.sleep(min(0.005, deadline.remaining()))


def observe(libc, child, report, expected_exe, parent, deadline):
    if report.get("pid") != child.pid or report.get("exe") != str(expected_exe):
        raise RuntimeError("Report provenance does not match the owned synthetic process.")
    current = status(child.pid)
    if int(current["PPid"]) != os.getpid():
        raise RuntimeError("Synthetic process is not owned by this parent.")
    credentials({key + ":": value for key, value in current.items()}, parent)
    credentials(report, parent)
    result = {"self": report, "attach": attach(libc, child.pid, deadline)}
    for name in ("environ", "maps", "mem"):
        deadline.remaining()
        try:
            fd = os.open(f"/proc/{child.pid}/{name}", os.O_RDONLY | os.O_CLOEXEC)
        except OSError as error:
            result[name] = {"allowed": False, "errno": error.errno}
        else:
            os.close(fd); result[name] = {"allowed": True, "errno": 0}
    buffer = ctypes.create_string_buffer(16)
    local, remote = Iovec(ctypes.addressof(buffer), 16), Iovec(report["address"], 16)
    deadline.remaining(); ctypes.set_errno(0)
    count = libc.process_vm_readv(child.pid, ctypes.byref(local), 1, ctypes.byref(remote), 1, 0)
    result["memory"] = {"allowed": count == 16 and buffer.raw == b"Q" * 16, "count": count,
                        "errno": ctypes.get_errno() if count < 0 else 0}
    return result


def managed_image(initial_command):
    return Path(initial_command)


def sample(command, root, directory, libc, marker, parent, deadline, owned):
    deadline.remaining()
    child = subprocess.Popen(command, cwd=directory, env=environment(root, directory), stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, close_fds=True, bufsize=0)
    owned.append(child)  # Retain ownership even if a later report, detach or settlement fails.
    ledger_path = directory / "ownership.json"
    ledger = json.loads(ledger_path.read_text()) if ledger_path.exists() else {"parent": os.getpid(), "children": []}
    process_stat = Path(f"/proc/{child.pid}/stat").read_text().rsplit(")", 1)[1].split()
    ledger["children"].append({"pid": child.pid, "start_ticks": process_stat[19], "initial_command": command})
    ledger_path.write_text(json.dumps(ledger, indent=2) + "\n", encoding="utf-8")
    output = Output(child, deadline)
    if marker is not None:
        if output.receive(4) != marker:
            raise RuntimeError("Launcher did not report the selected contract marker.")
        gate = status(child.pid)
        credentials({key + ":": value for key, value in gate.items()}, parent)
        if int(gate["NoNewPrivs"]) != 1 or int(gate["Seccomp"]) != 2:
            raise RuntimeError("Launcher gate lacks its required controls.")
        child.stdin.write(b"G")
    results = []
    for phase in ("managed-start", "after-mutations"):
        report = json.loads(output.receive())
        if report.get("phase") != phase:
            raise RuntimeError("The target did not report the expected managed checkpoint.")
        expected_exe = managed_image(command[0])
        results.append(observe(libc, child, report, expected_exe, parent, deadline))
        child.stdin.write(b"M\n" if phase == "managed-start" else b"Q\n")
    child.stdin.close()
    child.wait(timeout=deadline.remaining())
    if child.returncode != 0:
        raise RuntimeError("Synthetic child did not finish normally.")
    return results


def settle(owned, deadline):
    confirmed = True
    for child in owned:
        try:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=deadline.remaining())
        except (OSError, TimeoutError, subprocess.TimeoutExpired):
            confirmed = False
            continue
        for stream in (child.stdin, child.stdout, child.stderr):
            if stream:
                try:
                    stream.close()
                except OSError:
                    confirmed = False
    return confirmed


def access(observation, allowed):
    return all(observation[name]["allowed"] is allowed for name in ("attach", "environ", "maps", "mem", "memory"))


def provenance(path):
    value = path.stat()
    if not stat.S_ISREG(value.st_mode) or value.st_mode & (stat.S_ISUID | stat.S_ISGID):
        raise RuntimeError("Probe files must be ordinary unprivileged files.")
    try:
        capability = os.getxattr(path, "security.capability")
    except OSError as error:
        if error.errno not in (errno.ENODATA, errno.ENOTSUP):
            raise
    else:
        if capability:
            raise RuntimeError("File capabilities are not admitted.")
    return {"path": str(path), "device": value.st_dev, "inode": value.st_ino, "uid": value.st_uid,
            "gid": value.st_gid, "mode": stat.S_IMODE(value.st_mode), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def run(args):
    if sys.platform != "linux" or platform.machine() != "x86_64" or os.getuid() == 0:
        raise RuntimeError("Requires the approved real Linux x86-64 non-root qualification environment.")
    deadline = Deadline(30)
    parent = {key + ":": value for key, value in status("self").items()}
    credentials(parent, parent)
    libc = ctypes.CDLL(None, use_errno=True)
    libc.ptrace.restype, libc.ptrace.argtypes = ctypes.c_long, [ctypes.c_uint, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p]
    libc.process_vm_readv.restype = ctypes.c_ssize_t
    libc.process_vm_readv.argtypes = [ctypes.c_int, ctypes.POINTER(Iovec), ctypes.c_ulong, ctypes.POINTER(Iovec), ctypes.c_ulong, ctypes.c_ulong]
    root, probe, launcher = (path.resolve(strict=True) for path in (args.dotnet_root, args.probe_assembly, args.launcher))
    if probe.name != "braid-privacy-probe.dll":
        raise RuntimeError("Only the named synthetic fixture is admitted.")
    files = [provenance(path) for path in (launcher, root / "dotnet", probe, probe.with_suffix(".runtimeconfig.json"), root / f"host/fxr/{RUNTIME}/libhostfxr.so")]
    directory = Path(tempfile.mkdtemp(prefix="braid-privacy-", dir=args.work_root.resolve(strict=True)))
    owned = []
    try:
        control = sample([str(root / "dotnet"), str(probe), "control"], root, directory, libc, None, parent, deadline, owned)
        if not all(value["self"]["dump"] == 1 and access(value, True) for value in control):
            raise RuntimeError("Positive control blocked: INCONCLUSIVE. Do not grant tracing or change policy.")
        command = [str(launcher), "--cpu-seconds=30", "--memory-bytes=3221225472", "--open-files=128", "--file-bytes=1048576"]
        command += [f"--dotnet-root={root}", "--profile=runtime", f"--runtime-assembly={probe}"]
        read = [root, probe.parent] + [Path(path) for path in ("/lib", "/lib64", "/usr/lib", "/usr/lib64", "/dev/urandom") if Path(path).exists()]
        command += [f"--read={path}" for path in dict.fromkeys(read)]
        target = sample(command, root, directory, libc, b"BFC2", parent, deadline, owned)
        protected = all(value["self"]["dump"] == 0 and access(value, False) and
                        all(value[name]["errno"] in (errno.EPERM, errno.EACCES) for name in ("attach", "environ", "maps", "mem", "memory")) for value in target)
        mutations = target[-1]["self"]["mutations"]
        protected = protected and mutations.get("thread-dump") == 0 and len(mutations) == 10 and all(
            value == [-1, errno.EPERM] for key, value in mutations.items() if key != "thread-dump")
        vulnerable = all(value["self"]["dump"] == 1 and access(value, True) for value in target)
        print(json.dumps({"files": files, "control": control, "target": target, "protected": protected, "vulnerable": vulnerable}, indent=2))
        if not (protected if args.expect == "protected" else vulnerable):
            raise RuntimeError("Observed result does not meet the explicit expected outcome.")
    finally:
        if settle(owned, Deadline(2)):
            shutil.rmtree(directory)
        else:
            print(f"SETTLEMENT UNCONFIRMED: retained owned directory {directory}", file=sys.stderr)
            raise RuntimeError("Probe cleanup was not confirmed; retained files must not be erased automatically.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="mode", required=True)
    fixture = commands.add_parser("prepare")
    fixture.add_argument("--directory", type=Path, required=True)
    fixture.add_argument("--dotnet-root", type=Path, required=True)
    fixture.set_defaults(action=prepare)
    probe = commands.add_parser("run")
    for option in ("launcher", "dotnet-root", "probe-assembly", "work-root"):
        probe.add_argument("--" + option, type=Path, required=True)
    probe.add_argument("--expect", choices=("protected", "vulnerable"), default="protected")
    probe.set_defaults(action=run)
    args = parser.parse_args(); args.action(args)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        print(f"PROBE FAILED OR INCONCLUSIVE: {error}", file=sys.stderr)
        sys.exit(2)
