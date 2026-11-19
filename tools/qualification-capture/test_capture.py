import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

import capture

FIXTURES = Path(__file__).resolve().parent / 'fixtures'
POSIX = unittest.skipUnless(os.name == 'posix', 'the owner runs only on the Linux qualification host')


class DrainTests(unittest.TestCase):
    def test_oversized_driver_refused_before_capture_creation_or_execution(self):
        with tempfile.TemporaryDirectory() as tmp:
            driver = Path(tmp) / 'driver'
            with driver.open('wb') as stream:
                stream.truncate((64 << 20) + 1)
            driver.chmod(0o700)
            root = Path(tmp) / 'captures'
            root.mkdir(mode=0o700)
            with patch.object(capture.subprocess, 'Popen') as launch:
                with self.assertRaisesRegex(capture.CaptureInvalid, '^memberLimit$'):
                    capture.run_capture(root, [str(driver)], 1)
            launch.assert_not_called()
            self.assertEqual([], list(root.iterdir()))

    def test_zero_exit_with_partial_stdout_then_read_error_cannot_pass(self):
        child = Mock(returncode=0)
        child.stdout.read1.side_effect = [b'partial', OSError('private fixture detail')]
        with patch.object(capture.subprocess, 'Popen', return_value=child):
            result = capture.drive(Path('/unused'), ['fixture'], 10, threading.Event())
        self.assertEqual(('outputDidNotSettle', b'partial'), result)
        child.stdout.close.assert_called_once_with()


class MarkerTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix='braid-capture-'))
        self.addCleanup(shutil.rmtree, self.directory)

    def test_marker_is_never_replaced(self):
        marker = self.directory / capture.EXIT
        capture.write_new(marker, b'first\n')
        with self.assertRaises(FileExistsError):
            capture.write_new(marker, b'second\n')
        self.assertEqual(b'first\n', marker.read_bytes())
        self.assertEqual([capture.EXIT], os.listdir(self.directory))

    def test_concurrent_attempts_publish_exactly_one_complete_marker(self):
        marker, count = self.directory / capture.REVOKED, 16
        barrier, results = threading.Barrier(count), [None] * count

        def attempt(index):
            data = capture.canonical({'category': f'attempt{index}'})
            barrier.wait()
            try:
                capture.write_new(marker, data)
                results[index] = data
            except FileExistsError:
                results[index] = False

        threads = [threading.Thread(target=attempt, args=(index,)) for index in range(count)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        winners = [result for result in results if result]
        self.assertEqual(1, len(winners))
        self.assertEqual(count - 1, results.count(False))
        self.assertEqual(winners[0], marker.read_bytes())
        self.assertEqual([capture.REVOKED], os.listdir(self.directory))


class HistoricalReaderTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix='braid-capture-'))
        self.addCleanup(shutil.rmtree, self.directory)

    def fixture(self, name):
        target = self.directory / name
        shutil.copytree(FIXTURES / name, target)
        (target / capture.EVIDENCE).mkdir(exist_ok=True)
        return target, json.loads((FIXTURES / (name + '.golden.json')).read_bytes())

    def test_historical_captures_replay_against_their_golden_fixtures(self):
        names = sorted(path.name for path in FIXTURES.iterdir() if path.is_dir())
        self.assertEqual(2, len(names))
        outcomes = set()
        for name in names:
            path, golden = self.fixture(name)
            outcomes.add(capture.read_capture(path, golden)['notice']['outcome'])
        self.assertEqual({'passed', 'revoked'}, outcomes)

    def test_fixture_records_bind_actual_bytes_independently_of_the_reader(self):
        # Do not use capture.canonical/sha256/replay to derive the expected bindings.
        # Revocation wins at replay, but its retained manifest must still be truthful.
        for directory in sorted(path for path in FIXTURES.iterdir() if path.is_dir()):
            with self.subTest(capture=directory.name):
                records = {}
                for name in ('header.json', 'manifest.json', 'complete.exit', 'complete.revoked'):
                    path = directory / name
                    if not path.exists():
                        continue
                    data = path.read_bytes()
                    records[name] = json.loads(data)
                    expected = (json.dumps(records[name], sort_keys=True, separators=(',', ':')) + '\n').encode('utf-8')
                    self.assertEqual(expected, data)
                members = records['manifest.json']['members']
                actual_names = {'driver.stdout'}
                evidence = directory / 'evidence'
                if evidence.exists():
                    actual_names.update('evidence/' + path.name for path in evidence.iterdir())
                self.assertEqual(actual_names, {member['name'] for member in members})
                self.assertEqual(len(actual_names), len(members))
                digests = {}
                for member in members:
                    data = (directory / member['name']).read_bytes()
                    digests[member['name']] = hashlib.sha256(data).hexdigest()
                    self.assertEqual(len(data), member['size'])
                    self.assertEqual(digests[member['name']], member['sha256'])
                exited = records['complete.exit']
                if exited['exit'] == 0:
                    for name, field in [('header.json', 'headerSha256'), ('manifest.json', 'manifestSha256')]:
                        self.assertEqual(hashlib.sha256((directory / name).read_bytes()).hexdigest(), exited[field])
                golden = json.loads((FIXTURES / (directory.name + '.golden.json')).read_bytes())
                self.assertEqual(None if 'complete.revoked' in records else digests, golden['members'])

    def test_read_command_accepts_only_a_passed_capture(self):
        for name in (path.name for path in FIXTURES.iterdir() if path.is_dir()):
            path, golden = self.fixture(name)
            with patch('builtins.print'):
                code = capture.main(['read', str(path), '--golden', str(FIXTURES / (name + '.golden.json'))])
            self.assertEqual(0 if golden['notice']['outcome'] == 'passed' else 1, code)

    def passed(self):
        name = next(n for n in os.listdir(FIXTURES) if (FIXTURES / n / capture.EXIT).is_file()
                    and not (FIXTURES / n / capture.REVOKED).exists())
        return self.fixture(name)

    def assertInvalid(self, path, category, golden=None):
        with self.assertRaises(capture.CaptureInvalid) as raised:
            capture.read_capture(path, golden)
        self.assertEqual(category, str(raised.exception))

    def test_golden_notice_and_membership_are_compared(self):
        path, golden = self.passed()
        self.assertInvalid(path, 'goldenMismatch', {**golden, 'notice': {'outcome': 'revoked', 'category': 'x'}})
        self.assertInvalid(path, 'goldenMismatch', {**golden, 'members': {}})

    def test_header_and_binding_are_checked_before_replay(self):
        path, _ = self.passed()
        header = json.loads((path / capture.HEADER).read_bytes())
        (path / capture.HEADER).unlink()
        (path / capture.HEADER).write_bytes(capture.canonical({**header, 'deadlineSeconds': 1}))
        with patch.object(capture, 'replay') as replay:
            self.assertInvalid(path, 'terminalBindingMismatch')
            replay.assert_not_called()
        renamed = path.with_name('00000000-0000-4000-8000-000000000000')
        path.rename(renamed)
        self.assertInvalid(renamed, 'headerBindingMismatch')

    def test_whole_member_set_is_admitted_before_any_read(self):
        path, _ = self.passed()
        (path / capture.EVIDENCE / 'extra.json').write_bytes(b'{}\n')
        with patch.object(capture, 'read_fd', wraps=capture.read_fd) as read:
            self.assertInvalid(path, 'memberSetMismatch')
            self.assertEqual(3, read.call_count)  # header, complete.exit, manifest only

    def test_changed_member_is_rejected(self):
        path, _ = self.passed()
        member = next((path / capture.EVIDENCE).iterdir())
        data = bytearray(member.read_bytes())
        data[0] ^= 1
        member.write_bytes(bytes(data))
        self.assertInvalid(path, 'memberDigestMismatch')

    def test_failure_without_durable_revocation_is_a_protocol_violation(self):
        path, _ = self.passed()
        (path / capture.EXIT).unlink()
        capture.write_new(path / capture.EXIT, capture.canonical({'category': 'driverFailed', 'exit': 1}))
        self.assertInvalid(path, 'failureWithoutRevocation')

    def test_missing_markers_are_incomplete_never_passed(self):
        path, _ = self.passed()
        (path / capture.EXIT).unlink()
        self.assertEqual({'outcome': 'incomplete', 'category': None}, capture.read_capture(path)['notice'])

    def test_descriptors_are_closed_after_a_failed_close(self):
        path, _ = self.passed()
        real_open, real_close, opened, failed = os.open, os.close, [], []

        def tracking_open(*arguments, **keywords):
            fd = real_open(*arguments, **keywords)
            opened.append(fd)
            return fd

        def failing_close(fd):
            real_close(fd)
            if not failed and len(opened) > 3:
                failed.append(fd)
                raise OSError('close failed')

        with patch.object(capture.os, 'open', tracking_open), patch.object(capture.os, 'close', failing_close):
            self.assertInvalid(path, 'unreadableCapture')
        self.assertTrue(failed)
        for fd in opened:
            with self.assertRaises(OSError):
                os.fstat(fd)


