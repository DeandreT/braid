# Durable specification bundle registry

**Intake follow-up:** [Specification intake API](spec-intake-api.md) now wires the real service/registry/Blob path, adds bounded restricted inventory reads, and supersedes the prior 0044 review/checksum. The foundation notes below describe the original registry unit. Runtime activation still requires the real ordered schema and backend admission.

## Implemented code, activation status

This unit adds `0044_spec_bundles.sql`, `PostgresSpecBundleDatabase`, actor-bound `PostgresSpecBundleSession`, and `SpecBundleService`. The existing Blob adapter's real `ISpecObjectRegistry` callbacks can now execute against PostgreSQL. No HTTP/UI/host registration is added. Filesystem publication remains unavailable.

The original registry checkout contained migrations 0001–0041 and embedded 0044 under **PendingMigrations**. Integration now includes the real 0042 XML and 0043 proposal files, activating 0044 in the normal consecutive migration resource namespace. All historical migration bytes are preserved. `MigrationRunner`'s no-gaps/checksum rules remain unchanged. 0044 additionally requires journal entries for 42 and 43; there are no placeholder migrations, renumbering, or direct-install test shortcuts. See [integration validation](v1-integration.md).

Portable tests compile/run on the pinned Windows .NET 10.0.112 toolchain. PostgreSQL integration tests require explicit connection settings and an actual prepared database; a skip is not database qualification. No PostgreSQL execution, Linux filesystem qualification or cloud qualification is asserted by these portable checks.

## Data and authority

The migration adds owner-managed physical-store bindings, bundles, immutable source versions, original/extraction object reservations, temporary ownership records, and an append-only temporary seal history. No document bytes, paths, credentials or original filenames enter these tables. Only bounded physical evidence and provenance/digests are retained.

- Provision three separate restricted CONNECT-only logins: `weft_spec_reader`, `weft_spec_writer`, `weft_spec_maintenance`. The migration does not create roles or credentials. Runtime roles receive schema usage and only their enumerated SECURITY DEFINER functions; no direct table grants. Owner-only helpers are not executable by runtime roles.
- Transaction admission verifies current/session login identity, restricted role attributes/memberships, absence of table/column/grant-option privileges, owned persistent tables/RLS, exact column layouts and unique-key shapes, expected function bodies/security/search path/volatility, and the exact 0044 checksum.
- `spec_authorize` intersects active principal, organization/project membership and the existing **explicit project payload-read grant**. Publisher/administrator role is additionally required for writes. It SHARE-locks those authority rows through the operation; revocation cannot slip between admission and callback completion. Metadata viewing alone is insufficient.
- No method authorizes AI/provider sharing. A future generation API needs its explicit sharing policy; neither metadata nor this content-read grant establishes it.
- Reader credentials cannot use the gate's publication/retirement modes. Maintenance credentials can consume only previously issued retirement claims, not create bundles, publish, or read document contents. Cleanup survives revocation of the original uploader.
- Owner provisioning inserts a unique `spec_stores` ID, physical kind, deployment-defined canonical location SHA-256, and explicit activation state. The trusted session's `SpecStoreBinding` must match it. Physical adapters additionally enforce their own marker/incarnation checks. Provisioning must derive the digest from the actual server-owned backend configuration, not a client request; arbitrary object/claim UUIDs do not grant authority.

## Lifecycle and transactions

1. `CreateAsync` uses a caller-retained creation key and exact bundle ID. A duplicate key with different intent conflicts.
2. `UploadAsync` checks content/write authority before consuming a bounded stream. It captures exact bytes, then durably reserves the source version/object/publication attempt before calling storage. The registry checks authority again before consumption and at final publication.
3. `ReserveAsync` serializes on the bundle row and performs version CAS. Retries compare the entire intent, including original expected version, owner, source version/format, object kind/ID, digest/length and extraction provenance. Different intent never overwrites a reservation. New source versions are explicit identities, not replacements.
4. Registry execution holds the **actual PostgreSQL transaction** and lifecycle row locks across the physical callback and durable acknowledgement. Physical callbacks are called at most once per execution. A failed/uncertain callback rolls back acknowledgement; recover using the same reserved attempt. There is no lease expiry that silently transfers object ownership.
5. Reads resolve retained references through the same current authority and lifecycle gates, verify store binding, then verify actual bytes. Unpublished objects cannot be read even if a lost acknowledgement left physical bytes present.
6. Bundle/source tombstoning is an explicit CAS operation with a stable idempotency key. It commits logical denial and durable final/temporary retirement IDs before any physical cleanup. Source tombstoning is limited to the selected immutable source version; whole-bundle tombstoning includes every version.
7. Cleanup discovers at most 100 exact pending claims. Blob retirement preserves its permanent physical no-recreation fence. Stale workers are denied by the database; the Blob fence also blocks already-dispatched stale create-only writes after transaction loss.

