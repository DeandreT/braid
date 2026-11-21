"""Protected source admission for the standalone fixture; never a capture driver."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import sys


CAP = 64 << 10
MAX_FD = (1 << 26) - 1
SCHEMA = "braid.native-bun-fixture.v1"


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def protected(path):
    require(path.is_absolute() and path.resolve(strict=True) == path, "bootstrapPath")
    for directory in path.parents:
        info = directory.stat()
        require(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and
                not info.st_mode & 0o022, "bootstrapDirectory")
    return path


def open_fd(name, _requested_flags):
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
    fd = os.open(name, flags)
    if not 0 <= fd <= MAX_FD:
        os.close(fd)
        raise ValueError("descriptorRange")
    return fd


def bounded_bytes(path, protected_file=False):
    with open(path, "rb", opener=open_fd) as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= CAP, "bootstrapInput")
        if protected_file:
            require(info.st_uid == 0 and not info.st_mode & 0o022, "bootstrapFile")
        data = stream.read(CAP + 1)
        require(len(data) == info.st_size, "bootstrapInputChanged")
    return data


def pinned(data, expected, reason):
    require(type(expected) is str and re.fullmatch("[0-9a-f]{64}", expected) is not None
            and hashlib.sha256(data).hexdigest() == expected, reason)
    return data


def admit(args):
    # Trust in this bootstrap/interpreter is established externally, before
    # starting it. This check refuses accidental execution from a checkout.
    path = protected(Path(__file__).absolute())
    info = path.stat()
    require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and
            not info.st_mode & 0o022, "bootstrapFile")
    require(args.manifest.is_absolute(), "manifestPath")
    manifest = json.loads(pinned(bounded_bytes(args.manifest),
                                 args.approved_manifest_sha256, "manifestDigest"))
    require(manifest["schema"] == SCHEMA and manifest["runtimeApproved"] is True, "sourceOnly")
    supervisor = protected(path.with_name("supervisor.py"))
    checked = pinned(bounded_bytes(supervisor, protected_file=True),
                     manifest["sourceFiles"]["supervisor.py"], "supervisorDigest")
    return checked, supervisor


def main():
    parser = argparse.ArgumentParser(description=__doc__, allow_abbrev=False)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--approved-manifest-sha256", required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--case", choices=("reply", "hold"), required=True)
    args = parser.parse_args()
    try:
        require(sys.platform == "linux" and sys.implementation.name == "cpython" and
                sys.version_info[:3] == (3, 15, 2) and sys.flags.isolated and sys.flags.no_site
                and sys.flags.dont_write_bytecode, "hostRuntime")
        require(os.getuid() == os.geteuid() != 0 and os.getgid() == os.getegid(),
                "notUnprivileged")
        require(not {signal.SIGINT, signal.SIGTERM}.intersection(
            signal.pthread_sigmask(signal.SIG_BLOCK, ())), "blockedCancellationSignals")
        with open("/proc/self/status", "rb", opener=open_fd) as status:
            require(re.search(rb"(?m)^TracerPid:[ \t]+0$", status.read(CAP)) is not None,
                    "tracedLaunch")
        data, path = admit(args)
    except (OSError, ValueError, KeyError, TypeError):
        print('{"outcome":"refused","reason":"bootstrapAdmission"}')
        return 1
    # Do not import or reopen supervisor.py. Execute only the admitted buffer.
    scope = {"__name__": "braid_fixture_supervisor", "__file__": str(path)}
    exec(compile(data, str(path), "exec", dont_inherit=True), scope)
    return scope["run"](args)


if __name__ == "__main__":
    raise SystemExit(main())
