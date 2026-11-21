"""Source contract and inert memfd tests; no Bun, library, fork, or observer."""

import errno
import json
import os
from pathlib import Path
import re
import signal
import stat
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, call, patch

import bootstrap
import supervisor


class OwnerContract(unittest.TestCase):
    def terminal_worker(self, phase="admit"):
        owner = supervisor.Owner()
        owner.phase = phase
        owner.pid = 123
        owner.fds = {"output": 8}
        owner.end = 12
        owner.pause = Mock()
        return owner

    def test_early_exit_receipt_distinguishes_preexec_worker_guard_and_signal(self):
        for status, code, number, hint in (
            (22 << 8, 22, None, "execSyscall"),
            (43 << 8, 43, None, "artifactBytes"),
            (signal.SIGSEGV, None, signal.SIGSEGV, None),
        ):
            with (
                self.subTest(status=status),
                patch.object(supervisor.time, "monotonic", return_value=10),
                patch.object(supervisor.os, "waitpid", return_value=(123, status)),
                patch.object(supervisor.os, "read", return_value=b""),
            ):
                owner = self.terminal_worker()
                with self.assertRaisesRegex(supervisor.Refused, "workerResult"):
                    owner.supervise({}, None, hold=False)
                owner.fail("workerResult")
                self.assertEqual(owner.outcome("reply"), (1, {
                    "outcome": "refused", "reason": "workerResult",
                    "worker": {"lastPermit": "P", "lastFrame": None, "frameBytes": 0,
                               "eof": True, "exitCode": code, "exitSignal": number,
                               "exitHint": hint, "nativeResult": None, "protocolContradiction": False},
                }))

    def test_complete_admission_frame_and_load_exit_give_distinct_receipt(self):
        owner = self.terminal_worker()

        def commit(permit):
            self.assertEqual(permit, b"G")
            owner.phase = "load"

        with (
            patch.object(owner, "commit_permit", side_effect=commit) as grant,
            patch.object(supervisor.time, "monotonic", return_value=10),
            patch.object(supervisor.os, "waitpid", side_effect=((0, 0), (123, 47 << 8))),
            patch.object(supervisor.os, "read", side_effect=(b"A\n", b"")),
            patch.object(supervisor.os, "getsid", return_value=123),
            patch.object(supervisor.os, "getpgid", return_value=123),
            patch.object(supervisor, "proc_bytes", return_value=b"TracerPid:\t0\n"),
            patch.object(supervisor, "maps_match") as maps,
        ):
            with self.assertRaisesRegex(supervisor.Refused, "workerResult"):
                owner.supervise({}, None, hold=False)
            grant.assert_called_once_with(b"G")
            maps.assert_called_once_with(123, {})
        self.assertEqual(owner.worker_diagnostic(False), {
            "lastPermit": "G", "lastFrame": "A", "frameBytes": 2, "eof": True,
            "exitCode": 47, "exitSignal": None, "exitHint": "libraryOpen", "nativeResult": None,
            "protocolContradiction": False,
        })

    def test_terminal_drain_cannot_claim_frames_past_the_last_permit(self):
        for phase, hold, permit, frame in (
            ("admit", False, "P", "A"),
            ("load", False, "G", "L"),
            ("load", True, "G", "L"),
        ):
            with self.subTest(phase=phase, hold=hold):
                owner = self.terminal_worker(phase)
                data = b"A\nL\n" + (b"W\n" if hold else b"") + b"R*\0\0\0"
                with (
                    patch.object(supervisor.time, "monotonic", return_value=10),
                    patch.object(supervisor.os, "waitpid", return_value=(123, 0)),
                    patch.object(supervisor.os, "read", side_effect=(data, b"")),
                    patch.object(owner, "commit_permit") as grant,
                ):
                    with self.assertRaisesRegex(supervisor.Refused, "workerResult"):
                        owner.supervise({}, None, hold)
                    grant.assert_not_called()
                self.assertEqual(owner.worker_diagnostic(hold), {
                    "lastPermit": permit, "lastFrame": frame, "frameBytes": len(data),
                    "eof": True, "exitCode": 0, "exitSignal": None, "exitHint": None,
                    "nativeResult": None, "protocolContradiction": True,
                })

    def test_exit_hint_never_claims_progress_past_the_last_permit(self):
        for phase, code in (("preexec", 43), ("admit", 47), ("load", 50)):
            with self.subTest(phase=phase, code=code):
                owner = self.terminal_worker(phase)
                owner.status = code << 8
                owner.eof = True
                receipt = owner.worker_diagnostic(False)
                self.assertEqual(receipt["exitCode"], code)
                self.assertIsNone(receipt["exitHint"])
                self.assertTrue(receipt["eof"])

    def test_final_frame_is_drained_even_if_terminal_status_is_seen_first(self):
        owner = self.terminal_worker("query")
        owner.data.extend(b"A\nL\n")
        with (
            patch.object(supervisor.time, "monotonic", return_value=10),
            patch.object(supervisor.os, "waitpid", return_value=(123, 0)),
            patch.object(supervisor.os, "read", side_effect=(b"R*", b"\0\0\0", b"")),
            patch.object(owner, "commit_permit") as grant,
        ):
            owner.supervise({}, None, hold=False)
            grant.assert_not_called()
        self.assertEqual(owner.outcome("reply"), (0, {
            "outcome": "fixtureOnly", "case": "reply", "workerReaped": True,
        }))

    def test_result_category_requires_complete_frame_and_never_reports_raw_value(self):
        owner = self.terminal_worker("query")
        owner.status = 4 << 8
        owner.eof = True
        cases = (
            (b"A\nL\nR\xfe", False, "L", None, False),
            (b"A\nL\nR" + (-2).to_bytes(4, "little", signed=True), False, "R", "badDescriptor", False),
            (b"A\nL\nRtest", False, "R", "unexpected", False),
            (b"A\nL\nRtestExtra", False, "L", None, True),
            (b"A\nX\n", False, "A", None, True),
            (b"A\nL\nW\nR" + (-3).to_bytes(4, "little", signed=True), True, "R", "cancelled", False),
        )
        for data, hold, frame, result, contradiction in cases:
            with self.subTest(data=data):
                owner.data = bytearray(data)
                receipt = owner.worker_diagnostic(hold)
                self.assertEqual((receipt["lastFrame"], receipt["nativeResult"]), (frame, result))
                self.assertLessEqual(receipt["frameBytes"], 12)
                self.assertIs(receipt["protocolContradiction"], contradiction)
                self.assertNotIn("test", json.dumps(receipt))

    def test_prefork_tamper_output_is_unchanged(self):
        owner = supervisor.Owner()
        owner.fail("libraryDigest")
        self.assertEqual(owner.outcome("reply"), (1, {"outcome": "refused", "reason": "libraryDigest"}))

    def test_worker_exit_code_contract_matches_supervisor(self):
        source = (supervisor.HERE / "worker.mjs").read_text()
        table = re.search(r"const FAILURE = \{([^}]+)\}", source)
        self.assertIsNotNone(table)
        codes = [int(value) for value in re.findall(r": (\d+),", table[1])]
        self.assertEqual(sorted(codes), sorted(code for code in supervisor.EXIT_HINTS if code >= 40))

    def permit_owner(self, phase):
        owner = supervisor.Owner()
        owner.phase = phase
        owner.fds = {"gate": 8, "control": 9, "pidfd": 10}
        owner.end = 12
        return owner

    def test_permits_commit_once_in_order(self):
        owner = self.permit_owner("preexec")
        with (
            patch.object(supervisor, "STOP", False),
            patch.object(supervisor.signal, "pthread_sigmask", return_value=set()),
            patch.object(supervisor.signal, "sigpending", return_value={signal.SIGUSR1}),
            patch.object(supervisor.time, "monotonic", return_value=10),
            patch.object(supervisor.os, "write", return_value=1) as write,
        ):
            self.assertTrue(owner.commit_permit(b"P", gate_end=11))
            self.assertTrue(owner.commit_permit(b"G"))
            self.assertTrue(owner.commit_permit(b"Q"))
            with self.assertRaisesRegex(supervisor.Refused, "permitOrder"):
                owner.commit_permit(b"Q")
            self.assertEqual(owner.phase, "query")
            self.assertIsNone(owner.failure)
            self.assertEqual(write.call_args_list, [call(8, b"P"), call(9, b"G"), call(9, b"Q")])

    def test_all_permits_refuse_a_signal_pending_after_an_earlier_checkpoint(self):
        for permit, (_, phase, _) in supervisor.PERMITS.items():
            with self.subTest(permit=permit):
                owner = self.permit_owner(phase)
                old = {signal.SIGUSR1}
                with (
                    patch.object(supervisor, "STOP", False),
                    patch.object(supervisor.time, "monotonic", return_value=10),
                    patch.object(supervisor.signal, "pthread_sigmask", return_value=old) as mask,
                    patch.object(supervisor.signal, "sigpending",
                                 return_value={signal.SIGTERM}),
                    patch.object(supervisor.os, "write") as write,
                ):
                    owner.checkpoints()
                    self.assertIsNone(owner.failure)
                    options = {"gate_end": 11} if permit == b"P" else {}
                    self.assertFalse(owner.commit_permit(permit, **options))
                    self.assertEqual(owner.failure, "cancelled")
                    self.assertEqual(owner.phase, phase)
                    write.assert_not_called()
                    self.assertEqual(mask.call_args_list, [
                        call(signal.SIG_BLOCK, supervisor.SIGNALS), call(signal.SIG_SETMASK, old)
                    ])

    def test_latched_cancellation_while_entering_mask_prevents_publish(self):
        owner = self.permit_owner("admit")

        def mask(action, _signals):
            if action == signal.SIG_BLOCK:
                supervisor.request_stop(signal.SIGINT, None)
            return set()

        with (
            patch.object(supervisor, "STOP", False),
            patch.object(supervisor.signal, "pthread_sigmask", side_effect=mask),
            patch.object(supervisor.signal, "sigpending", return_value=set()),
            patch.object(supervisor.time, "monotonic", return_value=10),
            patch.object(supervisor.os, "write") as write,
        ):
            owner.checkpoints()
            self.assertIsNone(owner.failure)
            self.assertFalse(owner.commit_permit(b"G"))
            self.assertEqual(owner.failure, "cancelled")
            write.assert_not_called()

    def test_deadline_is_checked_inside_commit_for_run_and_exec_gate(self):
        for permit, phase, cutoff, options, reason in (
            (b"Q", "load", 12, {}, "deadline"),
            (b"P", "preexec", 11, {"gate_end": 11}, "execNotPermitted"),
        ):
            with self.subTest(permit=permit):
                owner = self.permit_owner(phase)
                # Time advances after the earlier checkpoint, inside the mask.
                clock = [10]

                def pending():
                    clock[0] = cutoff
                    return set()

                with (
                    patch.object(supervisor, "STOP", False),
                    patch.object(supervisor.signal, "pthread_sigmask", return_value=set()) as mask,
                    patch.object(supervisor.signal, "sigpending", side_effect=pending),
                    patch.object(supervisor.time, "monotonic", side_effect=lambda: clock[0]),
                    patch.object(supervisor.os, "write") as write,
                ):
                    owner.checkpoints()
                    self.assertFalse(owner.commit_permit(permit, **options))
                    self.assertEqual(owner.failure, reason)
                    self.assertEqual(owner.phase, phase)
                    write.assert_not_called()
                    self.assertEqual(mask.call_args_list[-1], call(signal.SIG_SETMASK, set()))

    def test_pending_after_commit_keeps_sent_permit_but_prevents_next_one(self):
        owner = self.permit_owner("preexec")
        with (
            patch.object(supervisor, "STOP", False),
            patch.object(supervisor.signal, "pthread_sigmask", return_value=set()),
            patch.object(supervisor.signal, "sigpending",
                         side_effect=(set(), {signal.SIGTERM}, set(), set())),
            patch.object(supervisor.time, "monotonic", return_value=10),
            patch.object(supervisor.os, "write", return_value=1) as write,
        ):
            self.assertTrue(owner.commit_permit(b"P", gate_end=11))
            self.assertEqual(owner.phase, "admit")
            self.assertEqual(owner.failure, "cancelled")
            self.assertFalse(owner.commit_permit(b"G"))
            write.assert_called_once_with(8, b"P")

    def test_delivery_on_unmask_and_deadline_after_write_stop_further_work(self):
        for reason in ("cancelled", "deadline"):
            with self.subTest(reason=reason):
                owner = self.permit_owner("admit")
                clock = [10]

                def mask(action, _signals):
                    if action == signal.SIG_SETMASK and reason == "cancelled":
                        supervisor.request_stop(signal.SIGTERM, None)
                    return {signal.SIGUSR1}

                def publish(_fd, _permit):
                    if reason == "deadline":
                        clock[0] = 12
                    return 1

                with (
                    patch.object(supervisor, "STOP", False),
                    patch.object(supervisor.signal, "pthread_sigmask", side_effect=mask) as masks,
                    patch.object(supervisor.signal, "sigpending", return_value=set()),
                    patch.object(supervisor.time, "monotonic", side_effect=lambda: clock[0]),
                    patch.object(supervisor.os, "write", side_effect=publish) as write,
                ):
                    self.assertTrue(owner.commit_permit(b"G"))
                    self.assertEqual(owner.phase, "load")
                    self.assertEqual(owner.failure, reason)
                    self.assertFalse(owner.commit_permit(b"Q"))
                    write.assert_called_once_with(9, b"G")
                    self.assertEqual(masks.call_args_list[-1],
                                     call(signal.SIG_SETMASK, {signal.SIGUSR1}))

    def test_failed_write_restores_mask_and_never_retries(self):
        owner = self.permit_owner("load")
        with (
            patch.object(supervisor, "STOP", False),
            patch.object(supervisor.signal, "pthread_sigmask", return_value=set()) as mask,
            patch.object(supervisor.signal, "sigpending", return_value=set()),
            patch.object(supervisor.time, "monotonic", return_value=10),
            patch.object(supervisor.os, "write",
                         side_effect=BlockingIOError(errno.EAGAIN, "mock")) as write,
        ):
            with self.assertRaises(BlockingIOError):
                owner.commit_permit(b"Q")
            self.assertEqual(owner.phase, "load")
            write.assert_called_once_with(9, b"Q")
            self.assertEqual(mask.call_args_list[-1], call(signal.SIG_SETMASK, set()))

    def test_mismatched_seal_exports_refuse_before_creating_artifact(self):
        for name, correct in supervisor.SEAL_UAPI.items():
            with (
                self.subTest(name=name),
                patch.object(supervisor, "fcntl", SimpleNamespace(**{name: correct + 1})),
                patch.object(supervisor.os, "memfd_create") as create,
            ):
                with self.assertRaisesRegex(supervisor.Refused, "memfdSealConstantMismatch"):
                    supervisor.Owner().seal_artifact(b"inert")
                create.assert_not_called()

    def test_seal_commands_refuse_other_platforms_and_32_bit_abi(self):
        for module, name, value in (
            (supervisor.sys, "platform", "freebsd"),
            (supervisor.platform, "machine", lambda: "aarch64"),
            (supervisor.sys, "byteorder", "big"),
            (supervisor.struct, "calcsize", lambda fmt: 4 if fmt != "@P" else 8),
        ):
            with (
                self.subTest(name=name),
                patch.object(module, name, value),
                patch.object(supervisor.os, "memfd_create") as create,
            ):
                with self.assertRaisesRegex(supervisor.Refused, "memfdSealAbi"):
                    supervisor.Owner().seal_artifact(b"inert")
                create.assert_not_called()

    def test_inert_memfd_roundtrip_with_no_python_constant_exports(self):
        owner = supervisor.Owner()
        payload = b"Braid fixture test: inert bytes only\n"
        kernel_fcntl = supervisor.fcntl.fcntl
        try:
            # Exercise the same path as pinned CPython, even if this test is
            # also run on a CPython that exports the seal constants.
            with patch.object(supervisor, "fcntl", SimpleNamespace(fcntl=kernel_fcntl)):
                fd = owner.seal_artifact(payload)
            self.assertEqual(kernel_fcntl(fd, 1034), 0x0f)
            self.assertEqual(os.pread(fd, len(payload) + 1, 0), payload)
            with self.assertRaises(PermissionError) as blocked:
                os.pwrite(fd, b"x", 0)
            self.assertEqual(blocked.exception.errno, errno.EPERM)
        finally:
            owner.close_all()

    def test_seal_apply_mask_and_byte_mismatches_do_not_admit(self):
        payload = b"inert"
        for results, returned, reason in (
            ((1,), payload, "sealApply"),
            ((0, 0x0e), payload, "sealMask"),
            ((0, 0x1f), payload, "sealMask"),
            ((0, 0x0f), b"wrong", "sealedBytes"),
            ((0, 0x0f), payload + b"!", "sealedBytes"),
        ):
            with self.subTest(results=results, returned=returned):
                owner = supervisor.Owner()
                operation = Mock(side_effect=results)
                with (
                    patch.object(supervisor, "fcntl",
                                 SimpleNamespace(fcntl=operation, **supervisor.SEAL_UAPI)),
                    patch.object(supervisor.os, "memfd_create", return_value=8),
                    patch.object(supervisor.os, "write", return_value=len(payload)),
                    patch.object(supervisor.os, "pread", return_value=returned) as read,
                    patch.object(supervisor.os, "close") as close,
                ):
                    try:
                        with self.assertRaisesRegex(supervisor.Refused, reason):
                            owner.seal_artifact(payload)
                        self.assertEqual(operation.call_args_list, [
                            call(8, 1033, 0x0f), call(8, 1034)
                        ][:len(results)])
                        if reason != "sealedBytes":
                            read.assert_not_called()
                    finally:
                        owner.close_all()
                    close.assert_called_once_with(8)

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
