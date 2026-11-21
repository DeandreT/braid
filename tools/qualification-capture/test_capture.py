import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, call, patch

import capture

FIXTURES = Path(__file__).resolve().parent / 'fixtures'
POSIX = unittest.skipUnless(os.name == 'posix', 'the owner runs only on the Linux qualification host')


@POSIX
class DriverTests(unittest.TestCase):
    def setUp(self):
        self.child = Mock(returncode=None, args=['fixture'])
        self.child.stdout.fileno.return_value = 42
        self.child.poll.return_value = None
        self.pipe = Mock()
        self.pipe.poll.return_value = [(42, capture.select.POLLIN)]
        self.clock = 0
        self.cancelled = False
        self.read = self.enterContext(patch.object(capture.os, 'read', return_value=b''))
        self.launch = self.enterContext(patch.object(capture.subprocess, 'Popen', return_value=self.child))
        self.enterContext(patch.object(capture.os, 'set_blocking'))
        self.enterContext(patch.object(capture.select, 'poll', return_value=self.pipe))
        self.enterContext(patch.object(capture, 'monotonic', lambda: self.clock))

    def drive(self, deadline=10):
        return capture.drive(Path('/unused'), ['fixture'], deadline, lambda: self.cancelled)

    def exit(self, code=0):
        self.child.returncode = code
        return code

    def test_cancellation_before_spawn_avoids_launch(self):
        self.cancelled = True
        self.assertEqual('cancelled', self.drive().failure)
        self.launch.assert_not_called()

    def test_zero_exit_at_or_after_cutoff_cannot_pass(self):
        for observed_at in (0.025, 0.026):
            def observe_exit():
                self.clock = observed_at
                return self.exit()

            with self.subTest(observed_at=observed_at):
                self.clock, self.child.returncode = 0, None
                self.child.poll.side_effect = observe_exit
                self.assertEqual('deadline', self.drive(deadline=0.025).failure)
                self.child.terminate.assert_not_called()
                self.child.kill.assert_not_called()

    def test_poll_slice_is_clamped_and_late_zero_exit_is_not_accepted(self):
        def wait_for_pipe(timeout):
            self.assertEqual(25, timeout)
            self.clock = 0.026
            self.exit()
            return [(42, capture.select.POLLIN)]

        self.pipe.poll.side_effect = wait_for_pipe
        self.assertEqual('deadline', self.drive(deadline=0.025).failure)
        self.read.assert_not_called()

    def test_cancellation_wins_when_observed_with_expiry_and_exit(self):
        def observe_exit():
            self.cancelled, self.clock = True, 10
            return self.exit(3)

        self.child.poll.side_effect = observe_exit
        self.assertEqual('cancelled', self.drive().failure)

    def test_zero_exit_is_not_enough_when_eof_arrives_at_cutoff(self):
        def read_eof(*_):
            self.clock = 10
            return b''

        self.child.poll.return_value = self.exit()
        self.read.side_effect = read_eof
        self.assertEqual(capture.DriverResult('deadline', settled=False), self.drive())

    def test_zero_exit_after_stop_does_not_clear_cancellation(self):
        def cancel_while_waiting(_timeout):
            self.cancelled = True
            return []

        self.pipe.poll.side_effect = cancel_while_waiting
        self.child.wait.side_effect = lambda **_: self.exit()
        self.assertEqual(capture.DriverResult('cancelled', settled=False), self.drive())
        self.child.wait.assert_called_once_with(timeout=capture.GRACE_SECONDS)

    def test_stdout_without_eof_has_a_bounded_grace_after_exit(self):
        def wait_for_pipe(_timeout):
            self.clock = capture.GRACE_SECONDS
            return []

        self.child.poll.return_value = self.exit()
        self.pipe.poll.side_effect = wait_for_pipe
        self.assertEqual(capture.DriverResult('outputDidNotSettle', settled=False), self.drive())
        self.child.kill.assert_not_called()
        self.child.stdout.close.assert_called_once_with()

    def test_zero_exit_with_partial_stdout_then_read_error_cannot_pass(self):
        self.child.poll.return_value = self.exit()
        self.read.side_effect = [b'partial', OSError('private fixture detail')]
        self.assertEqual(capture.DriverResult('outputDidNotSettle', b'partial', settled=False), self.drive())
        self.child.stdout.close.assert_called_once_with()

    def test_output_at_limit_passes_but_one_more_byte_revokes(self):
        data = b'a' * 10
        for extra in (b'', b'x'):
            with self.subTest(extra=extra), patch.object(capture, 'STDOUT_LIMIT', len(data)):
                self.child.poll.return_value = self.exit()
                self.read.reset_mock()
                self.read.side_effect = [data, extra]
                self.assertEqual(capture.DriverResult('outputLimit' if extra else None, data, not extra),
                                 self.drive())
                self.assertEqual([call(42, 11), call(42, 1)], self.read.call_args_list)

    def test_stdout_close_failure_cannot_pass_even_after_eof_and_zero_exit(self):
        self.child.poll.return_value = self.exit()
        self.child.stdout.close.side_effect = OSError('fixture')
        self.assertEqual(capture.DriverResult('outputDidNotSettle', settled=False), self.drive())


