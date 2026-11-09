# Qualification capture ownership

Root-operated host observers such as `tools/catalog-browser/observe-host.py` and
`tools/project-creation-browser/observe-host.py` write qualification evidence into a
private output directory. `tools/qualification-capture/capture.py` owns one such run
as a capture and records a durable, truthful terminal outcome. A crash, deadline,
cancellation, driver failure or close failure can never leave a record that reads as
success. The owner is Python standard library only and runs on the Linux
qualification host; the reader and marker primitives are portable.

## Capture layout

`<root>/<captureId>/` is created exclusively under an owned `0700` root. The
capture ID is a fresh UUID.

- `header.json`: schema `weft.qualification-capture.v1`, capture ID, creation time,
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

- Success is one immutable `complete.exit` attempt with `exit: 0`. It binds the
  SHA-256 of the exact `header.json` and `manifest.json` bytes.
- Failure first publishes `complete.revoked` with the failure category. Only after
  that revocation is durable is the failure encoded: `complete.exit` with `exit: 1`
  and the category, plus the owner's nonzero exit status. An existing revocation or
  exit marker is kept as it is.
- `complete.revoked` overrides any `complete.exit`. A success attempt that loses a
  race to an existing `complete.exit` revokes as `terminalWriteFailure`.

The reader derives the outcome from the markers:

| Markers | Outcome |
| --- | --- |
| none | `incomplete` (running, crashed or killed). Never qualifies. |
| `complete.revoked` (with or without `complete.exit`) | `revoked` with its category. |
| `complete.exit` with `exit: 0` only | `passed`, after full replay. |
| nonzero `complete.exit` without revocation | protocol violation (invalid). |

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

After creating the layout and header, the owner installs SIGINT/SIGTERM handlers that
only set a cancellation flag. It then follows a fixed order:

1. Drive the child (the managed child close), then check cancellation.
2. Close the layout: write `driver.stdout`, fsync every evidence file and directory,
   write `manifest.json`. Then check cancellation again.
3. Restore the original signal handlers, then check cancellation a final time.
4. Call `finish(capture, failure)`.

`finish` is the single source-owned terminal and the only code that writes
`complete.exit` or `complete.revoked`. Every normal, failure, layout-close and
late-cancellation path selects a failure category or none, then reaches this same
finisher. Success is selected only when no category was selected, after the outer
layout close and handler restoration. Nothing in the protocol uses `os.replace`.

Failure categories: `cancelled`, `deadline`, `driverStartFailure`, `driverFailed`,
`outputLimit`, `outputDidNotSettle`, `layoutCloseFailure`, `ownerFailure` and
`terminalWriteFailure`.

## Cancellation and deadline ownership

The main thread owns the monotonic deadline, the cancellation flag and every effect:
terminating or killing the child, and writing records and markers. The only blocking
query it delegates is reading the child's standard output. That runs in one bounded
daemon worker thread, which only appends to an in-memory buffer limited to 1 MiB.
The main thread waits on the child in 50 ms slices, so cancellation and the deadline
are checked continuously. On cancellation, deadline or an output overflow, it sends
SIGTERM, waits 5 s, then sends SIGKILL. It joins the worker for at most 5 s. A worker
that has not settled becomes `outputDidNotSettle` rather than blocking the terminal.

The owner uses no native library or FFI runtime. It never stores descriptors in a
narrow field or blocks the main thread on a native call.

## Historical reader

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
`unreadableCapture`. The frozen historical captures under
`tools/qualification-capture/fixtures/` are replayed against their golden files in
CI. They are excluded from Prettier and Git text conversion so their bytes stay
exact.

## Observation driver

The first driver is an existing observer, for example:

```sh
python3.12 tools/qualification-capture/capture.py run --root /var/lib/weft-qualification \
  --deadline-seconds 900 --trusted /opt/weft/tools/catalog-browser/observe-host.py -- \
  /usr/bin/python3.12 /opt/weft/tools/catalog-browser/observe-host.py --fixture-id <UUID>
```

The owner appends `--output <capture>/evidence`, which already satisfies the
observers' empty, owned, `0700` output check. The driver inherits the owner's
standard input, so the operator's `finish` control reaches the observer unchanged.
The driver's standard error is discarded, consistent with categorical host output.
The owner prints one JSON line with the capture ID and outcome. It exits 0 only for
`passed`, 1 for a revoked capture and 2 for a refusal or an uncertain terminal record.

## Gates

- **Accepted socket descriptor range and native library provenance/ABI:** these do
  not apply because the owner uses no native library, FFI runtime or event-loop
  descriptor encoding. Adding one requires a new range proof and provenance/ABI
  evidence before that library is first opened.
- **Trusted-code inventory:** recorded by preflight in every header. The operator must
  still compare those digests with reviewed source before accepting a capture.
- **Host fit:** preflight checks runtime type, platform, root ownership and mode, and
  trusted-file ownership. Running in the exclusive Docker window, as root, with the
  observer's own host checks, remains an operator gate. No privileged installation or
  activation is part of this tool.
- **Non-dumpability:** captures make no non-dumpability claim; see issue #396.

## Operator steps

These steps are run by the operator; the tool does not perform them.

1. Install the reviewed `capture.py` and the observer as root-owned `0644` files, for
   example under `/opt/weft/tools/`, with a root-owned `/usr/bin/python3.12`.
2. Create the capture root as root, `0700`, outside the checkout.
3. Run the owner as root in the exclusive window with the observer as its driver.
   Send `finish` to its standard input after operator cleanup has settled.
4. Replay with `capture.py read <capture>`. Accept only `passed`, after comparing the
   header's trusted-code digests with the reviewed source.
