"""Standalone Braid #4 source candidate; never a capture driver."""

import argparse
import errno
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import resource
import select
import signal
import socket
import stat
import sys
import threading
import time


HERE = Path(__file__).resolve().parent
SCHEMA = "braid.native-bun-fixture.v1"
MAX_FD = (1 << 26) - 1
ELF_CAP = 256 << 20  # Standalone only; does not change capture's 64 MiB limit.
LIB_CAP = 256 << 10
SOURCE_CAP = 64 << 10
RUN = 2.0
PROOF = 0.150
COOPERATE = 0.250
TERMINATE = 0.250
REAP = 1.0
TICK = 0.005
MARKER = "/memfd:braid-fixture-v1 (deleted)"
SEALS = (
    fcntl.F_SEAL_WRITE | fcntl.F_SEAL_GROW |
    fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_SEAL
) if sys.platform == "linux" else 0
SIGNALS = (signal.SIGINT, signal.SIGTERM)
STOP = False


class Refused(Exception):
    pass


def require(condition, reason):
    if not condition:
        raise Refused(reason)


def fits(fd):
    return type(fd) is int and 0 <= fd <= MAX_FD


def checked(fd):
    if not fits(fd):
        if type(fd) is int and fd >= 0:
            os.close(fd)  # Original full-width value; never retry.
        raise Refused("descriptorRange")
    return fd


def flags():
    return os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK


def read_bounded(fd, limit):
    require(fits(fd), "descriptorRange")
    data = bytearray()
    while len(data) <= limit:
        block = os.read(fd, min(65536, limit + 1 - len(data)))
        if not block:
            return bytes(data)
        data.extend(block)
    raise Refused("inputLimit")


def file_bytes(path, limit):
    fd = checked(os.open(path, flags()))
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_size <= limit, "artifactType")
        data = read_bounded(fd, limit)
        require(len(data) == info.st_size, "artifactChanged")
        return data
    finally:
        os.close(fd)


def elf_header(fd, allowed_types):
    h = os.pread(fd, 64, 0)
    require(len(h) == 64 and h[:7] == b"\x7fELF\x02\x01\x01"
            and int.from_bytes(h[16:18], "little") in allowed_types
            and h[18:20] == b"\x3e\x00", "elfHeader")


def proc_bytes(path, limit):
    fd = checked(os.open(path, flags()))
    try:
        return read_bounded(fd, limit)
    finally:
        os.close(fd)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def pin(value):
    return type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None


def inode(info):
    return os.major(info.st_dev), os.minor(info.st_dev), info.st_ino


def protected(path):
    require(path.is_absolute() and path.resolve(strict=True) == path, "runtimePath")
    for directory in path.parents:
        st = directory.stat()
        require(st.st_uid == 0 and not st.st_mode & 0o022, "runtimeDirectory")


def runtime_file(entry, executable=False):
    path = Path(entry["path"])
    require(re.fullmatch(r"/[a-zA-Z0-9_./+-]+", str(path)) is not None, "runtimePath")
    protected(path)
    fd = checked(os.open(path, flags()))
    try:
        st = os.fstat(fd)
        require(
            stat.S_ISREG(st.st_mode) and st.st_uid == 0 and not st.st_mode & 0o7022
            and (not executable or bool(st.st_mode & 0o100))
            and type(entry["size"]) is int and 0 < st.st_size == entry["size"] <= ELF_CAP
            and pin(entry["sha256"]), "runtimeFile",
        )
        if executable:
            try:
                os.getxattr(fd, "security.capability")
            except OSError as error:
                require(error.errno in (errno.ENODATA, errno.ENOTSUP), "fileCapabilities")
            else:
                raise Refused("fileCapabilities")
        elf_header(fd, {2, 3} if executable else {3})
        with os.fdopen(fd, "rb", closefd=False) as source:
            require(hashlib.file_digest(source, "sha256").hexdigest() == entry["sha256"],
                    "runtimeDigest")
        require(inode(path.stat()) == inode(st), "runtimeChanged")
        return path, inode(st)
    finally:
        os.close(fd)


def active_fds():
    # Listing /proc/self/fd also lists the transient enumeration handle(s).
    # This program is single-threaded, so ignore only descriptors now EBADF.
    directory = checked(os.open("/proc/self/fd", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC))
    try:
        names = os.listdir(directory)
    finally:
        os.close(directory)
    active = set()
    for name in names:
        fd = int(name)
        require(fits(fd), "descriptorRange")
        try:
            fcntl.fcntl(fd, fcntl.F_GETFD)
        except OSError as error:
            if error.errno != errno.EBADF:
                raise
        else:
            active.add(fd)
    return active


