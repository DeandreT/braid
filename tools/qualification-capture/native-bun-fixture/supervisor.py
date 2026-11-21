"""Standalone Braid #4 source candidate; never a capture driver."""

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
import struct
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
EXEC_GATE = 0.750
COOPERATE = 0.250
TERMINATE = 0.250
REAP = 1.0
TICK = 0.005
MARKER = "/memfd:braid-fixture-v1 (deleted)"
# Linux UAPI: linux/fcntl.h and asm-generic/fcntl.h. CPython may omit
# some or all of these names; a conflicting export is not admissible.
SEAL_UAPI = {
    "F_ADD_SEALS": 1033,
    "F_GET_SEALS": 1034,
    "F_SEAL_SEAL": 0x01,
    "F_SEAL_SHRINK": 0x02,
    "F_SEAL_GROW": 0x04,
    "F_SEAL_WRITE": 0x08,
}
SEAL_MASK = 0x0f
SIGNALS = (signal.SIGINT, signal.SIGTERM)
PERMITS = {
    b"P": ("gate", "preexec", "admit"),
    b"G": ("control", "admit", "load"),
    b"Q": ("control", "load", "query"),
}
CHILD_GATE_EXIT = 20
CHILD_SETUP_EXIT = 21
CHILD_EXEC_EXIT = 22
EXIT_HINTS = {
    CHILD_GATE_EXIT: (0, "execGate"),
    CHILD_SETUP_EXIT: (1, "preexecSetup"),
    CHILD_EXEC_EXIT: (1, "execSyscall"),
    # Worker exit hints are supplemental; they do not admit a frame or permit.
    40: (1, "workerIdentity"),
    41: (1, "workerStdio"),
    42: (1, "artifactDescriptor"),
    43: (1, "artifactBytes"),
    44: (1, "admissionFrame"),
    45: (1, "loadPermit"),
    46: (2, "ffiImport"),
    47: (2, "libraryOpen"),
    48: (2, "loadFrame"),
    49: (2, "queryPermit"),
    50: (3, "abiCheck"),
    51: (3, "nativeQuery"),
    52: (3, "resultFrame"),
}
NATIVE_RESULTS = {
    42: "reply", -1: "badArgument", -2: "badDescriptor", -3: "cancelled",
    -4: "deadline", -5: "ioFailure", -6: "closeFailure",
}
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
    for name in ("fixture.c", "worker.mjs"):
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


def untraced(status):
    require(re.search(rb"(?m)^TracerPid:[ \t]+0$", status) is not None, "tracedLaunch")


def launch_policy(status):
    untraced(status)
    require(not set(SIGNALS).intersection(signal.pthread_sigmask(signal.SIG_BLOCK, ())),
            "blockedCancellationSignals")


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
    launch_policy(status)
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


def await_exec_permit(fd, end):
    require(fits(fd), "descriptorRange")
    poller = select.poll()
    poller.register(fd, select.POLLIN)
    while (remaining := end - time.monotonic()) > 0:
        if not poller.poll(max(1, int(remaining * 1000))):
            continue
        try:
            byte = os.read(fd, 1)
        except BlockingIOError:
            continue
        # EOF or any unexpected byte means refuse, including a late permit.
        return byte == b"P" and time.monotonic() < end
    return False


def memfd_seal_commands():
    require(sys.platform == "linux" and platform.machine() == "x86_64" and
            sys.byteorder == "little" and
            all(struct.calcsize("@" + kind) == width
                for kind, width in (("i", 4), ("l", 8), ("P", 8))),
            "memfdSealAbi")
    for name, value in SEAL_UAPI.items():
        exposed = getattr(fcntl, name, value)
        require(type(exposed) is int and exposed == value, "memfdSealConstantMismatch")
    return SEAL_UAPI["F_ADD_SEALS"], SEAL_UAPI["F_GET_SEALS"]


def frame_prefix(hold):
    return b"A\nL\nW\n" if hold else b"A\nL\n"


