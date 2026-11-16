# Native revision ingestion

`IRevisionIngestionService` composes the qualified metadata, private storage, and
native validation adapters. It accepts trusted server actor/project identities
and a readable stream. [Revision uploads](catalog-revision-uploads.md) owns its
HTTP adapter; [catalog reads](catalog-reads.md) and [catalog browser](catalog-browser.md)
own discovery and navigation. This page owns service admission and recovery,
not host activation or provider/cloud acceptance. Original P3 requirements are
indexed by the [contract-owner map](catalog-persistence-plan.md#current-contract-owners).

## Submission and ownership

Create requests contain a mapping name/description, backend ID, and display
filename. Append requests contain an existing mapping and an optional head
condition. The server supplies the actor and project, looks up an append target's
immutable backend, and checks current upload permission before reading bytes.
Clients do not supply object references, digests, policies, worker identities,
claims, or validation evidence.

The service reads from the stream's current position without seeking or disposing
it. It preserves the exact bytes and computes their SHA-256 digest without parsing
or reserializing JSON. New admission uses the lowest application, storage, and
backend native limit, bounded by 4 MiB. The reader requests at most the remaining
budget plus one byte; it does not drain an oversized source.

Create names are trimmed, nonempty and bounded to 200 Unicode scalars/800 UTF-8
bytes; descriptions preserve at most 4,000 scalars/16,000 UTF-8 bytes. Invalid
Unicode and NUL are refused. Backend IDs are at most 128 UTF-8 bytes. Display
filenames are safe basenames of at most 255 UTF-8 bytes, defaulting to
`entry.native.json`; they never select object paths. Append preserves the mapping's
backend and cannot edit its name, description or owner. The 16 MiB derived-object
budget does not enlarge the 4 MiB native ceiling.

An existing actor-owned idempotency key is looked up before current configuration
admission. Its original backend profile and policy remain authoritative. Body
comparison uses the fixed 4 MiB ceiling so a changed body above a reduced original
limit still conflicts instead of being admitted under another policy. An exact
terminal replay returns the original receipt without storage or worker activity.

## One claimed attempt

Each explicit call claims at most once. Every metadata operation uses fresh
authority and fencing in a short transaction; no transaction remains open while
reading the source, accessing storage, or waiting for a worker.

1. Reserve immutable identities, digest/count, backend profile, and policy.
2. Claim an attempt under the database's 120-second lease and eight-claim limit.
3. For a submission, publish the private native object using create-only storage.
   Resume only reads the existing object.
4. Read and independently verify the complete object's scope, class, count, and
   digest, then mark the current generation staged.
5. Validate once with the exact reserved backend, or reuse an original durable
   eligible validation after current object verification.
6. Complete the attempt in a short transaction. A separate finalization
   atomically publishes eligible evidence into the mapping/revision catalog and
   terminal receipt. Decoded-invalid revisions retain their admission findings
   for later report and viewer routes; an undecodable artifact is rejected.

The first admission backend invokes the restricted Ferrule validator. Its trusted
registration captures the engine revision, worker build, adapter build, and
qualified byte limit. The result converter preserves actual validity,
provenance, diagnostics, counts, and truncation in the owned versioned evidence
envelope. Malformed results become protocol failures; enforced evidence overflow
is a separate policy result. Validation never creates an execution or deployment
capability.

## Recovery and results

`ResumeAsync` takes the original operation ID without a replacement source. A
missing object records `NeedsSource`; an explicit matching resubmission can supply
it. Storage integrity failures do not overwrite, repair, or delete objects. An
unavailable exact worker profile is retryable when validation is needed. Ready
evidence can recover without that worker registration, but always requires a
fresh verified stage and retains the original eligible attempt generation.

Retries preserve the original admission facts even when current configuration
changes. Tighter physical storage/backend constraints remain real constraints
and produce a categorical configuration/storage failure rather than rewriting
the captured policy or inventing a native validator report. Stale processes may
finish physical create-only work, but an expired generation cannot stage,
complete, or finalize metadata.

Caller cancellation is rethrown. After a granted claim, the service attempts
ordinary fenced cancellation bookkeeping for at most two seconds with a separate
token. Revocation, a lost fence, or unavailable metadata can leave the lease to
expire normally. There are no automatic retries, heartbeats, lease overrides, or
cleanup side effects.

The public application result contains a categorical outcome and, where
authorized, a safe operation receipt: scoped identities, phase, claim count,
display filename, times, and committed sequence/admission. Private paths,
references, lease tokens, worker configuration, and raw evidence are omitted.
[Saved reports](saved-reports.md) provide authorized findings and projections
through their separate read and derivation boundaries.

## Persisted admission contract

[Ingestion metadata](ingestion-metadata.md) owns the four-table persistence
boundary. Full organization/project/mapping/revision ancestry, native digest/count,
logical entry/dependency identity, selected backend, creator and committed sequence
remain immutable. The original [table/column specification](catalog-ingestion-plan.md#four-tables-and-immutable-ancestry)
and [exact grants](catalog-ingestion-plan.md#exact-roles-and-transaction-boundaries)
are retained for traceability; applied migrations and current schema guards define
the installed contract. Metadata editing is a separate [conditional write](catalog-reads.md#conditional-mapping-metadata),
not permission to mutate committed native facts.

Reservations freeze the trusted engine, worker build, adapter, policy
`weft.native-admission.v1` and effective limits. Engine/adapter identity text is
bounded to 256 UTF-8 bytes each and worker build to 2,048. The server registration,
not the client, chooses these values. Retained evidence keeps its original profile
when the configured Ferrule pin changes. The logical native entry is
`entry.native.json` with the admitted dependency identity.

Idempotency binds actor, organization, project, operation kind, append target and
key to accepted metadata, head condition and exact bytes. Generated identities,
timestamps and the currently deployed image are not replacement intent. Keys are
not expired or recycled by this workflow. AnyHead allocates the next committed
sequence while locking the mapping; EqualHead compares the exact expected revision
under that lock. First creation publishes mapping, sequence-one revision and
receipt atomically. Conflicts do not change the head or create placeholder mappings.

Confirmed matching valid and decoded-invalid results may commit; decode refusal
does not. Unsupported artifacts and enforced resource rejection remain rejections;
unavailability, timeout, isolation, cancellation or provenance/protocol faults do
not become semantic invalidity. Typed evidence preserves native owners/locators,
nullable validity, actual/emitted counts and truncation. Its 8 MiB serialized and
database-normalized bounds do not permit silent truncation or invented success.
Admission does not fabricate projection links or execution capability. Orphan
retirement belongs to [maintenance](private-object-maintenance-metadata.md), not
ingestion recovery. Neither cross-store atomicity nor power-loss durability follows
from process-restart recovery.

## Verification boundary

Contract, converter, and orchestration tests use explicitly synthetic snapshots
and ports. Database lookup tests use the actual restricted PostgreSQL roles.
Complete ingestion tests compose PostgreSQL, private filesystem storage, and the
qualified Ferrule container. A separate process probe interrupts the real service
at storage/validation/publication boundaries, waits for actual database lease
expiry, and checks recovery and stale-generation fencing. The probe is copied
only into test output and is not part of the application package.

Current native worker bounds can reject input before an evidence-envelope budget
is exceeded. Synthetic converter cases exercise that evidence budget; they do
not establish a native Ferrule fixture that can reach it. Qualification records
the source and cases actually executed, separately from inherited unchanged
engine and viewer evidence. A separate explicitly synthetic owned evidence
document tests the actual PostgreSQL JSONB normalization limit and its honest
rejection bookkeeping; it does not represent a native worker observation.