class StopTests(unittest.TestCase):
    def test_stop_requires_wait_status_not_just_a_signal_request(self):
        grace = capture.GRACE_SECONDS
        timeout = subprocess.TimeoutExpired(['fixture'], grace)
        for waits, reaped, expected in [
            ([0], True, [call.terminate(), call.wait(timeout=grace)]),
            ([timeout, -signal.SIGKILL], True,
             [call.terminate(), call.wait(timeout=grace), call.kill(), call.wait(timeout=grace)]),
            ([timeout, timeout], False,
             [call.terminate(), call.wait(timeout=grace), call.kill(), call.wait(timeout=grace)]),
        ]:
            with self.subTest(waits=waits):
                child = Mock()
                child.wait.side_effect = waits
                self.assertEqual(reaped, capture.stop(child))
                self.assertEqual(expected, child.mock_calls)

    def test_signal_or_wait_error_still_attempts_bounded_escalation(self):
        child = Mock()
        child.terminate.side_effect = PermissionError
        child.kill.side_effect = PermissionError
        child.wait.side_effect = [OSError, -signal.SIGKILL]
        self.assertTrue(capture.stop(child))
        self.assertEqual([call.terminate(), call.wait(timeout=capture.GRACE_SECONDS),
                          call.kill(), call.wait(timeout=capture.GRACE_SECONDS)], child.mock_calls)


