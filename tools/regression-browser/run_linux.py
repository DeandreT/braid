"""Run the root-operated fixture with a private Btrfs alias, without changing shared ancestor ownership."""
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import qualification_storage as storage

class OperatorUnsettled(RuntimeError):
    pass


def ensure_docker(docker_root):
    """Called only after mount/headroom admission; never adopts another data root."""
    command = ['/usr/bin/docker', 'info', '--format', '{{.DockerRootDir}} {{.Driver}} {{.CgroupVersion}}']
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        subprocess.run(['systemctl', 'start', 'docker.service'], check=True, capture_output=True, text=True)
        result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0 or result.stdout.strip() != f'{docker_root} overlay2 2':
        raise RuntimeError('The guarded Btrfs Docker daemon is unavailable or has unexpected storage.')


def wait_for_operator(command, shutdown_seconds=180):
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
                if interrupted is not None and time.monotonic() - interrupted > shutdown_seconds:
                    # Leave the namespace alias intact for the live owner, never kill it mid-cleanup.
                    raise OperatorUnsettled('The regression operator did not settle after interruption.')
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def main():
    if len(sys.argv) == 5 and sys.argv[1] != '--isolated':
        os.execv('/usr/bin/unshare', ['unshare', '--mount', '--propagation', 'private',
                                    '/usr/bin/python3', '-I', '-B', __file__, '--isolated', *sys.argv[1:]])
    if len(sys.argv) != 6 or sys.argv[1] != '--isolated' or os.geteuid() != 0:
        raise ValueError('Expected a root-operated isolated fixture.')
    profile_path, config, bun, operator = sys.argv[2:]
    profile = storage.read_profile(profile_path)
    storage.require_host_reserve(profile)
    docker_root = storage.admitted_path(profile['dockerRoot'], profile)
    if storage.admitted_path(profile['bun'], profile, directory=False) != Path(bun):
        raise ValueError('Operator tool does not match storage profile.')
    storage.admitted_path(profile['dotnet'], profile, directory=False)
    storage.admitted_path(operator, profile, directory=False)
    data = json.loads(Path(config).read_text())
    identity = data['fixtureId']
    if not re.fullmatch(r'[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}', identity):
        raise ValueError('Invalid fixture identity.')
    private = storage.admitted_path(Path(profile['dataRoot']) / ('braid-regression-' + identity), profile)
    if data['privateDirectory'] != str(private) or Path(config).parent != private:
        raise ValueError('The fixture config is outside the configured private directory.')
    if data['dotnet'] != profile['dotnet']:
        raise ValueError('Operator SDK does not match storage profile.')
    if private.stat().st_uid != 0 or private.stat().st_mode & 0o777 != 0o700:
        raise ValueError('Expected a private, root-owned directory.')
    alias = Path('/run/braid-regression-' + identity)
    if data['privateStorageRoot'] != str(alias / 'objects'):
        raise ValueError('Fixture alias mismatch.')
    alias.mkdir(mode=0o700)  # An empty mountpoint only; all fixture writes land on Btrfs.
    mounted = False
    settled = True
    try:
        subprocess.run(['/usr/bin/mount', '--bind', str(private), str(alias)], check=True)
        mounted = True
        actual = json.loads(subprocess.check_output(['findmnt', '-J', '-T', str(alias), '-o', 'FSTYPE,UUID,OPTIONS']))['filesystems'][0]
        options = actual['options'].split(',')
        if (actual['fstype'] != 'btrfs' or actual['uuid'] != profile['uuid'] or 'rw' not in options or
                'ro' in options or not os.path.samefile(alias, private)):
            raise ValueError('Bound fixture storage did not match the admitted directory.')
        ensure_docker(docker_root)
        try:
            code = wait_for_operator([bun, operator, '--config', config],
                                     shutdown_seconds=360 if data['schemaVersion'] == 'braid.build-browser-operator-config.v1' else 180)
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