def read_manifest(path, expected):
    require(pin(expected), "manifestPin")
    require(path.is_absolute(), "manifestPath")
    data = file_bytes(path, SOURCE_CAP)
    require(digest(data) == expected, "manifestDigest")
    manifest = json.loads(data)
    require(manifest["schema"] == SCHEMA and manifest["runtimeApproved"] is True,
            "sourceOnly")
    return manifest


def runtime_manifest(manifest, manifest_path, workspace):
    require(manifest["platform"] == "linux-x86_64-glibc" and
            manifest["bunVersion"] == "1.4.5" and
            manifest["bunSource"] == "a7733435cf40429e3b35c36072769df2052b80f1" and
            pin(manifest["staticElfReviewSha256"]), "runtimePolicy")
    report_bytes = file_bytes(manifest_path.with_name("elf-review.json"), SOURCE_CAP)
    require(digest(report_bytes) == manifest["staticElfReviewSha256"], "elfReviewDigest")
    report = json.loads(report_bytes)
    require(report["schema"] == "braid.native-bun-fixture.elf-review.v1" and
            report["disposition"] == "admitted" and
            report["bunSha256"] == manifest["bun"]["sha256"] and
            report["loaderSha256"] == manifest["loader"]["sha256"] and
            report["supervisorInterpreterSha256"] == manifest["supervisorInterpreter"]["sha256"]
            and report["librarySha256"] == manifest["library"]["sha256"]
            and report["dependencies"] == manifest["dependencies"], "elfReviewBinding")
    sources = {}
    for name in ("fixture.c", "worker.mjs", "supervisor.py"):
        data = file_bytes(HERE / name, SOURCE_CAP)
        require(digest(data) == manifest["sourceFiles"][name], "sourceDigest")
        sources[name] = data
    require(not os.path.lexists("/etc/ld.so.preload"), "globalPreload")
    interpreter, _ = runtime_file(manifest["supervisorInterpreter"], executable=True)
    require(interpreter == Path(sys.executable), "interpreterPath")
    bun, bun_inode = runtime_file(manifest["bun"], executable=True)
    loader, loader_inode = runtime_file(manifest["loader"], executable=True)
    require(loader == Path("/usr/lib/ld-linux-x86-64.so.2"), "loaderPath")
    native = {str(bun): bun_inode, str(loader): loader_inode}
    for entry in manifest["dependencies"]:
        path, value = runtime_file(entry)
        require(path.parent == Path("/usr/lib") and str(path) not in native, "dependencyPath")
        # A SONAME alias, if needed, must resolve to exactly the approved file.
        soname = entry["soname"]
        require(type(soname) is str and re.fullmatch(r"[a-zA-Z0-9_.+-]+", soname)
                is not None and (path.parent / soname).resolve(strict=True) == path,
                "dependencyAlias")
        native[str(path)] = value
    require(str(Path("/usr/lib/libc.so.6")) in native, "libcMissing")

    artifact = manifest["library"]
    require(artifact["file"] == "libbraid_fixture_v1.so" and pin(artifact["sha256"]),
            "libraryManifest")
    payload = file_bytes(workspace / artifact["file"], LIB_CAP)
    require(0 < len(payload) == artifact["size"] and
            digest(payload) == artifact["sha256"], "libraryDigest")
    return bun, loader, native, sources["worker.mjs"], payload, artifact["sha256"]


def validate_host():
    require(sys.platform == "linux" and platform.machine() == "x86_64" and
            sys.implementation.name == "cpython" and sys.version_info[:3] == (3, 15, 2)
            and sys.flags.isolated and sys.flags.no_site and sys.flags.dont_write_bytecode,
            "hostRuntime")
    require(os.getuid() == os.geteuid() != 0 and os.getgid() == os.getegid(), "notUnprivileged")
    require(threading.active_count() == 1 and
            signal.getsignal(signal.SIGCHLD) == signal.SIG_DFL and
            signal.getsignal(signal.SIGPIPE) == signal.SIG_IGN, "reaperPolicy")
    status = proc_bytes("/proc/self/status", SOURCE_CAP)
    for key in (b"CapInh", b"CapPrm", b"CapEff", b"CapAmb"):
        require(key + b":" in status and
                re.search(rb"(?m)^" + key + rb":\s+0+$", status) is not None, "capabilities")
    soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
    require(4 < soft <= hard <= MAX_FD + 1, "descriptorLimit")
    require(active_fds() == {0, 1, 2}, "unexpectedInheritance")
    return hard