@POSIX
class OwnerTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix='braid-capture-root-'))
        self.addCleanup(shutil.rmtree, self.root)
        os.chmod(self.root, 0o700)
        self.python = os.path.realpath(sys.executable)
        self.handlers = {number: signal.getsignal(number) for number in (signal.SIGINT, signal.SIGTERM)}

    def driver(self, body):
        script = self.root.parent / f'{self.root.name}-driver.py'
        script.write_text('import os, signal, sys, time\noutput = sys.argv[sys.argv.index("--output") + 1]\n' + body)
        os.chmod(script, 0o644)
        self.addCleanup(script.unlink)
        return [self.python, str(script)]

    def run_owner(self, body, deadline=30):
        argv = self.driver(body)
        code, path = capture.run_capture(self.root, argv, deadline, trusted=[argv[1]])
        self.assertEqual(self.handlers, {number: signal.getsignal(number) for number in self.handlers})
        return code, capture.read_capture(path), path

    def assertRevoked(self, body, category, deadline=30):
        code, report, path = self.run_owner(body, deadline)
        self.assertEqual((1, {'outcome': 'revoked', 'category': category}), (code, report['notice']))
        self.assertEqual({'category': category, 'exit': 1}, json.loads((path / capture.EXIT).read_bytes()))
        return path

    def test_success_records_bound_members_and_trusted_code(self):
        code, report, path = self.run_owner(
            'open(os.path.join(output, "receipt.json"), "w").write("{}\\n")\nprint("admitted", flush=True)\n')
        self.assertEqual((0, 'passed'), (code, report['notice']['outcome']))
        self.assertEqual({'driver.stdout', 'evidence/receipt.json'}, set(report['members']))
        self.assertEqual(b'admitted\n', (path / capture.STDOUT).read_bytes())
        header = json.loads((path / capture.HEADER).read_bytes())
        self.assertEqual(3, len(header['trustedCode']))

    def test_driver_failure_is_revoked(self):
        self.assertRevoked('sys.exit(3)\n', 'driverFailed')

    def test_deadline_stops_the_driver(self):
        self.assertRevoked('time.sleep(60)\n', 'deadline', deadline=0.5)

    def test_cancellation_during_the_run_stops_the_driver(self):
        self.assertRevoked('os.kill(os.getppid(), signal.SIGTERM)\ntime.sleep(60)\n', 'cancelled')

    def test_late_cancellation_after_the_last_managed_close_revokes(self):
        real = capture.close_layout

        def close_then_cancel(*arguments):
            real(*arguments)
            os.kill(os.getpid(), signal.SIGTERM)

        with patch.object(capture, 'close_layout', close_then_cancel):
            path = self.assertRevoked('', 'cancelled')
        self.assertTrue((path / capture.MANIFEST).is_file())

    def test_layout_close_failure_is_revoked(self):
        with patch.object(capture, 'close_layout', side_effect=OSError('fsync failed')):
            self.assertRevoked('', 'layoutCloseFailure')

    def test_lost_success_race_never_replaces_the_earlier_marker_and_revokes(self):
        real, competing = capture.close_layout, b'{"exit":0,"headerSha256":"","manifestSha256":""}\n'

        def close_then_race(path, output):
            real(path, output)
            capture.write_new(path / capture.EXIT, competing)

        with patch.object(capture, 'close_layout', close_then_race):
            code, report, path = self.run_owner('')
        self.assertEqual((1, {'outcome': 'revoked', 'category': 'terminalWriteFailure'}), (code, report['notice']))
        self.assertEqual(competing, (path / capture.EXIT).read_bytes())

    def test_preflight_refuses_before_creating_a_capture(self):
        argv = self.driver('')
        for arguments in [([], 30), (argv, True), (argv, float('inf')), (argv, 0)]:
            with self.assertRaises(capture.Refusal):
                capture.run_capture(self.root, *arguments, trusted=[argv[1]])
        os.chmod(argv[1], 0o666)
        with self.assertRaisesRegex(capture.Refusal, 'untrustedCode'):
            capture.run_capture(self.root, argv, 30, trusted=[argv[1]])
        os.chmod(self.root, 0o755)
        with self.assertRaisesRegex(capture.Refusal, 'invalidCaptureRoot'):
            capture.run_capture(self.root, argv, 30)
        self.assertEqual([], os.listdir(self.root))


if __name__ == '__main__':
    unittest.main()