Lock ordering uses authority rows, configured store binding, bundle row and object row. Store and authority locks are SHARE locks; bundle/object lifecycle locks are exclusive. Database lock/statement timeouts are bounded. A callback exceeding the idle-in-transaction deadline has an uncertain result and must be reconciled, never assumed absent. Keeping a physical no-recreation fence is essential even with database locks.

Limits are 20 retained source-version identities, 50 MiB original bytes and 128 object reservations per bundle, 10 MiB per object, and 32 temporary reservations per object. Historical rows count toward these limits; tombstoning does not silently reset quotas or permit identity reuse. Source and extraction objects remain opaque immutable bytes to storage. Extraction publication additionally binds source digest, extractor version, profile digest and extraction outcome to the registered original.

## Temporary reservations, inode binding, seals and cleanup

`ISpecTemporaryRegistry` is implemented by the same PostgreSQL session. These APIs prepare the missing **durable metadata** boundary, not a filesystem implementation:

- `AdvanceTemporaryAsync(... Reserved ...)` records a stable temporary ID, publication attempt and absence evidence before file creation. Same exact intent replays. The native caller must install/verify its physical absence seal in the proper order.
- `Bound` requires the exact previous seal digest, unchanged root/control/parent/gate/boot/mount-namespace context, and an observed file identity. The native caller must commit this binding before writing private payload.
- `Prepared` requires unchanged bound evidence and exact predecessor. The claim's digest/length is part of the seal identity; the future native adapter must actually verify bytes, fsync, close the writable descriptor and install its physical prepared seal before requesting final publication.
- Each transition writes an immutable history row and updates the current snapshot atomically. The seal SHA-256 covers the complete logical source/object/owner attempt, phase, predecessor and canonical physical evidence. Recovery decodes bounded evidence and recomputes its digest before returning a typed snapshot.
- `SpecTemporaryEvidence` is a fixed 196-byte, big-endian v1 codec with exact-length/version/reserved-byte checks. It carries boot/control IDs, mount namespace, effective UID, and root/control/parent/gate/file identities. Reserved evidence has no file; Bound/Prepared require one. Physical identities are not paths or deletion authority.
- Tombstoning issues exact temporary retirement IDs. `RetiringTemporariesAsync` supplies retained evidence to the future native cleanup adapter; `ExecuteTemporaryRetirementAsync` rechecks the current seal/claim under the database fence, invokes physical cleanup, then acknowledges. Arbitrary evidence supplied by a caller cannot replace stored identity facts.
- Final retirement acknowledgement refuses while any temporary remains outstanding. Pending discovery also retains an object if its temporary cleanup is incomplete, preventing lost maintenance work after a crash. A native worker must reconcile the physical final fence and temporary retirements before final acknowledgement; the current Blob service has no native temporaries.

Physical absence/binding/prepared seal installation, descriptor-relative filesystem gates, permanent on-disk tombstones and Linux crash qualification still need implementation. The prior prerequisites document is retained as the historical analysis; its missing registry metadata contracts are addressed here, while its native-protocol and runtime-qualification requirements remain.

## Exact next integration hooks

Provision/qualify the real ordered schema and physical binding first. Then:

```csharp
var database = new PostgresSpecBundleDatabase(writerConnectionString, SpecDatabaseProfile.Writer);
var session = database.ForActor(trustedActor, configuredStoreBinding);
var service = await session.CreateServiceAsync(async (registry, ct) =>
    await BlobSpecObjectStore.OpenAsync(blobOptions, configuredStoreBinding.StoreId, registry, ct));
```

The factory passes the actual durable session to storage. Services should be scoped to the validated actor; do not build them from user-supplied principal IDs or retain them across identity changes. Use a reader profile/session for content reads. A maintenance profile uses `ForMaintenance(binding)` and can only process durable cleanup claims. Keep pools and service/adapter lifetimes owned by host composition.

API integration should expose create/upload/status/read/tombstone routes with existing session/antiforgery and error mapping. Keep stable creation/publication/tombstone keys and expected versions across retries. An uncertain upload response requires recovery with the same IDs and bytes. No route should accept a physical filename, path, retirement claim or registry callback from a client.

## PostgreSQL test lane

`PostgresSpecRegistryTests` requires four private environment connections (`WEFT_SPEC_TEST_OWNER`, `_READER`, `_WRITER`, `_MAINTENANCE`) to an explicitly disposable database with the actual 0042/0043/0044 sequence and provisioned roles. It never creates missing migrations/roles or stores connection values in the repository. Fixtures create isolated organizations/projects/principals and retain append-only evidence for the disposable database's owner to discard as a whole.

The lane exercises exact replay, CAS contention, payload-grant denial, database-observed blocking while publication holds a fence, stale-worker rejection, uncertain callback recovery, native temporary binding/claim recovery, and denial of direct-table access/reader mutation. It does not substitute an in-memory registry for database tests. Portable service tests separately cover capture/admission ordering, recovery identity, digest rejection, tombstone-before-cleanup, canonical evidence and migration packaging.
