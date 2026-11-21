"""Own one qualification capture: drive an observer, then record a durable terminal outcome.

A capture is <root>/<captureId>/ with header.json, evidence/ (the driver's --output),
driver.stdout, manifest.json and the no-replace terminal markers complete.exit and
complete.revoked. See docs/qualification-capture.md.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import select
import signal
import stat
import subprocess
import sys
from time import monotonic
import uuid

SCHEMA = 'braid.qualification-capture.v1'
HEADER, MANIFEST, STDOUT, EVIDENCE = 'header.json', 'manifest.json', 'driver.stdout', 'evidence'
EXIT, REVOKED = 'complete.exit', 'complete.revoked'
LAYOUT = {HEADER, MANIFEST, STDOUT, EVIDENCE, EXIT, REVOKED}
SMALL_LIMIT, STDOUT_LIMIT, MEMBER_LIMIT, MEMBER_COUNT = 65536, 1 << 20, 64 << 20, 256
GRACE_SECONDS, POLL_SECONDS = 5.0, 0.05
NOFOLLOW = getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_BINARY', 0)
READ_FLAGS = os.O_RDONLY | NOFOLLOW


class Refusal(Exception):
    """A categorized refusal; before capture creation nothing is recorded."""


class CaptureInvalid(Exception):
    """A recorded capture violates the protocol or no longer matches its binding."""


def canonical(value) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n').encode()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sync_directory(path: Path) -> None:
    if os.name == 'posix':
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def write_new(path: Path, data: bytes, *, before_link=None) -> None:
    """Durably publish complete bytes at path or raise FileExistsError; never replaces."""
    temporary = path.with_name(f'.{path.name}.{uuid.uuid4().hex}.tmp')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | NOFOLLOW, 0o600)
    try:
        try:
            view = memoryview(data)
            while view:
                view = view[os.write(fd, view):]
            os.fsync(fd)
        finally:
            os.close(fd)
        if before_link is not None and (failure := before_link()) is not None:
            raise Refusal(failure)
        os.link(temporary, path)  # atomic no-replace: EEXIST when any earlier attempt won
    finally:
        os.unlink(temporary)
    sync_directory(path.parent)


def close_all(fds: list[int]) -> None:
    """Close every descriptor even after one close fails, then report the first failure."""
    error = None
    while fds:
        try:
            os.close(fds.pop())
        except OSError as failure:
            error = error or failure
    if error is not None:
        raise error


def read_fd(fd: int, limit: int) -> bytes:
    chunks, size = [], 0
    while chunk := os.read(fd, 65536):
        size += len(chunk)
        if size > limit:
            raise CaptureInvalid('memberLimit')
        chunks.append(chunk)
    return b''.join(chunks)


def open_regular(path: Path, fds: list[int]) -> tuple[int, int]:
    facts = os.lstat(path)
    if not stat.S_ISREG(facts.st_mode):
        raise CaptureInvalid('memberNotRegular')
    fd = os.open(path, READ_FLAGS)
    fds.append(fd)
    opened = os.fstat(fd)
    if not stat.S_ISREG(opened.st_mode) or (opened.st_dev, opened.st_ino) != (facts.st_dev, facts.st_ino):
        raise CaptureInvalid('memberChangedDuringAdmission')
    return fd, opened.st_size


def read_small(path: Path) -> bytes:
    fds: list[int] = []
    try:
        return read_fd(open_regular(path, fds)[0], SMALL_LIMIT)
    finally:
        close_all(fds)


def parse(data: bytes) -> dict:
    try:
        value = json.loads(data)
    except ValueError:
        raise CaptureInvalid('invalidJson') from None
    if not isinstance(value, dict) or canonical(value) != data:
        raise CaptureInvalid('nonCanonicalRecord')
    return value


def read_capture(path, golden: dict | None = None) -> dict:
    """Replay one historical capture; raise CaptureInvalid unless its record is consistent."""
    try:
        return read_recorded(Path(path), golden)
    except OSError as error:
        raise CaptureInvalid('unreadableCapture') from error


def read_recorded(path: Path, golden: dict | None) -> dict:
    if not stat.S_ISDIR(os.lstat(path).st_mode):
        raise CaptureInvalid('captureNotDirectory')
    entries = set(os.listdir(path))
    if not entries <= LAYOUT:
        raise CaptureInvalid('unexpectedLayoutEntry')
    header_bytes = read_small(path / HEADER)
    header = parse(header_bytes)
    if header.get('schemaVersion') != SCHEMA or header.get('captureId') != path.name:
        raise CaptureInvalid('headerBindingMismatch')
    revoked = parse(read_small(path / REVOKED)) if REVOKED in entries else None
    exited = parse(read_small(path / EXIT)) if EXIT in entries else None
    members = None
    if revoked is not None:
        if set(revoked) != {'category'} or not isinstance(revoked['category'], str):
            raise CaptureInvalid('invalidRevocation')
        notice = {'outcome': 'revoked', 'category': revoked['category']}
    elif exited is None:
        notice = {'outcome': 'incomplete', 'category': None}
    elif exited.get('exit') != 0:
        raise CaptureInvalid('failureWithoutRevocation')
    elif set(exited) != {'exit', 'headerSha256', 'manifestSha256'} or exited['headerSha256'] != sha256(header_bytes):
        raise CaptureInvalid('terminalBindingMismatch')
    else:
        manifest_bytes = read_small(path / MANIFEST)
        if sha256(manifest_bytes) != exited['manifestSha256']:
            raise CaptureInvalid('terminalBindingMismatch')
        members = replay(path, entries, parse(manifest_bytes))
        notice = {'outcome': 'passed', 'category': None}
    if golden is not None and (golden.get('notice') != notice or golden.get('members') != members):
        raise CaptureInvalid('goldenMismatch')
    return {'captureId': header['captureId'], 'notice': notice, 'members': members}


def replay(path: Path, entries: set[str], manifest: dict) -> dict[str, str]:
    listed = manifest.get('members')
    if set(manifest) != {'members'} or not isinstance(listed, list) or len(listed) > MEMBER_COUNT or not all(
            isinstance(m, dict) and set(m) == {'name', 'size', 'sha256'} for m in listed):
        raise CaptureInvalid('invalidManifest')
    expected = {m['name']: m for m in listed}
    if not stat.S_ISDIR(os.lstat(path / EVIDENCE).st_mode):
        raise CaptureInvalid('evidenceNotDirectory')
    actual = {STDOUT} & entries | {EVIDENCE + '/' + name for name in os.listdir(path / EVIDENCE)}
    if len(expected) != len(listed) or set(expected) != actual:
        raise CaptureInvalid('memberSetMismatch')
    fds: list[int] = []
    try:
        opened = []
        for name in sorted(expected):  # admit the whole member set before reading any member
            fd, size = open_regular(path / name, fds)
            if size != expected[name]['size']:
                raise CaptureInvalid('memberSizeMismatch')
            opened.append((name, fd))
        digests = {name: sha256(read_fd(fd, MEMBER_LIMIT)) for name, fd in opened}
    finally:
        close_all(fds)
    if any(digests[name] != expected[name]['sha256'] for name in digests):
        raise CaptureInvalid('memberDigestMismatch')
    return digests


def hash_file(path: Path) -> str:
    fds: list[int] = []
    try:
        return sha256(read_fd(open_regular(path, fds)[0], MEMBER_LIMIT))
    finally:
        close_all(fds)


def preflight(root: Path, argv, deadline, trusted) -> list[dict]:
    if not isinstance(argv, list) or not argv or not all(isinstance(a, str) and a and '\0' not in a for a in argv):
        raise Refusal('invalidDriverArgv')
    if type(deadline) not in (int, float) or not math.isfinite(deadline) or not 0 < deadline <= 86400:
        raise Refusal('invalidDeadline')
    if not root.is_absolute():
        raise Refusal('invalidCaptureRoot')
    facts = os.lstat(root)
    if not stat.S_ISDIR(facts.st_mode) or facts.st_uid != os.geteuid() or stat.S_IMODE(facts.st_mode) != 0o700:
        raise Refusal('invalidCaptureRoot')
    inventory = []
    for code in [Path(__file__).resolve(), Path(argv[0]), *map(Path, trusted)]:
        facts = os.lstat(code) if code.is_absolute() else None
        if (facts is None or not stat.S_ISREG(facts.st_mode) or facts.st_uid not in (0, os.geteuid())
                or facts.st_mode & 0o022):
            raise Refusal('untrustedCode')
        inventory.append({'path': str(code), 'sha256': hash_file(code)})
    return inventory


def stop(child: subprocess.Popen) -> bool:
    """Request shutdown and report whether the direct child was reaped."""
    for request in (child.terminate, child.kill):
        try:
            request()
        except OSError:
            pass  # The signal request is not proof either way; still try to reap.
        try:
            child.wait(timeout=GRACE_SECONDS)
        except (subprocess.TimeoutExpired, OSError):
            continue
        return True
    return False


def interrupted(end: float, cancelled) -> str | None:
    if cancelled():
        return 'cancelled'
    return 'deadline' if monotonic() >= end else None


@dataclass(frozen=True)
class DriverResult:
    failure: str | None
    output: bytes = b''
    settled: bool = True


def drive(capture: Path, argv: list[str], end: float, cancelled) -> DriverResult:
    """Observe the direct child's exit and stdout EOF under the same deadline."""
    if failure := interrupted(end, cancelled):
        return DriverResult(failure)
    # An ignored SIGCHLD (or a competing reaper) can make Popen synthesize exit 0.
    if signal.getsignal(signal.SIGCHLD) != signal.SIG_DFL:
        return DriverResult('ownerFailure')
    try:
        child = subprocess.Popen([*argv, '--output', str(capture / EVIDENCE)], stdout=subprocess.PIPE,
                                 stderr=subprocess.DEVNULL, close_fds=True)
    except OSError:
        return DriverResult(interrupted(end, cancelled) or 'driverStartFailure')

    output, eof, stdout_end = bytearray(), False, None
    try:
        fd = child.stdout.fileno()
        os.set_blocking(fd, False)
        pipe = select.poll()
        pipe.register(fd, select.POLLIN)
        while not (failure := interrupted(end, cancelled)):
            returncode = child.poll()
            if failure := interrupted(end, cancelled):
                break  # Exit status has no timestamp; observation at or after the cutoff is late.
            if eof and returncode is not None:
                failure = 'driverFailed' if returncode else None
                break
            if returncode is not None and stdout_end is None:
                stdout_end = monotonic() + GRACE_SECONDS
            if stdout_end is not None and monotonic() >= stdout_end:
                failure = interrupted(end, cancelled) or 'outputDidNotSettle'
                break
            wait_end = min(end, stdout_end) if stdout_end is not None else end
            remaining = min(POLL_SECONDS, wait_end - monotonic())
            if remaining <= 0 or not pipe.poll(math.ceil(remaining * 1000)):
                continue
            if failure := interrupted(end, cancelled):
                break
            try:
                chunk = os.read(fd, min(65536, STDOUT_LIMIT + 1 - len(output)))
            except BlockingIOError:
                continue
            if failure := interrupted(end, cancelled):
                break
            if stdout_end is not None and monotonic() >= stdout_end:
                failure = interrupted(end, cancelled) or 'outputDidNotSettle'
                break
            if not chunk:
                eof = True
                pipe.unregister(fd)
            elif len(output) + len(chunk) > STDOUT_LIMIT:
                failure = 'outputLimit'
                break
            else:
                output.extend(chunk)
    except OSError:
        failure = interrupted(end, cancelled) or 'outputDidNotSettle'
    except Exception:
        failure = 'ownerFailure'

    reaped = child.returncode is not None or stop(child)
    closed = False
    try:
        child.stdout.close()
        closed = True
    except OSError:
        failure = failure or 'outputDidNotSettle'
    if not reaped:
        return DriverResult('driverStopUnconfirmed', settled=False)
    return DriverResult(failure or interrupted(end, cancelled), bytes(output), eof and closed)


