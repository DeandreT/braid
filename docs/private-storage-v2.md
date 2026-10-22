# Claim-aware private filesystem storage

Issue [#65](https://github.com/DeandreT/weft/issues/65) adds an explicitly selected
v2 component behind `IPrivateObjectPublicationStore` and
`IPrivateObjectRetirementStore`. It retains the original publication owner and
coordinates publishers, verified readers and physical retirement across
processes. The component was qualified and merged in
[#68](https://github.com/DeandreT/weft/pull/68).

The configured application host now selects v2, forwarding the original
ingestion or report claim through a lazy role-specific registry. Existing v1
library factories remain available for deliberately selected legacy adapters.
Existing application roots require [explicit offline activation](private-storage-activation.md);
requests never initialize or convert a root. This integration is being qualified
in [#66](https://github.com/DeandreT/weft/issues/66). The bounded cleanup command and
fresh database acknowledgements follow in
[#62](https://github.com/DeandreT/weft/issues/62). The
[maintenance metadata contract](private-object-maintenance-metadata.md) supplies
the registration and captured retirement facts; physical storage never grants
project access or replaces an expired workflow claim.

## Component boundary

`FileSystemV2PrivateObjectStore.Initialize` creates a new private root;
`Open` admits its exact marker, control namespace and supported local Linux
profile. The additive `FileSystemPrivateObjectStore.InitializeV2` and `OpenV2`
factories select the same component explicitly. Initialization is for a fresh
root outside the application and repository trees.

Owner-bearing publication receives the actual native ingestion claim or report
attempt claim. A registry factory is resolved lazily only for this operation.
Opening, verified reading and physical retirement do not resolve that factory.
The inherited ownerless publication method fails with `Unsupported` before
reading the source. Production factories have no test observer.

Retirement consumes an already issued `ObjectCleanupClaim`, including the
captured original publication when the target is a temporary file. It does not
call ordinary Reserve or Bind, substitute a current owner, or acknowledge the
cleanup claim. Metadata acknowledgement must happen after releasing filesystem
gates and must independently recheck the cleanup claim's current token and
generation.

## Permanent namespace and private records

The marker `.weft-private-storage` is exactly 90 ASCII bytes:

```text
weft-private-storage-v2
<32 lowercase hexadecimal root ID>
<32 lowercase hexadecimal control namespace ID>
```

Both IDs are nonzero, and the final line ends with LF. The marker is mode 0400.
The mode-0700 control namespace is
`.weft-private-control/<control ID>/`; its immutable 68-byte `control.meta`
binds the root, namespace, format/profile version and compiled limits.
All admitted directories and files belong to the service's effective user.

Final data paths retain the existing full object scope:

```text
objects/{native|derived}/{organization}/{project}/{mapping}/{revision}/{object}.blob
```

The control namespace repeats that full class/scope/object tuple. An object leaf
contains a permanent empty mode-0600 `object.gate`, optional permanent
`object.retired` fence, and `publications/<TempID>/`. Each publication has its own
permanent `temp.gate`, optional `temp.retired` fence, and private immutable seals.
Its data slot is `.attempt-<TempID>.tmp` in the final data directory. IDs, gates
and fences are never recycled, removed or replaced by this component.

An object leaf is installed complete through an owned sibling directory stage
and a no-replace rename. Publishers, readers and final retirement share this
initializer. An existing incomplete leaf is refused; missing gates are never
recreated. Tracked-temp retirement requires its existing exact publication leaf.

| Seal | Additional retained facts |
| --- | --- |
| `absence` | The exact data slot was absent under its permanent temp gate |
| `reservation` | Absence seal digest/inode and the database-issued publication ID/time |
| `bound` | Reservation seal digest/inode and the actual newly created data inode |
| `prepared` | Bound seal digest/inode, acknowledged Bind time, verified digest/count and closed data inode |

Every seal repeats the root/control identity, full object reference and TempID,
original actor/instance/token/generation/lease, full native or report owner,
boot/mount namespace identity, effective user and six actual location/gate
identities. Report owners retain their complete canonical report key and hash.
These files contain private lease evidence and must not appear in HTTP,
diagnostics, process arguments or logs.

Seal headers are `WEFTV2R\n`, a 16-bit kind, 16-bit version 1 and 32-bit total
record length. Integers use big-endian encoding; UUIDs use network order. UTC
times use .NET 100-nanosecond ticks. Records are at most 65,536 bytes, and a
canonical report key is at most 32,768 bytes. Decoding rejects extra bytes,
unknown variants, malformed identities and noncanonical key representations.
The bound seal excludes the later Bind acknowledgement, avoiding a digest cycle.

The `control.meta` fields occur in this exact order, with no padding:

| Field | Bytes |
| --- | --- |
| ASCII `WEFTV2C\n` | 8 |
| Format version, unsigned 16-bit, value 1 | 2 |
| RootId, then ControlNamespaceId | 16 each |
| Profile version, unsigned 16-bit, value 1 | 2 |
| Native limit 4,194,304, then derived limit 16,777,216, unsigned 64-bit | 8 each |
| Maximum seal 65,536, then maximum key 32,768, unsigned 32-bit | 4 each |

After its 16-byte header, every seal has this common prefix in order:

| Field | Encoding |
| --- | --- |
| RootId, ControlNamespaceId | UUID, 16 bytes each |
| Object class | One byte: native 1, derived 2 |
| OrganizationId, ProjectId, MappingId, RevisionId, ObjectId, TempId | UUID, 16 bytes each |
| Expected content digest | 32 raw SHA-256 bytes |
| Expected byte count | Signed 64-bit |
| Owner kind | One byte: native ingestion 1, report attempt 2 |
| Original actor, server instance, lease token | UUID, 16 bytes each |
| Original generation, then lease expiry | Signed 64-bit generation, then UTC ticks |
| Owner-specific fields | The variant below |
| BootId | UUID, 16 bytes |
| Raw mount namespace device, then namespace inode | Unsigned 64-bit each |
| Effective user ID | Unsigned 32-bit |
| Root, control namespace, object leaf, temp leaf, data parent, temp gate identities | Six 24-byte identities, in that order |

An identity is unsigned inode (8 bytes), device major (4), device minor (4),
then mount ID (8). Raw namespace device uses the actual Linux `dev_t` value,
distinct from an identity's separate major/minor fields. UUIDs retain textual
network order, and every integer uses big-endian encoding without padding.

The native owner variant contains only its original OperationId (16 bytes);
its project and actor are already represented by the common fields. The report
variant contains, in order: CacheEntryId (16), DerivationId (16), original
StartedAtUtc ticks (8), repair-present flag (one byte, exactly 0 or 1), optional
RepairTargetDerivationId (16), canonical key byte length (unsigned 32-bit), those
exact UTF-8 key bytes, and their raw SHA-256 digest (32). The full key must decode
through existing public domain constructors and re-encode byte-for-byte; a
fingerprint never replaces it.

The common prefix is followed by exactly the matching suffix:

| Kind | Ordered suffix |
| --- | --- |
| Absence, 1 | Empty |
| Reservation, 2 | Absence digest (32), absence identity (24), PublicationId (16), RegisteredAtUtc ticks (8) |
| Bound, 3 | Reservation digest (32), reservation identity (24), PublicationId (16), data identity (24) |
| Prepared, 4 | Bound digest (32), bound identity (24), PublicationId (16), acknowledged BoundAtUtc ticks (8), data identity (24), verified digest (32), verified signed byte count (8) |

Identifiers must be nonzero, owner generations and timestamps must satisfy
their original typed claim constraints, and locations must retain distinct
inodes on the same recorded mount. The native golden records are exactly 446,
526, 542 and 590 bytes for the four kinds. Report record length varies with the
full key and optional repair UUID, within the fixed limits.

Each seal has a fixed `<name>.stage` slot. Installation uses CreateNew, bounded
write, file fsync while its ordinary descriptor is open, mode 0400, close,
no-replace rename and directory fsync. Directory fsync uses a separate readable
descriptor checked against the held directory. Partial, corrupt or unknown
stages remain retained and are never rewritten or adopted.

## Publication and retirement ordering

Publication holds TempGate EX throughout. It checks ObjectGate briefly for an
existing fence, proves temp absence and installs the absence seal. It then
reserves through the real registry, installs the reservation seal, creates the
empty mode-0600 data inode, installs the bound seal and obtains Bind
acknowledgement **before reading source bytes or writing payload**. Recovery of
an uncertain registry acknowledgement uses the same natural key and original
owner after the original call settles.

Payload streaming enforces the exact reference digest/count and class limit
(4 MiB native, 16 MiB derived). The file is flushed, made mode 0400 and closed;
the prepared seal retains those facts. Under ObjectGate EX, publication then
rechecks fences, the bound/prepared seals and actual named/held identities before the
no-replace final rename. A successful rename returns `Created` with no fallible
post-rename work. This establishes process-death recovery, without claiming
power-loss durability for that final directory entry.

Every acquisition uses a fresh open description and checked native `flock`;
runtime file-sharing settings do not establish this exclusion. Verified reads
retain ObjectGate SH through their final integrity/link checks. Final retirement
uses ObjectGate EX, durably installs the permanent fence, then removes only its
exact admitted target. A safe fence plus actual absence can yield `Missing`.

Temporary retirement uses TempGate EX and the captured original publication,
including after its workflow lease or access expires. Destructive temp recovery
also requires the same live boot/mount namespace and actual recorded identities.
Ordinary final reads remain compatible with a normal reboot.

An acknowledged database reservation followed by death before the local
reservation seal is a narrow recoverable state: the captured unbound database
row must match the valid absence seal and full owner/location/incarnation facts,
and the exact temp slot must actually be absent under TempGate EX. Retirement
may fence that slot and return `Missing`; it does not manufacture a seal or Bind.
A present temp requires the complete valid absence/reservation chain. Safe-empty
unbound cleanup additionally proves a zero-length, mode-0600, singly linked
file. Nonempty unbound files, mismatched/corrupt required chains and unknown
legacy slots remain retained.

Physical results are `Deleted`, `Missing`, `Busy`, `Unsupported`, `UnsafeStorage`
and `Io`; cancellation remains cancellation. These are separate from database
cleanup dispositions. The later cleanup command must map them truthfully and
perform fresh acknowledgement outside the filesystem critical section.

## Qualification scope

The component needs independent binary vectors, strict codec/key refusal,
actual filesystem identities and permissions, original-owner registry facts,
and real cross-process gate/death recovery checks. The separate test-only
`Weft.StorageProtocolProbe` accepts bounded stdin commands and can stop after an
actual completed phase. Its readiness marker never substitutes for a completed
filesystem operation. A post-rename stop is signaled by the probe only after
publication returns `Created`.

Synthetic registries qualify physical behavior only. PostgreSQL integration
checks must use actual persisted claims and registrations. This document and
the unactivated component do not qualify service activation, cleanup command
execution or existing-root conversion. The [Azure Blob store](private-storage-blob.md)
implements the same contract with blob leases and conditional commits, and the
shared contract tests run against both stores.
