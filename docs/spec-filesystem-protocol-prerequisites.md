# Filesystem spec protocol: enablement prerequisites

**Follow-up:** [The durable registry unit](spec-bundle-registry.md) now implements bundle reservations, physical evidence binding, seal history and issued cleanup claims. The analysis below is preserved from the prior, no-registry-change unit. Filesystem I/O remains unavailable until its native protocol and supported-platform qualification are complete.

## Decision for this unit

Keep `FileSystemSpecObjectStore` unavailable on **every** runtime. No publication, read, retirement, initialization, repair or cleanup is enabled. This is a deliberate capability refusal, not a filesystem implementation or successful Linux qualification.

The inspection host has the Windows .NET 10.0.112 toolchain. `wsl --list --quiet` returned no distributions, the current user's WSL registration key contained no installed distributions, and no Docker executable was found. No Linux runtime was installed or contacted. There is no supported-platform execution evidence from this unit.

## Available secure primitives

The existing revision adapter is substantially stronger than path validation:

- `LinuxFileSystem.cs`: Linux x86-64 ABI admission; `openat2` descriptor-relative lookup with `NO_SYMLINKS`, `BENEATH`, and no mount crossing below the root; `statx` inode/device/mount identity; exact owner/mode/link-count checks; `renameat2(RENAME_NOREPLACE)`; read-only final files.
- `LinuxFileSystem.V2.cs`: fresh-descriptor nonblocking `flock` gates with named/held identity revalidation; file/directory `fsync`; exact-identity unlink; boot and mount-namespace identity.
- `FileSystemV2PrivateObjectStore.Publication.cs`: durable absence/reservation/bound/prepared record chain; registry reservation and inode binding **before** writing private payload; writable descriptor closure before final publication; final gate and tombstone recheck.
- `FileSystemV2PrivateObjectStore.Seals.cs`: bounded record encoding, partial-stage handling, immutable seals, durable directory installation and exact record identity checking. An incomplete stage is not silently adopted or removed.
- `FileSystemV2PrivateObjectStore.Retirement.cs`: permanent retirement fence before payload unlink; exact temporary origin/identity checks; cross-process gates; refusal of ambiguous evidence and unsupported boot/namespace recovery.

These low-level primitives are reusable. Their high-level records and registry operations carry `RevisionScope` and revision ingestion/report ownership, so passing bundle data to them is not valid reuse.

## Concrete missing bundle protocol contracts

The current `ISpecObjectRegistry` provides logical admission and fenced callbacks only. It does not provide the revision protocol's durable temporary reservation/binding/recovery contracts. For a named-temporary implementation, the missing pieces are:

1. **Bundle root/control activation and incarnation pin.** Bind store ID to root, control namespace, effective UID, device/mount/inode identities, and admitted boot/mount namespace. Establish permanent gate identities and revalidate held/named paths before every destructive or final operation. Do not merely reopen a matching path/marker string.
2. **Recoverable pre-creation reservation.** Persist an exact bundle publication attempt plus temporary identity and absence-seal digest before creating its named payload file. Recover a lost reservation acknowledgement under the same key.
3. **Exact inode binding before payload write.** Durably bind the actually created inode, parent/control/gate identities, and bound-seal digest. A filename, claimed digest or exclusive-create result is not sufficient authority to delete a file after a process restart.
4. **Bundle-owned bounded seal codec and durable installation.** Versioned absence/reservation/bound/prepared records must include the complete bundle/source/version/object/kind/length/digest context and chain exact prior record identities. Truncated or replaced stages must fail closed. Do not adapt these records by inventing revision IDs.
5. **Temporary retirement/recovery claim.** The present `SpecRetirementClaim` identifies only a final object. Cleanup needs the exact registered temporary, its bound identity and original owner, with crash-window classification. An unbound file containing private bytes cannot be swept by its prefix or adopted by a retry.
6. **Permanent physical final fence and gate protocol.** Publication and retirement must use the same cross-process object gate and recheck its identity. Persist the retirement fence before unlinking payload, preserve it permanently, and recheck it immediately before no-replace publication. Database callback admission alone cannot replace this physical fence.
7. **Observed-result recovery.** Distinguish failure before rename/unlink from an uncertain acknowledgement after physical success, and reconcile under the same immutable claim. Never convert an uncertain result to a new owner or a replacement write.

This unit was expressly scoped to exclude registry changes. It therefore does not add speculative reservation/binding ports or enable an incompletely owned named-temporary path.

### Possible alternative requiring different native primitives

An unnamed-temporary design could avoid recoverable named payload stages, but the current helpers do not expose qualified `O_TMPFILE` creation plus descriptor-to-name atomic no-replace publication. That route requires a supported, capability-checked linking mechanism, filesystem-specific qualification, read-only/writable-descriptor closure semantics, durable directory publication, and the same permanent gate/tombstone protections. It must not use an unchecked `/proc/self/fd` magic-link fallback or assume `linkat(AT_EMPTY_PATH)` privileges. This unit does not claim that unnamed publication is available.

## Required supported-platform verification before changing capability

Use actual Linux x86-64 processes and the admitted local filesystem profile, not Windows mocks. Existing primitive admission permits the listed filesystem magic values in `LinuxFileSystem.RequireLocalFileSystem` (ext-family, Btrfs, XFS, tmpfs, overlayfs); that allowlist is not new spec-adapter qualification. Unsupported syscalls, ABIs, mount changes, filesystems and permissions must remain explicit refusals. Windows/ARM64 remain unavailable unless independently implemented and qualified.

Required executions include:

- Two separately started writers, identical and conflicting content, same/different claims; no overwrite and exact replay verification.
- Publisher stalled after preparation, independent retirement installing the physical fence, publisher resuming; no resurrection. Include final absence and empty original/extraction objects.
- Process termination after each reservation/binding/seal/write/close/rename/acknowledgement phase; reopen and reconcile using the same claim. Prove no partial payload becomes readable and no unowned candidate is deleted.
- Termination after retirement fence persistence and before/after unlink; retry and delayed publishers must preserve the fence.
- Symlinked root/parent/namespace/gate/payload, hard links, FIFO/device targets, mount crossing, renamed directory/gate replacement, invalid owner/modes, repository/content-root placement. Validate actual held descriptors, not preflight path checks.
- Stream cancellation, I/O failure, exact length/digest mismatch, byte limits, reference scope isolation, and fresh/final registry admission before consumption/publication.
- Crash persistence verification where durability is claimed. Process death is not a power-loss/fsync-ordering test.

## Tests delivered here

`FileSystemSpecCapabilityTests.cs` checks non-consumption, no registry callbacks, exact scope refusal, parallel adapter non-adoption, canonical path rejection, and byte-for-byte preservation of opaque recovery candidates over retries/retirement. It includes an explicitly Linux-only real symlink-preservation case; a skip on Windows is recorded, not counted as Linux success.

The recovery-candidate tests do not fabricate valid seals or simulate a process crash. The parallel tests are in-process refusal tests, not cross-process lock qualification. They make the current capability boundary executable while the prerequisites remain unresolved.

No SQL/API/UI/registry contracts or production filesystem adapter behavior change in this unit. The future 0044 database slot remains as documented in `spec-object-storage-foundation.md`; no migration is created.
