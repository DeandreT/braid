# Standalone Bun native fixture

This Linux x86-64 test is separate from the Python capture owner, browser
observers, and production libraries. It does not create qualification markers
or accept `--output`. `fixtureOnly` is a test result, not host qualification.
The [proposed manifest](artifact-manifest.proposed.json) is deliberately not
runnable. An independently approved manifest and static ELF review are required
before any runtime test.

## Ownership and proof

`supervisor.py` is the only supervisor and reaper. It requires a single,
untraced, unprivileged CPython process with SIGINT and SIGTERM unblocked and
only fds 0–2 on entry. Before fork it checks the inherited hard fd limit and
the exact open set. The forked Python child closes its copy of the dedicated
gate writer and waits for `P` for at most 750 ms from the pre-fork clock. The
parent writes `P` only after obtaining a checked pidfd for that child and
settling its unused child endpoint copies. Without a pidfd, it shuts down the
gate's write side to deliver EOF, without closing the retained handles; the
child's absolute deadline is the backstop if shutdown or delivery fails. EOF,
a wrong byte, and a late permit never lead to exec.

After `P`, the child calls `setsid`, duplicates checked original fds using
full-width integers, and installs exactly the four reviewed slots (two
separate Unix socketpair ends, `/dev/null`, sealed library). It rechecks
tracing and its signal mask before exec and never spawns another process.
The parent has no numeric-PID or group signal fallback. The C library keeps
full-width descriptors locally; its FFI surface accepts no descriptors or
pointers. No Bun socket backend or Bun worker is under test.

| Bytes | Direction | Meaning |
| --- | --- | --- |
| `P` | Parent → forked Python child (separate socket) | The parent owns a checked pidfd; exec may proceed within the gate deadline. |
| `A\n` | Child → parent | The worker checked the fixed stdio types and the sealed artifact's bytes. |
| `G` | Parent → child | The parent verified session/group isolation and pre-load file mappings. |
| `L\n` | Child → parent | `dlopen` returned; no fixture function was called. |
| `Q` | Parent → child | Exact post-load mapping inventory checked, including the sealed inode. |
| `W\n` | Child → parent | Hold only: C is about to poll. Not proof of being blocked. |
| `C` | Parent → child | One cancellation byte. EOF alone is not cooperative cancellation. |
| `R` + signed little-endian i32 | Child → parent | Native return after closing C-owned descriptors. |

The normal case needs `R/42`, EOF and reaped status 0. The hold case samples
only `/proc/<child>/task/<child>/syscall`, after `W`, for Linux x86-64
`poll`/`ppoll` with exactly two fds. It then needs an actual `C` write, `R/-3`,
EOF, and reaped status 3 within 250 ms without signal escalation. Missing proc
access, a late sample, a signal, a slow exit, or an incomplete or unexpected
frame makes the hold case unproven. The worker's two admission waits time out
independently, and the native poll has its own absolute 4 s backstop.

The main 2 s clock starts before fork and includes final owned closes. On a
stop, the supervisor gives 250 ms for cooperative exit, then sends SIGTERM,
sends SIGKILL 250 ms later if needed, and allows another 1 s for reaping. The
latter is an observation bound, not a guarantee about Linux scheduling. If
missed, `workerStopUnconfirmed` is sticky; the same supervisor keeps reaping
with its handles intact and no success can be emitted. A readable pidfd is
only a wakeup. Only `WIFEXITED` or `WIFSIGNALED` settles process custody;
a stopped or continued status is a failed run and does not release any live
handles. Final success also requires normal exit, the frame contract, EOF,
clean final closes, and the final cancellation/deadline check. The reporting
cutoff is the check with SIGINT/SIGTERM blocked after all owned closes.

## Artifact review requirements

- Review the exact inputs and compile/link command in the proposed manifest.
  The future `elf-review.json` is a separate record: schema
  `braid.native-bun-fixture.elf-review.v1`, disposition `admitted`, and fields
  `bunSha256`, `loaderSha256`, `supervisorInterpreterSha256`, `librarySha256`,
  and `dependencies` matching the approved manifest. The independent review
  must record the actual ELF/loader evidence described in the proposed
  manifest. The supervisor binds that review digest; it does not infer ELF
  dependency policy from a SONAME or a map of filenames.
- Admit CPython's exact loader, libraries, stdlib and extensions **before**
  starting the protected bootstrap. Independently verify `bootstrap.py`
  against the approved manifest before invoking it through the real CPython
  path with a clean environment and `-I -S -B`. Use a canonical, root-owned,
  non-user-writable source directory for the bootstrap and its fixed sibling
  `supervisor.py`; do not execute the checkout copy or start the supervisor
  directly. The bootstrap verifies the independently supplied manifest pin,
  checks the opened supervisor file and hashes the exact bytes before any
  supervisor code runs. It executes that verified in-memory buffer, not an
  import or a second pathname lookup. The supervisor's later interpreter
  check is consistency only, not its bootstrapping trust decision.
- For Bun, admit only the reviewed official runtime, canonical root-controlled
  paths and whole dependency closure. The supervisor uses the exact approved GNU
  loader, no cache or hardware-capability subdirectory preference, `/usr/lib`
  as its only explicit library directory, a clean environment and fixed argv.
  Static review must establish that no object falls through to another path
  or loads other code before admission. The mapping checks then tie the actual
  child to approved inodes before granting each permit. There is no alternate
  runtime, interpreter, loader, `.so`, dependency or proc access route.
- A future run uses one private, nonprivileged work directory containing only
  `libbraid_fixture_v1.so`, the correct separately admitted manifest and its
  sibling `elf-review.json`. Pass the manifest SHA-256 via an independent
  approval channel, not by hashing an operator-provided manifest just before
  invocation. The fixture's standalone executable cap does not qualify Bun for
  the capture owner's separate 64 MiB limit.

The proposed manifest references the original design and the independent
source-only review that led to this pre-exec gate and protected bootstrap.
Neither admits a runtime. Before promoting the candidate, cover the reply
and observed hold paths; refusal on wrong artifacts, tracing, blocked
cancellation, unexpected inodes or inherited fds; no success on
deadline/cancellation/close error; pidfd failure with no exec; and retention
of custody until terminal wait even after a stopped status or missed bound.
