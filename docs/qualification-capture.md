# Qualification capture ownership

Root-operated host observers such as `tools/catalog-browser/observe-host.py` and
`tools/project-creation-browser/observe-host.py` write qualification evidence into a
private output directory. `tools/qualification-capture/capture.py` owns one such run
as a capture and records a durable terminal outcome. Observed deadline expiry,
cancellation, driver failure and close failure do not qualify. The owner is Python
standard library only and runs on the Linux qualification host; the reader and
marker primitives are portable.

## Capture layout

`<root>/<captureId>/` is created exclusively under an owned `0700` root. The
capture ID is a fresh UUID.

- `header.json`: schema `braid.qualification-capture.v1`, capture ID, creation time,
  deadline, exact driver argv, runtime identity and the trusted-code inventory.
- `evidence/`: an empty `0700` directory passed to the driver as `--output`.
- `driver.stdout`: the driver's standard output, bounded to 1 MiB.
- `manifest.json`: name, size and SHA-256 of `driver.stdout` and every evidence file.
- `complete.exit` and `complete.revoked`: the terminal markers.

The header, manifest and markers are canonical JSON (sorted keys, compact, one
trailing newline). Files are `0600`.

## Terminal marker protocol

Each record is published with no-replace semantics. Its bytes go to a unique
temporary file, are fsynced, and are hard-linked to the final name. The link is
atomic and fails if the name exists. The directory is then fsynced. A marker is
either absent or complete, and no attempt can overwrite an earlier one, including
concurrent attempts.

The link can be visible before the directory sync or close finishes. A live read,
or a read after an owner that did not return status 0, is not an acknowledgment of
a durable success on a new capture.

- Success is one immutable `complete.exit` attempt with `exit: 0`. It binds the
  SHA-256 of the exact `header.json` and `manifest.json` bytes.
- Failure first publishes `complete.revoked` with the failure category. Only after
  that revocation is durable is the failure encoded: `complete.exit` with `exit: 1`
  and the category, plus the owner's nonzero exit status. An existing revocation or
  exit marker is kept as it is.
- `complete.revoked` overrides any `complete.exit`. A success attempt that loses a
  race to an existing `complete.exit` revokes as `terminalWriteFailure`.

The reader derives the outcome from the markers:

| Markers                                              | Outcome                                                     |
| ---------------------------------------------------- | ----------------------------------------------------------- |
| none                                                 | `incomplete` (running, crashed or killed). Never qualifies. |
| `complete.revoked` (with or without `complete.exit`) | `revoked` with its category.                                |
| `complete.exit` with `exit: 0` only                  | `passed`, after full replay.                                |
| nonzero `complete.exit` without revocation           | protocol violation (invalid).                               |

If the revocation itself cannot be published, the owner prints outcome `uncertain`
and exits with status 2 without encoding a failure. The capture then reads as `incomplete`, or as
the earlier marker state if one already exists.

## Owner graph and the finisher

`run_capture` is the whole-entry owner. Preflight refuses before anything is created:

- the driver argv must be a nonempty list of strings with an absolute executable;
- the deadline must be a finite number in (0, 86400] and not a boolean;
- the root must be absolute, a real directory owned by the effective user, and `0700`;
- every trusted file (the owner, the driver executable, each `--trusted` path) must be
  an absolute, non-symlink regular file owned by root or the effective user, with no
  group or world write permission. Its SHA-256 becomes the trusted-code inventory.

After creating the layout and header, the owner installs SIGINT/SIGTERM handlers
that only set a cancellation flag and starts the monotonic capture deadline. It
then follows a fixed order:

1. Drive the child and close its stdout. Both the direct child's exit status and
   stdout EOF must be observed before the deadline, even if they arrive in the
   final polling slice.
2. If the driver and stdout settled, close the layout: write `driver.stdout`,
   fsync every evidence file and directory, and write `manifest.json`. Otherwise,
   record the failure without collecting potentially live evidence.
3. Mask SIGINT/SIGTERM before restoring the previous handlers. Just before
   linking the staged success marker, the finisher checks the flag, newly
   pending cancellation and the deadline. It records and consumes pending
   cancellation before restoring any `SIG_IGN` disposition, which would
   discard the signal. After this check, signals belong to the restored
   handlers or dispositions, and the original mask applies when publication
   returns.
4. Return success only after terminal publication, including directory sync and
   close. A publication failure uses the same revocation protocol.