def close_layout(capture: Path, output: bytes) -> None:
    write_new(capture / STDOUT, output)
    members = [{'name': STDOUT, 'size': len(output), 'sha256': sha256(output)}]
    for name in sorted(os.listdir(capture / EVIDENCE)):
        fds: list[int] = []
        try:
            fd = os.open(capture / EVIDENCE / name, READ_FLAGS | os.O_NONBLOCK)
            fds.append(fd)
            if not stat.S_ISREG(os.fstat(fd).st_mode):
                raise Refusal('evidenceLayoutInvalid')
            data = read_fd(fd, MEMBER_LIMIT)
            os.fsync(fd)
        finally:
            close_all(fds)
        members.append({'name': EVIDENCE + '/' + name, 'size': len(data), 'sha256': sha256(data)})
    if len(members) > MEMBER_COUNT:
        raise Refusal('evidenceLayoutInvalid')
    sync_directory(capture / EVIDENCE)
    write_new(capture / MANIFEST, canonical({'members': members}))


def finish(capture: Path, failure: str | None, *, before_success=None) -> int:
    """The single source-owned terminal: success only when no failure was selected anywhere."""
    if failure is None:
        try:
            write_new(capture / EXIT, canonical({'exit': 0, 'headerSha256': sha256(read_small(capture / HEADER)),
                                                  'manifestSha256': sha256(read_small(capture / MANIFEST))}),
                      before_link=before_success)
            return 0
        except Refusal as error:
            failure = str(error)  # Success was declined before the link.
        except (OSError, CaptureInvalid):
            failure = 'terminalWriteFailure'
    try:
        write_new(capture / REVOKED, canonical({'category': failure}))
    except FileExistsError:
        pass  # an earlier revocation is already authoritative
    try:
        write_new(capture / EXIT, canonical({'category': failure, 'exit': 1}))
    except FileExistsError:
        pass  # an immutable earlier attempt stays; the revocation overrides it
    return 1


