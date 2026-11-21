"""Source contract tests; no Bun, library, fork, signals, or observer needed."""

import errno
import json
import os
from pathlib import Path
import signal
import stat
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, call, patch

import bootstrap
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

    def test_pidfd_failure_never_permits_exec_and_retains_handles_until_reap(self):
        owner = supervisor.Owner()
        owner.fds["artifact"] = 8

        def pair(parent, child):
            owner.own(parent, 8 + len(owner.fds))
            owner.own(child, 8 + len(owner.fds))
            if parent == "gate":
                owner.gate_writer = Mock()
                owner.gate_writer.detach.return_value = owner.fds[parent]

        owner.socketpair = pair
        with (
            patch.object(supervisor.os, "open", return_value=15),
            patch.object(supervisor.os, "fstat", return_value=SimpleNamespace(
                st_mode=stat.S_IFCHR, st_rdev=os.makedev(1, 3))),
            patch.object(supervisor, "active_fds",
                         side_effect=lambda: {0, 1, 2, *owner.fds.values()}),
            patch.object(supervisor, "proc_bytes", return_value=b"TracerPid:\t0\n"),
            patch.object(supervisor.signal, "pthread_sigmask", return_value=set()),
            patch.object(supervisor.os, "fork", return_value=123),
            patch.object(supervisor.os, "pidfd_open", side_effect=OSError(errno.EMFILE, "mock")),
            patch.object(supervisor.os, "write") as write,
            patch.object(supervisor.os, "close") as close,
        ):
            with self.assertRaisesRegex(supervisor.Refused, "pidfdUnavailable"):
                owner.launch(1024, ["/not-executed"], {}, Path("/"))
            write.assert_not_called()
            owner.gate_writer.shutdown.assert_called_once_with(supervisor.socket.SHUT_WR)
            owner.gate_writer.detach.assert_not_called()
            close.assert_not_called()
            owned = owner.fds.copy()
            with self.assertRaisesRegex(supervisor.Refused, "childNotReaped"):
                owner.close_all()
            self.assertEqual(owner.fds, owned)

        # Independently exercise the fork branch after the parent's shutdown:
        # the real gate parser sees EOF and does not reach exec.
        with (
            patch.object(supervisor.os, "close"),
            patch.object(supervisor.os, "read", return_value=b""),
            patch.object(supervisor.select, "poll") as poll,
            patch.object(supervisor.os, "execve") as execve,
            patch.object(supervisor.time, "monotonic", return_value=10.0),
        ):
            poll.return_value.poll.return_value = [(owned["childGate"], supervisor.select.POLLHUP)]
            with self.assertRaisesRegex(supervisor.Refused, "execNotPermitted"):
                owner.child_exec(1024, ["/not-executed"], {}, Path("/"), 10.75)
            execve.assert_not_called()

        def wait(pid, flags):
            self.assertEqual((pid, flags), (123, os.WNOHANG))
            self.assertEqual(owner.fds, owned)
            return next(statuses)

        statuses = iter(((0, 0), (123, 4 << 8)))
        with (
            patch.object(supervisor.os, "waitpid", side_effect=wait),
            patch.object(supervisor.time, "sleep"),
            patch.object(supervisor.os, "close") as close,
            patch.object(supervisor.signal, "pidfd_send_signal") as send_signal,
        ):
            owner.retain_until_reaped()
            self.assertEqual(owner.status, 4 << 8)
            close.assert_not_called()
            send_signal.assert_not_called()
            owner.close_all()
            self.assertEqual(close.call_args_list, [call(fd) for fd in owned.values()])

    def test_exec_gate_refuses_timeout_and_late_permit(self):
        with (
            patch.object(supervisor.select, "poll") as poll,
            patch.object(supervisor.os, "read", return_value=b"P"),
            patch.object(supervisor.time, "monotonic", side_effect=(0, 1, 0, 1)),
        ):
            poll.return_value.poll.side_effect = ([], [(8, supervisor.select.POLLIN)])
            self.assertFalse(supervisor.await_exec_permit(8, 1))
            self.assertFalse(supervisor.await_exec_permit(8, 1))

    def test_stopped_status_never_releases_child_handles(self):
        owner = supervisor.Owner()
        owner.pid = 123
        owner.fds = {"pidfd": 8, "artifact": 9}
        stopped = (signal.SIGSTOP << 8) | 0x7f
        self.assertTrue(os.WIFSTOPPED(stopped))
        with (
            patch.object(supervisor.os, "waitpid",
                         side_effect=((123, stopped), (123, signal.SIGKILL))),
            patch.object(supervisor.os, "close") as close,
        ):
            owner.reap_once()
            self.assertIsNone(owner.status)
            self.assertEqual(owner.failure, "nonterminalWaitStatus")
            with self.assertRaisesRegex(supervisor.Refused, "childNotReaped"):
                owner.close_all()
            close.assert_not_called()
            owner.reap_once()
            self.assertTrue(os.WIFSIGNALED(owner.status))
            owner.close_all()
            self.assertEqual(close.call_args_list, [call(8), call(9)])

    def test_trace_or_blocked_cancellation_refuses_launch(self):
        with self.assertRaisesRegex(supervisor.Refused, "tracedLaunch"):
            supervisor.launch_policy(b"TracerPid:\t123\n")
        with patch.object(supervisor.signal, "pthread_sigmask", return_value={signal.SIGTERM}):
            with self.assertRaisesRegex(supervisor.Refused, "blockedCancellationSignals"):
                supervisor.launch_policy(b"TracerPid:\t0\n")

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

    def test_bootstrap_refuses_tampered_supervisor_before_execution(self):
        path = Path("/protected/bootstrap.py")
        manifest = json.dumps({
            "schema": bootstrap.SCHEMA,
            "runtimeApproved": True,
            "sourceFiles": {"supervisor.py": supervisor.digest(b"reviewed")},
        }).encode()
        args = SimpleNamespace(manifest=Path("/approval/manifest.json"),
                               approved_manifest_sha256=supervisor.digest(manifest))
        with (
            patch.object(bootstrap, "__file__", str(path)),
            patch.object(bootstrap, "protected", side_effect=lambda value: value),
            patch.object(Path, "stat", return_value=SimpleNamespace(
                st_mode=stat.S_IFREG | 0o644, st_uid=0)),
            patch.object(bootstrap, "bounded_bytes", side_effect=(manifest, b"tampered")),
        ):
            with self.assertRaisesRegex(ValueError, "supervisorDigest"):
                bootstrap.admit(args)