`finish` is the single source-owned terminal and the only code that writes
`complete.exit` or `complete.revoked`. Every normal, failure, layout-close and
late-cancellation path reaches this same finisher. Cancellation takes precedence
over expiry when both are observed at the same check. An already selected failure
is not cleared by a late child success; an unconfirmed stop takes precedence over
the reason for stopping. Nothing in the protocol uses `os.replace`.

Failure categories: `cancelled`, `deadline`, `driverStartFailure`, `driverFailed`,
`driverStopUnconfirmed`, `outputLimit`, `outputDidNotSettle`, `layoutCloseFailure`,
`ownerFailure` and `terminalWriteFailure`.

## Cancellation and deadline ownership

The owner polls the direct child and a nonblocking stdout pipe in slices of at
most 50 ms, checking the remaining time and cancellation between operations.
Stdout is capped at 1 MiB. If the child exits first, stdout must reach EOF within
5 seconds of observing the exit and before the capture deadline.

If the direct child has not exited when the run fails, the owner requests
SIGTERM and allows up to 5 seconds to reap it. If that does not confirm exit,
it requests SIGKILL and allows up to 5 more seconds. If neither wait confirms
exit, the owner closes its pipe end and revokes as `driverStopUnconfirmed`,
without writing a manifest. This is not proof that the driver or its descendants
stopped. Verify that the observer, its descendants and its resources have
settled before releasing the qualification window or retrying.

The capture deadline is a cutoff for success, not a hard bound on launch,
filesystem calls, cleanup, or terminal publication. The owner uses no native
library or FFI runtime. Run it as a standalone process, with the default
SIGCHLD disposition and no other code reaping its direct child.

## Replay reader

`read_capture(path, golden=None)` (CLI: `capture.py read <capture> [--golden <file>]`)
replays a recorded capture:

1. It requires a real directory whose entries are a subset of the layout names. It
   checks that `header.json` is canonical, has the current schema and binds the
   directory's capture ID.
2. For a `passed` capture, it checks that `complete.exit` binds the current header
   and manifest digests before replaying anything.
3. It admits the whole member set before reading any member. The manifest's names
   must exactly equal `driver.stdout` plus the entries in `evidence/`. Every member is
   opened without following symlinks and must be a regular file with the same inode
   as its `lstat` and the recorded size. Only then are the members read and hashed.
4. With a golden fixture, it compares the terminal notice (outcome and category) and
   the exact member-to-digest map.
5. All descriptors are closed even when a close fails. The first close failure is
   then reported as an invalid capture.

Any inconsistency raises `CaptureInvalid` with a category; unreadable files are
`unreadableCapture`. The fixed capture fixtures under
`tools/qualification-capture/fixtures/` are replayed against their golden files in
CI. They are excluded from Prettier and Git text conversion so their bytes stay
exact.

## Observation driver

Use an observer as the driver, for example:

```sh
PY=/opt/braid-runtimes/cpython-3.15.2/bin/python3.15
"$PY" -I -B /opt/braid/tools/qualification-capture/capture.py run --root /var/lib/braid-qualification \
  --deadline-seconds 900 --trusted /opt/braid/tools/catalog-browser/observe-host.py -- \
  "$PY" -I -B /opt/braid/tools/catalog-browser/observe-host.py --fixture-id "$FIXTURE_ID"
```

The owner appends `--output <capture>/evidence`, which already satisfies the
observers' empty, owned, `0700` output check. The driver inherits the owner's
standard input, so the operator's `finish` control reaches the observer unchanged.
The driver's standard error is discarded, consistent with categorical host output.
The owner prints one JSON line with the capture ID and outcome. It exits 0 only for
`passed`, 1 for a revoked capture and 2 for a refusal or an uncertain terminal record.

## Operator steps

1. Install the reviewed `capture.py` and the observer as root-owned `0644` files, for
   example under `/opt/braid/tools/`. Use the same reviewed, root-owned CPython
   executable for the owner and the Python observer. Pin its absolute path and
   verify its distribution checksum; do not resolve `python3` through `PATH`.
   Before changing the pin, test the owner from the reviewed checkout:

   ```sh
   "$PY" -I -B -m unittest discover -s tools/qualification-capture -p test_capture.py
   ```

   This does not start a privileged observer or update either installed script.
   Compare the installed scripts to the reviewed source before running a capture.

2. Create the capture root as root, `0700`, outside the checkout.
3. Run the owner as root in the exclusive window with the observer as its driver.
   Send `finish` to its standard input after operator cleanup has settled.
4. Require owner exit status 0, then replay with `capture.py read <capture>`.
   Accept only `passed`, after comparing the header's trusted-code digests
   with the reviewed source.