def run_capture(root, argv: list[str], deadline: float, trusted=()) -> tuple[int, Path]:
    root = Path(root)
    inventory = preflight(root, argv, deadline, trusted)
    capture_id = str(uuid.uuid4())
    capture = root / capture_id
    os.mkdir(capture, 0o700)
    os.mkdir(capture / EVIDENCE, 0o700)
    write_new(capture / HEADER, canonical({
        'schemaVersion': SCHEMA, 'captureId': capture_id, 'deadlineSeconds': deadline,
        'createdUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'driver': argv,
        'trustedCode': inventory, 'runtime': {'implementation': platform.python_implementation(),
                                             'version': platform.python_version(), 'platform': sys.platform}}))
    cancellation = False

    def cancel(*_):
        nonlocal cancellation
        cancellation = True

    def cancelled():
        return cancellation

    numbers = (signal.SIGINT, signal.SIGTERM)
    previous = {}

    def restore_handlers(numbers_to_restore):
        restored = True
        for number in numbers_to_restore:
            try:
                signal.signal(number, previous.pop(number))
            except Exception:
                restored = False
        return restored

    failure, mask = None, None

    def admit_success():
        pending = (signal.sigpending() & set(numbers)) - mask
        for number in pending:
            signal.sigtimedwait({number}, 0)  # Consume the cancellation we record, not a prior blocked signal.
        interrupted_before_link = 'cancelled' if pending else interrupted(end, cancelled)
        if not restore_handlers(list(previous)):
            return interrupted_before_link or 'ownerFailure'
        return interrupted_before_link or interrupted(end, cancelled)

    try:
        try:
            for number in numbers:
                previous[number] = signal.signal(number, cancel)
            end = monotonic() + deadline
            result = drive(capture, argv, end, cancelled)
            failure = result.failure or interrupted(end, cancelled)
            if result.settled:
                try:
                    close_layout(capture, result.output)
                except (OSError, Refusal, CaptureInvalid):
                    failure = failure or 'layoutCloseFailure'
            failure = failure or interrupted(end, cancelled)
        except Exception:
            failure = failure or 'ownerFailure'

        # No signal may fall back to its previous handler during the success
        # decision. While masked, newly pending signals are part of that decision.
        try:
            mask = signal.pthread_sigmask(signal.SIG_BLOCK, numbers)
        except OSError:
            failure = failure or 'ownerFailure'
        # SIG_IGN discards a pending signal. Do not restore that disposition
        # before sampling pending cancellation.
        ordinary = [number for number, handler in previous.items() if handler != signal.SIG_IGN]
        if not restore_handlers(ordinary):
            failure = failure or 'ownerFailure'
        code = finish(capture, failure, before_success=admit_success)
    finally:
        restored = restore_handlers(list(previous))
        if mask is not None:
            try:
                signal.pthread_sigmask(signal.SIG_SETMASK, mask)
            except OSError:
                restored = False
        if not restored:
            code = finish(capture, failure or 'ownerFailure')
    return code, capture


