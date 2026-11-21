"""Source contract tests; no Bun, library, fork, signals, or observer needed."""

import errno
import json
from pathlib import Path
import signal
import unittest
from unittest.mock import Mock, call, patch

import supervisor


class OwnerContract(unittest.TestCase):
    def test_full_width_boundary(self):
        for fd in (0, 3, 1048575, 67108863):
            self.assertTrue(supervisor.fits(fd), fd)
        for fd in (-1, 67108864, (1 << 32) + 3, True, 3.0):
            self.assertFalse(supervisor.fits(fd), fd)
        with patch.object(supervisor.os, "close") as close:
            with self.assertRaisesRegex(supervisor.Refused, "descriptorRange"):
                supervisor.checked((1 << 32) + 3)
            close.assert_called_once_with((1 << 32) + 3)

    def test_eof_and_pidfd_do_not_establish_wait_status(self):
        owner = supervisor.Owner()
        owner.pid = 123
        owner.fds = {"pidfd": 8, "output": 9}
        with patch.object(supervisor.os, "read", return_value=b""):
            owner.collect()
        with patch.object(supervisor.select, "poll") as poll:
            poll.return_value.poll.return_value = [(8, supervisor.select.POLLIN)]
            owner.pause()
            poll.return_value.register.assert_called_once_with(8, supervisor.select.POLLIN)
        with patch.object(supervisor.os, "waitpid", side_effect=[(0, 0), (123, 3 << 8)]):
            owner.reap_once()
            self.assertTrue(owner.eof)
            self.assertIsNone(owner.status)
            owner.reap_once()
        self.assertEqual(owner.status, 3 << 8)

    def test_stop_bound_is_sticky_and_keeps_resources(self):
        owner = supervisor.Owner()
        owner.pid = 123
        owner.fds = {"pidfd": 8, "control": 9, "output": 10, "artifact": 11}
        owner.begin_stop(10)
        owner.send_signal = Mock()
        owner.settle_step(10.25)
        owner.settle_step(10.50)
        owner.settle_step(11.51)
        self.assertEqual(owner.send_signal.call_args_list,
                         [call(signal.SIGTERM), call(signal.SIGKILL)])
        self.assertTrue(owner.overdue)
        self.assertEqual(owner.failure, "workerStopUnconfirmed")
        self.assertEqual(owner.fds, {"pidfd": 8, "control": 9, "output": 10, "artifact": 11})
        with patch.object(supervisor.os, "waitpid", return_value=(123, 9)):
            owner.retain_until_reaped()
        self.assertEqual(owner.status, 9)
        self.assertTrue(owner.overdue)

    def test_first_wait_status_observed_after_bound_is_late(self):
        owner = supervisor.Owner()
        owner.pid = 123
        owner.begin_stop(10)
        with patch.object(supervisor.os, "waitpid", return_value=(123, 3 << 8)), \
                patch.object(supervisor.time, "monotonic", return_value=11.51):
            owner.reap_once()
        self.assertEqual(owner.status, 3 << 8)
        self.assertTrue(owner.overdue)

    def test_close_failure_attempts_every_other_close(self):
        owner = supervisor.Owner()
        owner.fds = {"control": 8, "output": 9, "artifact": 10}
        with patch.object(supervisor.os, "close",
                          side_effect=[OSError(errno.EIO, "injected"), None, None]) as close:
            owner.close_all()
        self.assertEqual(close.call_args_list, [call(8), call(9), call(10)])
        self.assertEqual(owner.failure, "closeFailure")

    def test_proc_proof_uses_the_main_task_and_two_fds(self):
        row = b"7 0x1000 0x2 0xf 0x0 0x0 0x0 0x2000 0x3000\n"
        with patch.object(supervisor, "proc_bytes", side_effect=[row, row.replace(b"0x2 ", b"0x1 "),
                                                               b"running\n"]) as read:
            self.assertTrue(supervisor.in_two_fd_poll(123))
            self.assertFalse(supervisor.in_two_fd_poll(123))
            self.assertFalse(supervisor.in_two_fd_poll(123))
        self.assertEqual(read.call_args_list, [call("/proc/123/task/123/syscall", 512)] * 3)

    def test_proposed_manifest_refuses_even_with_the_right_digest(self):
        path = Path(supervisor.HERE / "artifact-manifest.proposed.json")
        data = path.read_bytes()
        self.assertFalse(json.loads(data)["runtimeApproved"])
        with self.assertRaisesRegex(supervisor.Refused, "sourceOnly"):
            supervisor.read_manifest(path, supervisor.digest(data))
