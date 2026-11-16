# Specification object storage foundation

Project/bundle-owned storage uses the [durable registry](spec-bundle-registry.md)
and [intake API](spec-intake-api.md), independently of mapping/revision scope.

## Implemented boundaries

- `SpecObjectId` pins the source identity/version, object kind (original or extraction), and object ID.
- `SpecObjectReference` pins exact SHA-256 and byte count, with a 10 MiB ceiling. Empty originals/extractions are allowed.
- `SpecPublicationClaim` identifies the exact store and durable publication attempt. `SpecRetirementClaim` identifies an issued exact retirement.
- Separate publication/read/retirement interfaces are implemented by the shared registry-required workflow.
- Source capture verifies length and digest before physical publication. It reads at most the expected count plus one detection byte, never seeks, and leaves the caller's stream open. Two operations per adapter instance may hold bounded buffers concurrently.
- Current content is immutable: writes are create-only, and replay verifies existing content rather than trusting object existence. Retirement is a separate lifecycle operation.
- Diagnostics are closed codes; storage/provider exception messages are not returned. Cancellation with the caller's token remains cancellation.

These identities and interfaces are not user authorization. Authoritative services must obtain fresh project and document-content permission before reading or writing. The registry owns lifecycle admission, not authentication.

## Blob adapter

`BlobSpecObjectStore` reuses the existing configured Blob client factory and credential/endpoint options. Opening requires an explicitly supplied registry and an existing private container. Explicit initialization creates only `spec-v1-store`, using a create-only request. The marker pins a caller-provisioned store ID; opening captures its ETag and every operation rechecks privacy, marker bytes and marker incarnation.

Object names use a disjoint `spec-v1-` namespace containing only typed IDs, decimal source version and kind. No filenames, document URLs or caller-provided path segments enter names. Revision object names/records are untouched.

Publications use `If-None-Match: *`; reads verify metadata, actual bytes, digest and length. Retirement verifies the existing bytes and uses `If-Match` against the downloaded ETag, replacing the current payload with a zero-payload `retired` fence at the **same name**. Retirement of an absent object installs that fence create-only. Repeated retirement recognizes an exact fence.

The physical fence must remain indefinitely. Deleting the name would let an already dispatched, delayed create-only request resurrect data after database tombstoning. Even a stale registry admission cannot overwrite the fence. This also distinguishes a retired empty object from a legitimate live empty object. Provider/ETag conflicts are safe failures, not successful deletion claims.

`Retired` means the current payload was replaced by the fence. Azure versions,
snapshots, soft-deleted copies, replicas and backups follow provider retention
policies. Cleanup of those copies must preserve the current fence.

## Filesystem capability

`FileSystemSpecObjectStore` is intentionally **unavailable on every platform** and does no filesystem I/O, even when a registry is supplied. Canonical configuration validation reuses `FileSystemStorageOptions`. It never falls back to ordinary path-based I/O, follows a junction/reparse point/symlink, or fabricates a revision identity.

The descriptor-relative Linux primitives have revision-specific owner records,
temporary identities, seals and retirement records. Bundle storage needs its own
durable protocol, crash/restart and cross-process checks and permanent
no-resurrection fence. Windows has no handle-relative implementation.

See [filesystem requirements](spec-filesystem-protocol-prerequisites.md) for native
primitives, physical ownership/recovery requirements and supported-platform checks.

## Required registry/lifecycle integration

`ISpecObjectRegistry` must:

1. Bind each unique store ID to the verified physical deployment/container. Bind claims durably to exact organization/project/bundle/source version, object kind/ID, bytes/digest, and workflow owner before source consumption. Never reuse object identities, including failed or retired attempts.
2. Retain extraction provenance separately: extractor/profile version and exact source digest. Storage treats both kinds as opaque bytes and does not validate a document or extraction manifest.
3. Recheck bundle/source tombstones at final publication, then hold a cross-process lifecycle fence across the callback and durable acknowledgement. Admission is not a boolean check followed by an unfenced write. Serialize publication/read/retirement with bundle tombstoning.
4. Invoke each physical callback at most once per execution and await completion. Do not retain callbacks, acknowledge before the physical result, or retry them under a different owner. Reconcile lost acknowledgement using the same immutable claim and exact verified physical state.
5. Issue retirement claims durably only after logical tombstoning; preserve physical no-recreation fences. An expired worker/DB lock does not prove that an already dispatched provider request has stopped.
6. Make read admission authoritative for retained, non-tombstoned references. A reference alone must not grant access, and a read may linearize before a concurrent tombstone only while protected by the lifecycle protocol.
7. Track incomplete/uncertain attempts, retirement progress and retries, with bounded maintenance selection. Do not sweep a container or delete names based on a filename prefix. Do not reinterpret the existing orphan policy as successful-evidence expiration.
8. Reconcile originals, extractions and their consumers under an explicit retention policy; removing an original alone does not remove excerpts in conversations or generated evidence.

The host uses the durable PostgreSQL registry. A missing registry returns
capability-unavailable.