def main(arguments=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    run = commands.add_parser('run', help='own one capture of the driver after --')
    run.add_argument('--root', required=True)
    run.add_argument('--deadline-seconds', type=float, required=True)
    run.add_argument('--trusted', action='append', default=[])
    run.add_argument('driver', nargs=argparse.REMAINDER)
    read = commands.add_parser('read', help='replay one recorded capture')
    read.add_argument('capture')
    read.add_argument('--golden')
    options = parser.parse_args(arguments)
    try:
        if options.command == 'read':
            golden = json.loads(Path(options.golden).read_bytes()) if options.golden else None
            report = read_capture(options.capture, golden)
            print(json.dumps(report, sort_keys=True))
            return 0 if report['notice']['outcome'] == 'passed' else 1
        if sys.platform != 'linux':
            raise Refusal('unsupportedPlatform')
        driver = options.driver[1:] if options.driver[:1] == ['--'] else options.driver
        code, capture = run_capture(options.root, driver, options.deadline_seconds, options.trusted)
    except (Refusal, CaptureInvalid) as error:
        print(json.dumps({'outcome': 'refused', 'category': str(error)}), flush=True)
        return 2
    except OSError:
        print(json.dumps({'outcome': 'uncertain', 'category': 'terminalRecordUnavailable'}), flush=True)
        return 2
    print(json.dumps({'captureId': capture.name, 'outcome': 'passed' if code == 0 else 'revoked'}), flush=True)
    return code


if __name__ == '__main__':
    raise SystemExit(main())