def validate_workspace(workspace):
    require(workspace.is_absolute() and workspace.resolve(strict=True) == workspace, "workPath")
    st = workspace.stat()
    require(stat.S_ISDIR(st.st_mode) and st.st_uid == os.getuid() and
            stat.S_IMODE(st.st_mode) == 0o700 and
            set(os.listdir(workspace)) == {"libbraid_fixture_v1.so"}, "workDirectory")
    require(hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal"), "pidfdUnavailable")
    os.close(checked(os.pidfd_open(os.getpid())))


def maps_match(pid, native, library=None):
    expected = dict(native)
    if library is not None:
        expected[MARKER] = library
    mapped, executable = set(), set()
    data = proc_bytes(f"/proc/{pid}/maps", 1 << 20)
    for row in data.decode("ascii").splitlines():
        fields = row.split(maxsplit=5)
        require(len(fields) >= 5, "mappingFormat")
        if fields[4] == "0":
            # Kernel special entries, JSC/allocator anonymous memory and stack.
            require(len(fields) == 5 or fields[5].startswith("["), "mappingFormat")
            continue
        require(len(fields) == 6 and fields[5] in expected, "unexpectedMapping")
        path = fields[5]
        major, minor = (int(part, 16) for part in fields[3].split(":"))
        require((major, minor, int(fields[4])) == expected[path], "mappingIdentity")
        mapped.add(path)
        if "x" in fields[1]:
            executable.add(path)
    require(mapped == expected.keys() and executable == expected.keys(), "missingMapping")


def in_two_fd_poll(pid):
    # Linux x86-64: poll(7) and ppoll(271); argument 1 is nfds.
    # A W frame alone, /proc state, or a timer is not proof of a native wait.
    fields = proc_bytes(f"/proc/{pid}/task/{pid}/syscall", 512).split()
    return (len(fields) == 9 and fields[0] in (b"7", b"271")
            and int(fields[2], 0) == 2)


def request_stop(_number, _frame):
    global STOP
    STOP = True


class Owner:
    def __init__(self):
        self.fds = {}
        self.pid = None
        self.status = None
        self.data = bytearray()
        self.failure = None
        self.eof = False
        self.phase = "admit"
        self.observed = False
        self.cancel_at = None
        self.stop_at = None
        self.term = False
        self.kill = False
        self.overdue = False
        self.end = None

    def own(self, name, fd):
        self.fds[name] = checked(fd)

    def close(self, name):
        fd = self.fds.pop(name, None)
        if fd is not None:
            try:
                os.close(fd)
            except OSError:
                self.fail("closeFailure")

    def close_all(self):
        for name in tuple(self.fds):
            self.close(name)

    def fail(self, reason):
        self.failure = self.failure or reason

    def socketpair(self, parent, child):
        left, right = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM |
                                        socket.SOCK_NONBLOCK | socket.SOCK_CLOEXEC)
        try:
            require(fits(left.fileno()) and fits(right.fileno()), "descriptorRange")
            self.own(parent, left.detach())
            self.own(child, right.detach())
        finally:
            left.close()
            right.close()

    def make_artifact(self, payload):
        self.own("artifact", os.memfd_create("braid-fixture-v1",
                                            os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING))
        fd = self.fds["artifact"]
        rest = memoryview(payload)
        while rest:
            count = os.write(fd, rest)
            require(count > 0, "sealWrite")
            rest = rest[count:]
        fcntl.fcntl(fd, fcntl.F_ADD_SEALS, SEALS)
        require(fcntl.fcntl(fd, fcntl.F_GET_SEALS) == SEALS and
                os.pread(fd, len(payload) + 1, 0) == payload, "sealedBytes")
        elf_header(fd, {3})
        return inode(os.fstat(fd))

    def launch(self, hard, argv, env, workspace):
        self.socketpair("control", "childIn")
        self.socketpair("output", "childOut")
        self.own("null", os.open("/dev/null", os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC))
        info = os.fstat(self.fds["null"])
        require(stat.S_ISCHR(info.st_mode) and
                (os.major(info.st_rdev), os.minor(info.st_rdev)) == (1, 3), "devNull")
        require(active_fds() == {0, 1, 2, *self.fds.values()}, "unexpectedInheritance")
        require(not STOP, "cancelled")
        self.end = time.monotonic() + RUN  # Before fork; success includes all closes.
        pid = os.fork()
        if pid == 0:
            try:
                os.setsid()
                for sig in SIGNALS:
                    signal.signal(sig, signal.SIG_DFL)
                # Move all sources away from target slots before touching 0..3.
                copies = [checked(fcntl.fcntl(self.fds[name], fcntl.F_DUPFD_CLOEXEC, 4))
                          for name in ("childIn", "childOut", "null", "artifact")]
                for target, fd in enumerate(copies):
                    require(os.dup2(fd, target, inheritable=True) == target, "dupSlot")
                # Inherited set was exact before fork. All copies are below hard.
                os.closerange(4, hard)
                require(active_fds() == {0, 1, 2, 3}, "childInheritance")
                os.chdir(workspace)
                os.execve(argv[0], argv, env)
            except BaseException:
                os._exit(4)
        self.pid = pid
        for name in ("childIn", "childOut", "null"):
            self.close(name)  # Parent never retains child endpoints (EOF would be false).
        self.own("pidfd", os.pidfd_open(pid))

    def reap_once(self):
        if self.pid is None or self.status is not None:
            return
        # Exactly one reaper; neither EOF nor a readable pidfd changes status.
        waited, status = os.waitpid(self.pid, os.WNOHANG)
        if waited == self.pid:
            self.check_stop_bound(time.monotonic())
            self.status = status

    def control(self, byte):
        return os.write(self.fds["control"], byte) == 1

    def collect(self):
        if self.eof:
            return
        try:
            chunk = os.read(self.fds["output"], 12 - len(self.data))
        except BlockingIOError:
            return
        if not chunk:
            self.eof = True
        else:
            self.data.extend(chunk)
            require(len(self.data) <= 11, "outputLimit")  # A\n L\n [W\n] R + i32

    def pause(self, seconds=TICK):
        watch = []
        if self.status is None and "pidfd" in self.fds:
            watch.append(self.fds["pidfd"])
        if not self.eof and "output" in self.fds:
            watch.append(self.fds["output"])
        # Linux select(2) fd_set would impose its own narrower limit.
        poller = select.poll()
        for fd in watch:
            poller.register(fd, select.POLLIN)
        poller.poll(max(1, int(seconds * 1000)))

    def send_signal(self, number):
        if self.status is None and "pidfd" in self.fds:
            try:
                signal.pidfd_send_signal(self.fds["pidfd"], number)
            except ProcessLookupError:
                pass  # Still need waitpid.
            except OSError:
                self.fail("signalFailed")

    def begin_stop(self, now):
        if self.stop_at is None:
            self.stop_at = now

    def check_stop_bound(self, now):
        if (self.stop_at is not None and not self.overdue and
                now - self.stop_at >= COOPERATE + TERMINATE + REAP):
            self.overdue = True
            self.fail("workerStopUnconfirmed")
            # Keep custody, pidfd and channels. Never block on a reporting pipe
            # while the only reaper still owns a live or unreaped child.

    def settle_step(self, now):
        if self.stop_at is None or self.status is not None:
            return
        self.check_stop_bound(now)
        elapsed = now - self.stop_at
        if elapsed >= COOPERATE + TERMINATE and not self.kill:
            self.kill = True
            self.send_signal(signal.SIGKILL)
        elif elapsed >= COOPERATE and not self.term and not self.kill:
            self.term = True
            self.send_signal(signal.SIGTERM)

    def cancel_for_failure(self):
        if self.phase == "query" and self.cancel_at is None:
            try:
                if self.control(b"C"):
                    self.cancel_at = time.monotonic()
            except OSError:
                pass  # The failure and pidfd settlement remain authoritative.

    def checkpoints(self):
        if STOP:
            self.fail("cancelled")
        if time.monotonic() >= self.end:
            self.fail("deadline")

    def supervise(self, native, library, hold):
        proof_end = None
        normal_prefix = b"A\nL\n"
        prefix = normal_prefix + (b"W\n" if hold else b"")
        while True:
            self.checkpoints()
            self.reap_once()
            self.collect()
            now = time.monotonic()
            fixed = prefix + b"R"
            require(bytes(self.data[:len(fixed)]) == fixed[:len(self.data)], "frameProtocol")
            if self.failure is None and self.status is None:
                if self.phase == "admit" and len(self.data) >= 2:
                    require(self.data == b"A\n" and os.getsid(self.pid) == self.pid and
                            os.getpgid(self.pid) == self.pid, "childIsolation")
                    maps_match(self.pid, native)
                    self.checkpoints()
                    if self.failure is None:
                        require(self.control(b"G"), "permitWrite")
                        self.phase = "load"
                elif self.phase == "load" and len(self.data) >= 4:
                    require(self.data == normal_prefix, "frameProtocol")
                    maps_match(self.pid, native, library)
                    self.checkpoints()
                    if self.failure is None:
                        require(self.control(b"Q"), "permitWrite")
                        self.phase = "query"
                elif hold and self.phase == "query" and len(self.data) >= 6 and self.cancel_at is None:
                    require(self.data == prefix, "nativeUnproven")
                    proof_end = proof_end or (now + PROOF)
                    require(now < proof_end, "nativeUnproven")
                    try:
                        blocked = in_two_fd_poll(self.pid)
                    except (OSError, ValueError) as error:
                        raise Refused("nativeUnproven") from error
                    if blocked:
                        self.checkpoints()
                        if self.failure is None:
                            require(time.monotonic() < proof_end and self.control(b"C"),
                                    "nativeUnproven")
                            self.observed = True
                            self.cancel_at = time.monotonic()
                            self.begin_stop(self.cancel_at)
            if self.failure is not None and self.status is None:
                self.begin_stop(now)
                self.cancel_for_failure()
            self.settle_step(time.monotonic())
            if self.status is not None and self.eof:
                break
            if self.status is not None and self.failure is not None:
                break  # Failed run; EOF can never be used in lieu of reaping.
            self.pause()

        self.checkpoints()
        if self.failure is not None:
            return
        require(self.phase == "query" and self.status is not None and self.eof and
                len(self.data) == len(prefix) + 5 and os.WIFEXITED(self.status), "workerResult")
        result = int.from_bytes(self.data[-4:], "little", signed=True)
        if hold:
            require(self.observed and self.cancel_at is not None and not self.term and not self.kill
                    and time.monotonic() - self.cancel_at < COOPERATE
                    and result == -3 and os.WEXITSTATUS(self.status) == 3, "nativeUnproven")
        else:
            require(result == 42 and os.WEXITSTATUS(self.status) == 0, "workerResult")

    def retain_until_reaped(self):
        if self.pid is None or self.status is not None:
            return
        self.begin_stop(time.monotonic())
        self.cancel_for_failure()
        while self.status is None:
            try:
                self.reap_once()
            except OSError:
                # An unavailable wait status is not settlement. Under the
                # single-reaper policy ECHILD is not expected; fail closed.
                self.fail("waitStatusUnavailable")
            self.settle_step(time.monotonic())
            if self.status is None:
                time.sleep(TICK if not self.overdue else 0.050)


