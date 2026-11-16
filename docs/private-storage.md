# Private artifact storage

`IPrivateObjectStore` stores and verifies exact artifact bytes. Application hosts
use [claim-aware storage](private-storage-v2.md); this page describes the local
filesystem primitives beneath that contract.

## Reference and authority

`PrivateObjectId` combines the full organization/project/mapping/revision scope,
a server-generated nonzero UUID, and the closed native/derived object class.
`PrivateObjectReference` adds SHA-256 and an exact byte count. Neither contains a
physical path, URL, credential, or browser-selected storage key. `Create`
generates an ID; `Restore` reconstructs typed facts loaded by server persistence.

Publication and reads require the expected complete revision scope. A scope
mismatch fails before storage I/O. Matching references do not grant permission:
application services must obtain fresh project authority and load the scoped
reference before calling the store. The storage boundary cannot authenticate a
caller who deliberately supplies a coherent foreign scope and reference.

`PublishCreateOnlyAsync` consumes the caller-owned stream from its current
position. It verifies the complete incoming length and digest before publication,
including retries. It returns `Created` or, after verifying the existing object,
`AlreadyPresent`. Different existing content conflicts; the store never repairs
or overwrites it. `ReadVerifiedAsync` returns an owned byte array only after the
complete file passes length, EOF, and digest checks.

Native objects must be nonempty and at most 4 MiB. Derived objects may be empty
and are independently bounded at 16 MiB. Configuration can lower these limits
but cannot raise them. Derived storage does not broaden Ferrule native admission,
worker response budgets, or public report budgets. Streaming uses 64 KiB chunks;
per-instance concurrency is configurable from one to four operations, default
two. This bounds adapter work, not deployment-wide storage or tenant quotas.

## Local profile and initialization

The local adapter requires Linux x86-64 filesystem primitives.
Azure Blob Storage has its own [Blob profile](private-storage-blob.md). Only ext4, Btrfs, XFS, tmpfs, and overlay filesystem
families are admitted; opening also probes the actual create-only primitive.

`FileSystemStorageOptions` requires an explicit canonical absolute `RootPath`
and `ApplicationContentRoot`, with an optional `RepositoryRoot`. Roots beneath
the application/repository trees or detected Git ancestors are refused. Relative
paths, filesystem root, dot segments, duplicate separators, trailing aliases,
symlinks, and magic links are refused. Missing ancestor directories are not
created, and existing directories are not silently adopted or chmodded.

`FileSystemPrivateObjectStore.Initialize(options)` creates a new dedicated
root. `Open(options)` reopens an existing validated store. The marker is
`.braid-private-storage`, containing ASCII `braid-private-storage-v1`, a newline,
a nonzero lowercase root UUID in `N` format, and a final newline. An absent,
malformed, unknown, or unsafe marker fails opening.

Existing ancestors must be owned by the service UID or privileged root and
cannot be writable by other accounts; a root-owned mode 01777 temporary parent
is admitted. Checks retain the parent descriptor when creating/opening the
store. No existing parent permissions or ownership are repaired.

Directories are mode 0700, unpublished files 0600, and the marker and published
objects 0400, all owned by the effective service UID. Objects must be regular
files with exactly one hard link. The private internal layout is:

```text
objects/{native|derived}/{organization:N}/{project:N}/{mapping:N}/{revision:N}/{object:N}.blob
```

All variable names come from typed nonzero UUIDs. Held directory descriptors and
Linux `openat2` constrain resolution; symlinks and mount traversal below the root
are refused. Checks operate on held file descriptors rather than relying on
pathname checks followed by an unrestricted open. The admitted x86-64 ABI uses
`openat2` syscall 437 and `statx` metadata checks.
[Linux path resolution](https://man7.org/linux/man-pages/man2/openat2.2.html),
[descriptor metadata](https://man7.org/linux/man-pages/man2/statx.2.html).

## Publication and failure

Each attempt exclusively creates an unpredictable private temporary file in the
final object's directory. The adapter streams and hashes the bounded body,
checks exact EOF, flushes the complete file, and closes its writable handle.
It then publishes with `renameat2(RENAME_NOREPLACE)` on the same directory
descriptor. There is no ordinary rename, copy, or replacement fallback.
Readers see an absent final name or complete published bytes.
[Linux no-replace publication](https://man7.org/linux/man-pages/man2/rename.2.html).

Cooperative cancellation before publication cleans only that attempt's
unpublished file. A completed publication returns success even if cancellation
arrives afterward. Arbitrary caller streams and synchronous filesystem flushing
are not forcibly cancelable under a hard deadline. Missing, scope mismatch,
conflict, integrity, limit, unsafe storage, I/O, and unsupported profile failures
have bounded internal categories; cancellation uses `OperationCanceledException`.
Physical paths and native input text do not enter storage error messages.

A killed writer may leave its private incomplete attempt. Reopening performs
no sweep. The final object remains absent unless publication completed; a
matching retry can recover a completed publication. [Ingestion](native-revision-ingestion.md)
supplies durable reservations and cross-store publication recovery;
[maintenance](maintenance-command.md) handles abandoned attempts. This contract exposes no listing, unverified stream,
or final-object deletion API.

File flushing is requested before publication; directory entries are not synced.
Use persistent storage when objects must survive host replacement.
Effective-service-user compromise and privileged administration
are outside the local private-root boundary.

## Verification

Real-filesystem tests run with the normal locked C# suite on the admitted Linux
profile; they fail when required primitives are unavailable. Test roots are
uniquely owned directories outside the repository/application, and cleanup
removes only those roots. `Braid.StorageProbe` is test tooling, copied alongside
the test assembly, for independent-process writes, reads, races, and process
loss. It is not packaged into the web server or an execution worker.

Check private modes and ownership with a separate unprivileged reader, and run
PostgreSQL and isolated Ferrule integration cases on the target filesystem.