class PreflightTests(unittest.TestCase):
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

    def test_deadline_during_layout_close_revokes_a_timely_driver_exit(self):
        real = capture.close_layout
        now = 0

        def close_then_expire(*arguments):
            nonlocal now
            real(*arguments)
            now = 30

        with patch.object(capture, 'monotonic', lambda: now), patch.object(
                capture, 'close_layout', close_then_expire):
            path = self.assertRevoked('', 'deadline')
        self.assertTrue((path / capture.MANIFEST).is_file())

    def test_unconfirmed_stop_revokes_without_reading_live_evidence(self):
        child = Mock(returncode=None)
        child.stdout.fileno.return_value = 42
        child.wait.side_effect = subprocess.TimeoutExpired(['fixture'], capture.GRACE_SECONDS)

        def launch(*args, **kwargs):
            signal.raise_signal(signal.SIGTERM)
            return child

        with (patch.object(capture.subprocess, 'Popen', side_effect=launch),
              patch.object(capture.os, 'set_blocking'),
              patch.object(capture.select, 'poll'),
              patch.object(capture, 'close_layout') as close):
            path = self.assertRevoked('', 'driverStopUnconfirmed')
        close.assert_not_called()
        child.stdout.close.assert_called_once_with()
        self.assertFalse((path / capture.MANIFEST).exists())

    def test_signal_during_handler_restoration_revokes_before_publication(self):
        real_signal = signal.signal
        previously_handled = Mock()
        old_term = real_signal(signal.SIGTERM, previously_handled)
        self.handlers[signal.SIGTERM] = previously_handled
        self.addCleanup(real_signal, signal.SIGTERM, old_term)

        def restore_then_cancel(number, handler):
            previous = real_signal(number, handler)
            if handler is previously_handled:
                signal.raise_signal(signal.SIGTERM)
            return previous

        with patch.object(signal, 'signal', restore_then_cancel):
            self.assertRevoked('', 'cancelled')
        previously_handled.assert_not_called()

    def test_restoring_ignored_disposition_does_not_discard_pending_cancellation(self):
        real_signal = signal.signal
        old_term = real_signal(signal.SIGTERM, signal.SIG_IGN)
        self.handlers[signal.SIGTERM] = signal.SIG_IGN
        self.addCleanup(real_signal, signal.SIGTERM, old_term)

        def restore_then_cancel(number, handler):
            previous = real_signal(number, handler)
            if number == signal.SIGINT and handler is self.handlers[number]:
                signal.raise_signal(signal.SIGTERM)
            return previous

        with patch.object(signal, 'signal', restore_then_cancel):
            self.assertRevoked('', 'cancelled')

    def test_deadline_while_staging_success_marker_revokes_before_link(self):
        sync, now = os.fsync, 0

        def sync_then_expire(fd):
            nonlocal now
            sync(fd)
            if Path(os.readlink(f'/proc/self/fd/{fd}')).name.startswith(f'.{capture.EXIT}.'):
                now = 30

        with patch.object(capture, 'monotonic', lambda: now), patch.object(os, 'fsync', sync_then_expire):
            self.assertRevoked('', 'deadline')

    def test_signal_after_cutoff_is_delivered_to_restored_handler(self):
        previously_handled = Mock()
        old_term = signal.signal(signal.SIGTERM, previously_handled)
        self.handlers[signal.SIGTERM] = previously_handled
        self.addCleanup(signal.signal, signal.SIGTERM, old_term)
        link = os.link

        def link_and_cancel(source, destination):
            if destination.name == capture.EXIT:
                signal.raise_signal(signal.SIGTERM)
                previously_handled.assert_not_called()
            link(source, destination)

        with patch.object(os, 'link', link_and_cancel):
            code, report, _ = self.run_owner('')
        self.assertEqual((0, 'passed'), (code, report['notice']['outcome']))
        previously_handled.assert_called_once()

    def test_previously_blocked_signal_is_not_consumed_by_capture(self):
        blocked = {signal.SIGTERM}
        previous_mask = signal.pthread_sigmask(signal.SIG_BLOCK, blocked)
        try:
            signal.raise_signal(signal.SIGTERM)
            code, report, _ = self.run_owner('')
            self.assertEqual((0, 'passed'), (code, report['notice']['outcome']))
            self.assertIn(signal.SIGTERM, signal.sigpending())
        finally:
            signal.sigtimedwait(blocked, 0)
            signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)

    def test_layout_close_failure_is_revoked(self):
        with patch.object(capture, 'close_layout', side_effect=OSError('fsync failed')):
            self.assertRevoked('', 'layoutCloseFailure')

    def test_late_cancellation_cannot_hide_a_layout_close_failure(self):
        def cancel_and_fail(*_):
            signal.raise_signal(signal.SIGTERM)
            raise OSError('fixture')

        with patch.object(capture, 'close_layout', side_effect=cancel_and_fail):
            self.assertRevoked('', 'layoutCloseFailure')

    def test_nonregular_evidence_is_refused_without_opening_it_in_blocking_mode(self):
        original_open = os.open

        def check_flags(path, flags, *args, **kwargs):
            if Path(path).name == 'receipt.fifo':
                self.assertTrue(flags & os.O_NONBLOCK)
            return original_open(path, flags, *args, **kwargs)

        with patch.object(os, 'open', check_flags):
            self.assertRevoked('os.mkfifo(os.path.join(output, "receipt.fifo"))\n', 'layoutCloseFailure')

    def test_partial_handler_install_and_failed_restore_revoke(self):
        numbers = list(self.handlers)
        argv = self.driver('')
        for failed_call, expected in [(2, numbers + numbers[:1]), (3, numbers * 2)]:
            calls = []

            def change(number, handler):
                calls.append(number)
                if len(calls) == failed_call:
                    raise OSError('fixture')
                return self.handlers[number]

            with self.subTest(failed_call=failed_call), patch.object(signal, 'signal', change):
                code, path = capture.run_capture(self.root, argv, 30, trusted=[argv[1]])
                self.assertEqual((1, {'outcome': 'revoked', 'category': 'ownerFailure'}),
                                 (code, capture.read_capture(path)['notice']))
                self.assertEqual(expected, calls)

    def test_directory_close_failure_after_linking_success_is_revoked(self):
        close = os.close
        injected = []

        def fail_once(fd):
            facts = os.fstat(fd)
            close(fd)
            for marker in self.root.glob(f'*/{capture.EXIT}'):
                if not injected and os.path.samestat(facts, marker.parent.stat()):
                    injected.append(marker.parent)
                    raise OSError('directory close failed')

        with patch.object(os, 'close', fail_once):
            code, report, path = self.run_owner('')
        self.assertEqual([path], injected)
        self.assertEqual(0, json.loads((path / capture.EXIT).read_bytes())['exit'])
        self.assertEqual((1, {'outcome': 'revoked', 'category': 'terminalWriteFailure'}), (code, report['notice']))

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