def run(args):
    owner = Owner()
    old = {sig: signal.signal(sig, request_stop) for sig in SIGNALS}
    previous_mask = None
    try:
        hard = validate_host()
        manifest = read_manifest(args.manifest, args.approved_manifest_sha256)
        validate_workspace(args.work)
        bun, loader, native, worker, payload, sha = runtime_manifest(
            manifest, args.manifest, args.work)
        library = owner.make_artifact(payload)
        config = {"mode": int(args.case == "hold"), "size": len(payload), "sha256": sha}
        source = "const fixture=Object.freeze(" + json.dumps(config) + ");\n" + worker.decode("utf-8")
        argv = [str(loader), "--inhibit-cache", "--glibc-hwcaps-mask", "", "--library-path",
                "/usr/lib", "--argv0", str(bun), str(bun), "--config", "/dev/null",
                "--no-env-file", "--no-install", "--eval", source]
        env = {"HOME": str(args.work), "XDG_CONFIG_HOME": str(args.work),
               "TMPDIR": str(args.work), "LANG": "C", "TZ": "UTC", "BUN_NO_TELEMETRY": "1"}
        owner.launch(hard, argv, env, args.work)
        owner.supervise(native, library, args.case == "hold")
    except Refused as error:
        owner.fail(str(error))
    except (OSError, RuntimeError, ValueError, KeyError, TypeError):
        owner.fail("fixtureFailure")
    finally:
        # A missed stop bound fails promptly, but never abandons process custody.
        owner.retain_until_reaped()
        owner.close_all()
        previous_mask = signal.pthread_sigmask(signal.SIG_BLOCK, SIGNALS)
        if STOP or signal.sigpending().intersection(SIGNALS):
            owner.fail("cancelled")
        if owner.end is not None and time.monotonic() >= owner.end:
            owner.fail("deadline")
    try:
        reason = "workerStopUnconfirmed" if owner.overdue else owner.failure
        if args.case == "hold" and owner.pid is not None and reason is not None:
            # Preserve the failure reason without claiming native cancellation.
            result = {"outcome": "unproven", "reason": reason}
        elif reason is not None:
            result = {"outcome": "refused", "reason": reason}
        else:
            result = {"outcome": "fixtureOnly", "case": args.case, "workerReaped": True}
        print(json.dumps(result, sort_keys=True), flush=True)
        return int(reason is not None)
    finally:
        signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)
        for sig, handler in old.items():
            signal.signal(sig, handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--approved-manifest-sha256", required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--case", choices=("reply", "hold"), required=True)
    return run(parser.parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
