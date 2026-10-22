# Azure Blob private storage

`Weft.Storage.Blob` keeps private objects in one Azure Blob Storage container with the
same contract as the [claim-aware filesystem store](private-storage-v2.md):
`BlobPrivateObjectStore` implements `IPrivateObjectPublicationStore`,
`IPrivateObjectRetirementStore` and `IPrivateObjectStorageObserver`. Native mapping
artifacts, derived reports, and the preview, regression and build inputs they feed all
use it unchanged. Tracking work is [#232](https://github.com/DeandreT/weft/issues/232).

## Configuration and credentials

`BlobStorageOptions` names the account's blob service endpoint, one container and a
credential source. No account key, SAS or connection string is accepted.

| Credential | Endpoint | Use |
| --- | --- | --- |
| `ManagedIdentity` | `https://<account>.blob.core.windows.net`, no path, query or port | Hosted use. An optional client ID selects a user-assigned identity. |
| `WorkloadIdentity` | Same | A federated workload identity from the standard `AZURE_*` environment. |
| `Emulator` | `http://<loopback>:<port>/devstoreaccount1` | Local tests against Azurite only, with its published development account. |

The container name must be canonical (3–63 lowercase letters, digits and single
hyphens). Object limits (4 MiB native, 16 MiB derived) and per-instance concurrency
(one to four, default two) match the filesystem store; configuration can lower but not
raise the limits.

The identity needs the `Storage Blob Data Contributor` data-plane role on the container
(read, create, conditional commit, lease and delete). Account settings that the data plane
cannot observe, such as disabled shared-key access, public network access and anonymous
access at the account level, belong to the deployment.

## Host selection

The Server, `Weft.Worker`, `Weft.MaintenanceTool` and `Weft.StorageTool` select private
storage with the same settings, parsed by `Weft.Storage.Hosting`:

| Setting | Value |
| --- | --- |
| `WEFT_PRIVATE_STORAGE_KIND` | Absent or `filesystem` (the default) selects the v2 filesystem store; `azure-blob` selects this store. Anything else is refused. |
| `WEFT_PRIVATE_STORAGE_ROOT` | Filesystem only: the canonical root. Refused under `azure-blob`. |
| `WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT` | The account's blob service endpoint. |
| `WEFT_PRIVATE_STORAGE_BLOB_CONTAINER` | The private container. |
| `WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL` | `managed-identity`, `workload-identity` or `emulator`. |
| `WEFT_PRIVATE_STORAGE_BLOB_CLIENT_ID` | Optional: the user-assigned managed identity's client ID, canonical lowercase. |

The Blob settings are refused under the filesystem kind, so a mixed or partial
configuration never selects storage by accident. A missing or malformed setting fails
host startup wherever the host reads storage at startup (native upload, builds, the worker
and the tools); retained report reads alone keep their existing deferred, fail-closed
storage resolution. Hosts open the store lazily at the first object operation, never
initialize it, and refuse ownerless publication. The tools refuse filesystem operands
under the Blob kind. Provision a new Blob store once with
`Weft.StorageTool initialize-blob` (see [offline activation](private-storage-activation.md#explicit-provisioning)).

## Admission

`InitializeAsync` and `OpenAsync` fail closed with `UnsafeStorage` unless:

- the container already exists (neither factory creates it);
- the container grants no anonymous access and has no immutability policy, legal hold or
  version-level immutability (retirement must be able to replace an object);
- `OpenAsync`: the marker `.weft-private-storage` exists as exactly
  `weft-private-storage-blob-v1\n<root ID>\n<control ID>\n` (95 bytes, two distinct
  nonzero lowercase `N`-format UUIDs) with `weftkind=marker` metadata;
- `InitializeAsync`: the container is empty, and the marker is written create-only.

Both factories then probe the actual primitives with the configured credential: create a
fresh probe blob with `If-None-Match:*`, confirm a second create-only write is refused,
lease it, confirm writes with a missing or wrong lease and an unconditioned delete are
refused, then commit and delete with the correct lease. A missing permission is `UnsafeStorage`; an unsupported conditional or lease
behavior is `Unsupported`. Opening never resolves the publication registry, and the
store never adopts an existing container or creates the marker during ordinary use.

## Layout

Every name is generated from typed nonzero UUIDs; no caller text reaches a blob name.

```text
.weft-private-storage                                                    marker
objects/{native|derived}/{org}/{project}/{mapping}/{revision}/{object}.blob     leased final slot
objects/{native|derived}/{org}/{project}/{mapping}/{revision}/.attempt-{temp}.tmp  attempt marker
control/{control}/probes/{probe}                                         transient probe
control/.../{object}/publications/{temp}/temp.gate
control/.../{object}/publications/{temp}/{absence|reservation|bound|prepared}
```

Each blob carries a closed `weftkind` metadata form; anything else is an unknown form.

| Form | Content | Other metadata |
| --- | --- | --- |
| `marker` | Marker bytes | — |
| `gate` | Empty control gate or unpublished final-name reservation | — |
| `seal` | One canonical record | — |
| `attempt` | Empty | `weftincarnation`: 16 lowercase hex digits, nonzero |
| `object` | Exact payload | `weftincarnation`, `weftsha256`, `weftlength` |
| `retired` | Empty | — |
| `probe` | Empty | — |

## Fencing mapping

| Filesystem v2 | Blob |
| --- | --- |
| Permanent empty `object.gate` / `temp.gate` files | An empty reservation at the final name and a separate temporary control gate, initially created with `If-None-Match:*` |
| Exclusive `flock` (non-blocking; contention is Busy or Io) | 30-second leases with fresh IDs on the final slot, temporary control gate and actual attempt marker, renewed every 10 seconds; acquisition contention is Busy or Io |
| Shared reader `flock` | None. A read is one download of the final name; the service never mixes two versions, and a retirement either precedes the read (Missing) or follows it |
| Process death releases the lock | An unrenewed lease expires within 30 seconds |
| Held descriptor identity checks | The storage service checks the current lease ID on the actual write target; renewal and local freshness checks also refuse known-lost ownership |
| Seal `stage` file, fsync and no-replace rename | One create-only Put Blob; the service makes it atomic and durable |
| Empty mode-0600 temporary file bound before payload | Empty leased `attempt` blob with a random incarnation, created with `If-None-Match:*` and bound before any source read |
| Payload streamed into the temporary file | Payload staged under the final slot's lease as uncommitted blocks, with block IDs derived from the TempID; uncommitted blocks are never readable or listed |
| `renameat2(RENAME_NOREPLACE)` | One Put Block List of exactly those blocks, conditional on the vacant reservation's entity tag and current lease ID; a published or retired slot is never promoted |
| Permanent `object.retired` / `temp.retired` fence files | A permanent empty `retired` blob at the actual data name, conditional on its entity tag and lease ID; an absent attempt is fenced with `If-None-Match:*` |
| Unlinking the exact admitted inode | Replacing the exact observed entity tag with the fence |

Only an empty `gate` reservation can be promoted to an object; published objects and
retirement fences cannot. Every payload write carries the lease of that same final slot,
so storage rejects a delayed commit after its lease expires or another holder retires
the original attempt. Temporary retirement also leases the actual attempt marker, so
an expired cleanup holder cannot later fence a marker it no longer owns. Uncommitted
blocks left by an interrupted publication are discarded by any
later commit on that name, including a fence, and the service removes the remainder after
seven days.

## Publication and retirement ordering

Publication takes the object gate briefly (an in-progress retirement refuses publication
with Io before any registration or source read), creates and leases a fresh temporary
gate, refuses a fenced final name, and proves the attempt name absent. It installs the
absence seal, reserves through the real registry, installs the reservation seal, creates
and leases the attempt marker, installs the bound seal and obtains Bind acknowledgement **before
reading source bytes**. A lost Reserve or Bind acknowledgement is recovered by the same
natural key and original owner, exactly as in the filesystem store.

Publication takes the final slot's lease before staging. Staging enforces the exact reference digest, byte count and class limit (64 KiB reads into
1 MiB blocks with a transfer checksum). The prepared seal retains the verified facts.
Under the final slot's lease, publication renews the temporary leases, rechecks the attempt marker's
entity tag and incarnation, both seals and the final name, then commits. A successful
commit promotes only the originally vacant slot and returns `Created`; removing the attempt marker afterwards is best effort and cannot
change the result. A refused commit verifies the existing object: exact bytes return
`AlreadyPresent`, a fence returns `Missing`, and anything else is `Conflict`.

Retirement of a final object takes that final slot's lease (Busy when held), then replaces a
published object with the fence (`Deleted`) or fences the absent name (`Missing`); an
existing fence is `Missing`. Retirement of a tracked temporary takes the final slot's
lease, its existing temporary control gate and, when present, the actual attempt marker's
lease. It requires the absence seal and checks every present seal against the captured original
publication (owner digest, absence digest, publication ID, times, incarnation and entity
tag) and the actual attempt marker, then fences the attempt name. The filesystem rule for
a reservation acknowledged before death without a local reservation seal carries over: only
an actually absent attempt can be fenced `Missing`. Outcomes and their mapping to cleanup
dispositions are unchanged.

## Seal records

Seals start with `WEFTB1R\n`, a big-endian 16-bit kind, 16-bit version 1 and 32-bit total
length. The common prefix holds the root and control IDs, object class (native 1,
derived 2), organization, project, mapping, revision, object and TempIDs, the reference
digest and signed 64-bit byte count, the owner kind and a SHA-256 owner digest. UUIDs use
network order and integers are big-endian. The owner digest covers every fact of the
original claim: actor, server instance, lease token, generation, lease expiry, and either
the ingestion project and operation or the report cache entry, derivation, start time,
optional repair target, candidate object and complete canonical report key.

| Kind | Suffix |
| --- | --- |
| Absence, 1 | Empty |
| Reservation, 2 | Absence digest, PublicationId, RegisteredAtUtc ticks |
| Bound, 3 | Reservation digest, PublicationId, attempt incarnation, entity tag length and ASCII bytes |
| Prepared, 4 | Bound digest, PublicationId, BoundAtUtc ticks, incarnation, verified digest, verified count, block count |

Absence records are 218 bytes and prepared records 326 bytes; records are at most 1,024
bytes. Decoding rejects extra, missing or noncanonical bytes and re-encodes for equality.

## Publication identity mapping

The registry and maintenance observations keep the existing v2 identity record. A Blob
store fills it as follows; these values identify Blob publications exactly and never
coincide with filesystem facts, whose root IDs differ.

| Record field | Blob value |
| --- | --- |
| Root and control namespace | The marker's IDs |
| Boot ID | The fixed Blob profile ID `57656674-426c-6f62-0001-000000000001` |
| Mount namespace device and inode | 0 and 1 |
| Effective user | 0 |
| File identity | The attempt incarnation as inode, device 0:0, mount ID from the control ID |
| Directory identity | A SHA-256-derived inode of the root ID and exact revision prefix, same mount ID |

A committed object carries its creating attempt's incarnation, so an observed final
object matches the registration that published it, as a renamed inode does on a
filesystem.

## Observation

`ObserveAsync` lists one project's `objects/native/...` then `objects/derived/...` names
hierarchically, through mapping, revision and payload levels, within the shared 100-entry
budget. Canonical mapping and revision prefixes are structural entries with synthesized
directory facts. Payload entries are candidate slots only for an exact final or attempt
name of the matching form. Unknown names, forms or virtual subdirectories are unproved
and are not traversed. Empty final reservations and retirement fences are not payload entries and are skipped, at most
10,000 per invocation before the namespace is reported incomplete. Observation reads no
payload and changes nothing.

## Verification

`PrivateObjectStoreContractTests` runs the shared store contract against both the
filesystem store and this store. `BlobPrivateObjectStoreTests` adds admission, options,
codec, staged-block visibility, lease expiry, stale-publisher fencing, abandoned
publication recovery and tampering checks. Both use an owned Azurite container started
from the pinned image in `WEFT_AZURITE_IMAGE` (see `AzuriteFixture.QualifiedImage`); each
test uses its own uniquely named blob container, and only those containers and the
emulator are removed. Without the variable the Blob cases are skipped and the filesystem
cases still run.
