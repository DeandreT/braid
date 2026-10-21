"""Run the root-operated fixture with a private Btrfs alias, without changing shared ancestor ownership."""
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time


class OperatorUnsettled(RuntimeError):
    pass


def wait_for_operator(command):
    child = None
    interrupted = None
    received = None

    def forward(signum, _frame):
        nonlocal interrupted, received
        if interrupted is None:
            interrupted = time.monotonic()
            received = signum
            if child is not None and child.poll() is None:
                child.send_signal(signum)

    previous = {sig: signal.signal(sig, forward) for sig in (signal.SIGINT, signal.SIGTERM)}
    try:
        # Keep terminal group interrupts away from the API/worker; their owner stops them.
        child = subprocess.Popen(command, start_new_session=True)
        if received is not None and child.poll() is None:
            child.send_signal(received)
        while True:
            try:
                return child.wait(timeout=1)
            except subprocess.TimeoutExpired:
                if interrupted is not None and time.monotonic() - interrupted > 180:
                    # Leave the namespace alias intact for the live owner, never kill it mid-cleanup.
                    raise OperatorUnsettled('The regression operator did not settle after interruption.')
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def main():
    if len(sys.argv) == 4:
        os.execv('/usr/bin/unshare', ['unshare', '--mount', '--propagation', 'private',
                                    '/usr/bin/python3', '-I', '-B', __file__, '--isolated', *sys.argv[1:]])
    assert len(sys.argv) == 5 and sys.argv[1] == '--isolated' and os.geteuid() == 0
    config, node, operator = sys.argv[2:]
    data = json.loads(Path(config).read_text())
    identity = data['fixtureId']
    assert re.fullmatch(r'[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}', identity)
    private = Path('/srv/example-build/data/weft-regression-' + identity)
    assert data['privateDirectory'] == str(private) and Path(config).parent == private
    mount = json.loads(subprocess.check_output(['findmnt', '-J', '-T', str(private), '-o', 'FSTYPE,UUID,FSROOT,OPTIONS']))['filesystems'][0]
    assert mount['fstype'] == 'btrfs' and mount['uuid'] == '00000000-0000-4000-8000-000000000001' and mount['fsroot'] == '/@example-build'
    assert 'rw' in mount['options'].split(',') and 'compress=zstd:3' in mount['options']
    host = os.statvfs('/mnt/example-host')
    assert host.f_bavail * host.f_frsize >= 25 * 2**30
    assert not private.is_symlink() and private.stat().st_uid == 0 and private.stat().st_mode & 0o777 == 0o700
    alias = Path('/run/weft-regression-' + identity)
    assert data['privateStorageRoot'] == str(alias / 'objects')
    alias.mkdir(mode=0o700)  # An empty mountpoint only; all fixture writes land on Btrfs.
    mounted = False
    settled = True
    try:
        subprocess.run(['/usr/bin/mount', '--bind', str(private), str(alias)], check=True)
        mounted = True
        actual = json.loads(subprocess.check_output(['findmnt', '-J', '-T', str(alias), '-o', 'FSTYPE,UUID,OPTIONS']))['filesystems'][0]
        assert actual['fstype'] == 'btrfs' and actual['uuid'] == mount['uuid'] and 'rw' in actual['options'].split(',')
        assert alias.stat().st_ino == private.stat().st_ino and alias.stat().st_dev == private.stat().st_dev
        try:
            code = wait_for_operator([node, operator, '--config', config])
        except OperatorUnsettled:
            settled = False
            raise
    finally:
        if settled:
            if mounted:
                subprocess.run(['/usr/bin/umount', str(alias)], check=True)
            alias.rmdir()
    return code


if __name__ == '__main__':
    sys.exit(main())
