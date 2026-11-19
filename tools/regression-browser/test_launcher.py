"""Linux signal forwarding checks; scratch stays on the verified Btrfs volume."""
import os
from pathlib import Path
import signal
import runpy
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

launcher = runpy.run_path(str(Path(__file__).with_name('run_linux.py')))
wait_for_operator = launcher['wait_for_operator']
ensure_docker = launcher['ensure_docker']
storage = launcher['storage']


class DockerAdmissionTests(unittest.TestCase):
    def test_running_guarded_daemon_is_not_restarted(self):
        with patch('subprocess.run', return_value=subprocess.CompletedProcess([], 0, '/fixture/docker overlay2 2\n')) as run:
            ensure_docker('/fixture/docker')
        self.assertEqual(run.call_count, 1)

    def test_absent_daemon_starts_existing_service_and_rechecks(self):
        results = [subprocess.CompletedProcess([], 1, ''), subprocess.CompletedProcess([], 0, ''),
                   subprocess.CompletedProcess([], 0, '/fixture/docker overlay2 2\n')]
        with patch('subprocess.run', side_effect=results) as run:
            ensure_docker('/fixture/docker')
        self.assertEqual(run.call_args_list[1].args[0], ['systemctl', 'start', 'docker.service'])
        self.assertEqual(run.call_count, 3)

    def test_another_data_root_is_refused_without_changing_it(self):
        with patch('subprocess.run', return_value=subprocess.CompletedProcess([], 0, '/var/lib/docker overlay2 2\n')) as run:
            with self.assertRaises(RuntimeError): ensure_docker('/fixture/docker')
        self.assertEqual(run.call_count, 1)

    def test_failed_restart_never_admits_the_operator(self):
        with patch('subprocess.run', side_effect=[subprocess.CompletedProcess([], 1, ''),
                   subprocess.CalledProcessError(1, ['systemctl'])]):
            with self.assertRaises(subprocess.CalledProcessError): ensure_docker('/fixture/docker')


class FixtureStorageTests(unittest.TestCase):
    def test_unconfigured_profile_fails_before_starting_the_fixture(self):
        with patch.object(sys, 'argv', ['run', '--isolated', '', 'config', 'bun', 'operator']), \
                patch.object(os, 'geteuid', return_value=0, create=True), patch('subprocess.run') as run:
            with self.assertRaisesRegex(ValueError, 'profile path'):
                launcher['main']()
            run.assert_not_called()

    def test_canonical_path_on_another_volume_is_not_admitted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            profile = {'source': '/dev/example', 'target': '/example', 'fsroot': '/fixture',
                       'uuid': '00000000-0000-4000-8000-000000000001', 'requiredOptions': ['compress=zstd']}
            mount = {**profile, 'target': '/elsewhere', 'fstype': 'btrfs', 'options': 'rw,compress=zstd'}
            with patch.object(storage, 'mount_at', return_value=mount), self.assertRaises(ValueError):
                storage.admitted_path(root, profile)


@unittest.skipUnless(sys.platform == 'linux', 'Requires Linux process signal semantics.')
class LauncherTests(unittest.TestCase):
    def test_child_exit_code_is_preserved(self):
        self.assertEqual(wait_for_operator([sys.executable, '-I', '-B', '-c', 'raise SystemExit(7)']), 7)

    def test_interrupts_allow_owned_child_cleanup(self):
        root = Path(os.environ['TMPDIR'])
        profile = storage.read_profile(os.environ.get('BRAID_STORAGE_PROFILE'))
        storage.admitted_path(root, profile)
        storage.require_host_reserve(profile)
        with tempfile.TemporaryDirectory(prefix='braid-launcher-', dir=root) as folder:
            ready, received, closed = [Path(folder) / name for name in ('ready', 'received', 'closed')]
            worker = (
                'import pathlib,signal,sys,time\n'
                'ready,received,closed=map(pathlib.Path,sys.argv[1:])\n'
                'def close(signum,frame):\n'
                ' received.write_text(str(signum))\n'
                ' time.sleep(0.25)\n'
                ' closed.write_text("clean")\n'
                ' raise SystemExit(0)\n'
                'signal.signal(signal.SIGINT,close)\n'
                'signal.signal(signal.SIGTERM,close)\n'
                'ready.write_text("ready")\n'
                'time.sleep(15)\n'
                'raise SystemExit(9)\n'
            )
            command = [sys.executable, '-I', '-B', '-c', worker, str(ready), str(received), str(closed)]
            launcher = ('import runpy,sys; wait=runpy.run_path(sys.argv[1])["wait_for_operator"]; '
                        'raise SystemExit(wait(sys.argv[2:]))')
            with subprocess.Popen([sys.executable, '-I', '-B', '-c', launcher,
                                   str(Path(__file__).with_name('run_linux.py')), *command], start_new_session=True) as process:
                end = time.monotonic() + 10
                while not ready.exists():
                    self.assertIsNone(process.poll())
                    self.assertLess(time.monotonic(), end)
                    time.sleep(0.01)
                process.send_signal(signal.SIGINT)
                while not received.exists():
                    self.assertIsNone(process.poll())
                    self.assertLess(time.monotonic(), end)
                    time.sleep(0.01)
                process.send_signal(signal.SIGINT)
                self.assertEqual(process.wait(timeout=10), 0)
            self.assertEqual(closed.read_text(), 'clean')
            self.assertEqual(received.read_text(), str(signal.SIGINT))


if __name__ == '__main__':
    unittest.main()
