# Filesystem specification storage requirements

`FileSystemSpecObjectStore` is unavailable on every runtime and performs no
publication, read, retirement, initialization, repair or cleanup I/O. The
[durable registry](spec-bundle-registry.md) supplies reservations, physical evidence
binding, seal history and cleanup claims. A physical bundle-owned protocol is
still required.

## Reusable native primitives

- `LinuxFileSystem.cs`: Linux x86-64 ABI admission; descriptor-relative `openat2`
  with `NO_SYMLINKS`, `BENEATH` and no mount crossing; `statx` identity; owner,
  mode and link checks; `renameat2(RENAME_NOREPLACE)`; read-only final files.
- `LinuxFileSystem.V2.cs`: fresh-descriptor nonblocking `flock` gates, held/named
  identity revalidation, file/directory `fsync`, exact-identity unlink, boot and
  mount-namespace identity.
- `FileSystemV2PrivateObjectStore.Publication.cs`: durable absence/reservation/
  bound/prepared chain, registry binding before private writes, writable-descriptor
  closure and final gate/tombstone checks.
- `FileSystemV2PrivateObjectStore.Seals.cs`: bounded encoding, immutable seals,
  durable directory installation and exact record identity checks. Incomplete
  stages are not adopted or removed.
- `FileSystemV2PrivateObjectStore.Retirement.cs`: permanent retirement fence before
  unlink, exact temporary identity, cross-process gates and refusal of ambiguous
  evidence or unsupported boot/namespace recovery.

The high-level revision records carry `RevisionScope`; bundle storage needs its
own records rather than fabricated revision IDs.

## Physical protocol

1. Bind store ID to root/control namespace, effective UID, device/mount/inode
   identities and boot/mount namespace. Establish permanent gates and revalidate
   held/named identities before final or destructive operations.
2. Persist the exact publication attempt, temporary identity and absence-seal
   digest before creating a named payload file. Recover lost acknowledgement with
   the same key.
3. Bind the created inode, parent/control/gates and seal digest durably before
   writing private payload. Filenames and claimed digests cannot authorize cleanup.
4. Install bounded bundle-owned absence/reservation/bound/prepared seals with full
   bundle/source/version/object/kind/length/digest context and predecessor identity.
   Reject truncated or replaced stages.
5. Cleanup only exact registry-issued temporary retirement claims and bound
   identities. Never sweep an unbound file by prefix or adopt it on retry.
6. Publication and retirement share the same cross-process object gate. Persist
   the permanent retirement fence before unlink, preserve it, and recheck it
   immediately before no-replace publication. Database admission alone cannot
   block an already-dispatched stale write.
7. Distinguish failure before rename/unlink from uncertain acknowledgement after
   physical success. Reconcile under the same immutable claim, not a replacement
   owner or write.

## Supported-platform checks

Use actual Linux x86-64 processes and the local filesystem profile admitted by
`LinuxFileSystem.RequireLocalFileSystem` (ext-family, Btrfs, XFS, tmpfs, overlayfs).
Unsupported syscalls, ABIs, mount changes, filesystems and permissions must refuse
the capability. Windows and ARM64 are unavailable.

Required cases:

- Separately started writers with identical/conflicting bytes and same/different
  claims: no overwrite and exact replay verification.
- Publisher stalled after preparation, retirement persisting its fence, then
  resumed publisher: no resurrection, including absent and empty objects.
- Process death after each reservation/binding/seal/write/close/rename/ack phase:
  reopen with the same claim; no partial read and no unowned deletion.
- Death before/after unlink following retirement-fence persistence; delayed
  publishers and retries must preserve the fence.
- Symlinked root/parent/control/gate/payload, hard links, special files, mount
  crossings, renamed/replaced gates or directories, invalid owner/modes and
  repository/content-root placement. Check held descriptors, not only paths.
- Cancellation, I/O failures, length/digest/byte-limit refusals, scope isolation
  and fresh registry admission before consumption/publication.
- Crash persistence with actual fsync ordering; process death alone does not
  simulate power loss.

`FileSystemSpecCapabilityTests.cs` covers the current refusal contract:
non-consumption, no registry callbacks, scope/path refusal, adapter non-adoption,
symlink preservation and unchanged opaque recovery candidates over retries and
retirement.