class Owner:
    def __init__(self):
        self.fds = {}
        self.gate_writer = None
        self.pid = None
        self.status = None
        self.data = bytearray()
        self.failure = None
        self.eof = False
        self.phase = "preexec"
        # Only mutated in the forked Python child; successful exec never returns.
        self.preexec_failure = CHILD_GATE_EXIT
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
                if name == "gate":
                    self.gate_writer.detach()
                    self.gate_writer = None
                os.close(fd)
            except OSError:
                self.fail("closeFailure")

    def close_all(self):
        require(self.pid is None or self.status is not None, "childNotReaped")
        for name in tuple(self.fds):
            self.close(name)

    def fail(self, reason):
        self.failure = self.failure or reason

    def socketpair(self, parent, child):
        left, right = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM |
                                        socket.SOCK_NONBLOCK | socket.SOCK_CLOEXEC)
        try:
            require(fits(left.fileno()) and fits(right.fileno()), "descriptorRange")
            if parent == "gate":
                # Keep the original socket object for shutdown on a refusal.
                # No second wrapper briefly owning the same fd.
                self.own(parent, left.fileno())
                self.gate_writer, left = left, None
            else:
                self.own(parent, left.detach())
            self.own(child, right.detach())
        finally:
            if left is not None:
                left.close()
            right.close()

    def seal_artifact(self, payload):
        add, get = memfd_seal_commands()
        self.own("artifact", os.memfd_create("braid-fixture-v1",
                                            os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING))
        fd = self.fds["artifact"]
        rest = memoryview(payload)
        while rest:
            count = os.write(fd, rest)
            require(count > 0, "sealWrite")
            rest = rest[count:]
        require(fcntl.fcntl(fd, add, SEAL_MASK) == 0, "sealApply")
        require(fcntl.fcntl(fd, get) == SEAL_MASK, "sealMask")
        require(os.pread(fd, len(payload) + 1, 0) == payload, "sealedBytes")
        return fd

    def make_artifact(self, payload):
        fd = self.seal_artifact(payload)
        elf_header(fd, {3})
        return inode(os.fstat(fd))

    def child_exec(self, hard, argv, env, workspace, gate_end):
        # Drop this process's writer copy before waiting, so parent loss also
        # produces EOF. No loader, slot installation, or Bun before a permit.
        os.close(self.gate_writer.detach())
        gate = self.fds["childGate"]
        require(await_exec_permit(gate, gate_end), "execNotPermitted")
        self.preexec_failure = CHILD_SETUP_EXIT
        os.close(gate)
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
        launch_policy(proc_bytes("/proc/self/status", SOURCE_CAP))
        require(time.monotonic() < gate_end, "execNotPermitted")
        self.preexec_failure = CHILD_EXEC_EXIT
        os.execve(argv[0], argv, env)

    def abort_exec_gate(self):
        # Publish EOF without surrendering the owned fd. Even if shutdown
        # fails, the child's absolute gate cutoff does not depend on us.
        try:
            self.gate_writer.shutdown(socket.SHUT_WR)
        except OSError:
            self.fail("execGateFailure")

    def launch(self, hard, argv, env, workspace):
        self.socketpair("control", "childIn")
        self.socketpair("output", "childOut")
        self.socketpair("gate", "childGate")
        self.own("null", os.open("/dev/null", os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC))
        info = os.fstat(self.fds["null"])
        require(stat.S_ISCHR(info.st_mode) and
                (os.major(info.st_rdev), os.minor(info.st_rdev)) == (1, 3), "devNull")
        require(active_fds() == {0, 1, 2, *self.fds.values()}, "unexpectedInheritance")
        launch_policy(proc_bytes("/proc/self/status", SOURCE_CAP))
        require(not STOP, "cancelled")
        start = time.monotonic()
        self.end = start + RUN  # Before fork; success includes all closes.
        gate_end = min(start + EXEC_GATE, self.end)
        pid = os.fork()
        if pid == 0:
            try:
                self.child_exec(hard, argv, env, workspace, gate_end)
            finally:
                # Do not unwind into the parent's reaper or interpreter teardown.
                os._exit(self.preexec_failure)
        self.pid = pid
        try:
            try:
                self.own("pidfd", os.pidfd_open(pid))
            except OSError as error:
                raise Refused("pidfdUnavailable") from error
            for name in ("childIn", "childOut", "childGate", "null"):
                self.close(name)  # Parent never retains the worker output peer.
            require(self.commit_permit(b"P", gate_end=gate_end),
                    self.failure or "execNotPermitted")
        finally:
            if self.phase == "preexec":
                self.abort_exec_gate()

    def reap_once(self):
        if self.pid is None or self.status is not None:
            return
        # Exactly one reaper; neither EOF nor a readable pidfd changes status.
        waited, status = os.waitpid(self.pid, os.WNOHANG)
        if waited == self.pid:
            if not (os.WIFEXITED(status) or os.WIFSIGNALED(status)):
                self.fail("nonterminalWaitStatus")
                return
            self.check_stop_bound(time.monotonic())
            self.status = status

    def latch_pending_cancel(self):
        if signal.sigpending().intersection(SIGNALS) or STOP:
            self.fail("cancelled")

    def commit_permit(self, permit, *, gate_end=None):
        """Decide once under the mask; a later signal cannot retract a byte."""
        channel, before, after = PERMITS[permit]
        require(self.phase == before and "pidfd" in self.fds, "permitOrder")
        require((permit == b"P") == (gate_end is not None), "permitDeadline")
        fd = self.fds[channel]  # All permit sockets are nonblocking.
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, SIGNALS)
        try:
            # This snapshot is the cancellation commit point. An arrival
            # after it may not suppress this one write but must bar later work.
            self.latch_pending_cancel()
            now = time.monotonic()
            if now >= self.end:
                self.fail("deadline")
            elif gate_end is not None and now >= gate_end:
                self.fail("execNotPermitted")
            if self.failure is not None:
                return False
            require(os.write(fd, permit) == 1, "permitWrite")
            self.phase = after
            return True
        finally:
            try:
                self.latch_pending_cancel()
            finally:
                signal.pthread_sigmask(signal.SIG_SETMASK, previous)
            self.checkpoints()  # Includes cancellation delivered on unmask.

    def write_cancel(self):
        return os.write(self.fds["control"], b"C") == 1

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
                if self.write_cancel():
                    self.cancel_at = time.monotonic()
            except OSError:
                pass  # The failure and pidfd settlement remain authoritative.

    def checkpoints(self):
        if STOP:
            self.fail("cancelled")
        if time.monotonic() >= self.end:
            self.fail("deadline")

    def worker_diagnostic(self, hold):
        """One bounded receipt from owned state, never raw worker output."""
        prefix = frame_prefix(hold)
        issued, permit = {"preexec": (0, None), "admit": (1, "P"),
                          "load": (2, "G"), "query": (3, "Q")}[self.phase]
        # Terminal/EOF draining can read bytes sent without a matching grant.
        # Keep their count but never interpret them as progress past the gate.
        max_bytes = issued * 2 if issued < 3 else len(prefix) + 5
        fixed = prefix + b"R"
        contradiction = (len(self.data) > max_bytes or
                         self.data[:len(fixed)] != fixed[:len(self.data)])
        last_frame = None
        native_result = None
        for index, marker in enumerate(("A", "L", "W") if hold else ("A", "L"), 1):
            if issued >= index and self.data.startswith(prefix[:index * 2]):
                last_frame = marker
        if issued == 3 and not contradiction and len(self.data) == len(prefix) + 5:
            last_frame = "R"
            result = int.from_bytes(self.data[-4:], "little", signed=True)
            native_result = NATIVE_RESULTS.get(result, "unexpected")
        exit_code = exit_signal = None
        if self.status is not None:
            if os.WIFEXITED(self.status):
                exit_code = os.WEXITSTATUS(self.status)
            elif os.WIFSIGNALED(self.status):
                exit_signal = os.WTERMSIG(self.status)
        minimum, exit_hint = EXIT_HINTS.get(exit_code, (0, None))
        return {
            "lastPermit": permit,
            "lastFrame": last_frame,
            "frameBytes": min(len(self.data), 12),
            "eof": self.eof,
            "exitCode": exit_code,
            "exitSignal": exit_signal,
            "exitHint": exit_hint if issued >= minimum else None,
            "nativeResult": native_result,
            "protocolContradiction": contradiction,
        }

    def outcome(self, case):
        reason = "workerStopUnconfirmed" if self.overdue else self.failure
        if reason is None:
            return 0, {"outcome": "fixtureOnly", "case": case, "workerReaped": True}
        result = {"outcome": "unproven" if case == "hold" and self.pid is not None else "refused",
                  "reason": reason}
        if self.pid is not None:
            result["worker"] = self.worker_diagnostic(case == "hold")
        return 1, result

    def supervise(self, native, library, hold):
        proof_end = None
        normal_prefix = b"A\nL\n"
        prefix = frame_prefix(hold)
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
                    untraced(proc_bytes(f"/proc/{self.pid}/status", SOURCE_CAP))
                    maps_match(self.pid, native)
                    self.commit_permit(b"G")
                elif self.phase == "load" and len(self.data) >= 4:
                    require(self.data == normal_prefix, "frameProtocol")
                    maps_match(self.pid, native, library)
                    self.commit_permit(b"Q")
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
                            require(time.monotonic() < proof_end and self.write_cancel(),
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


def worker_argv(loader, bun, source):
    # Bun 1.4.5's optional --config value must be joined with "=".
    # A separate /dev/null token is positional and stops parsing before --eval.
    return [str(loader), "--inhibit-cache", "--glibc-hwcaps-mask", "", "--library-path",
            "/usr/lib", "--argv0", str(bun), str(bun), "--config=/dev/null",
            "--no-env-file", "--no-install", "--eval", source]


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
        env = {"HOME": str(args.work), "XDG_CONFIG_HOME": str(args.work),
               "TMPDIR": str(args.work), "LANG": "C", "TZ": "UTC", "BUN_NO_TELEMETRY": "1"}
        owner.launch(hard, worker_argv(loader, bun, source), env, args.work)
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
        code, result = owner.outcome(args.case)
        print(json.dumps(result, sort_keys=True), flush=True)
        return code
    finally:
        signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)
        for sig, handler in old.items():
            signal.signal(sig, handler)


if __name__ == "__main__":
    raise SystemExit("Use the independently admitted protected bootstrap.")
