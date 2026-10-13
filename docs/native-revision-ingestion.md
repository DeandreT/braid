# Native revision ingestion

`IRevisionIngestionService` composes the qualified metadata, private storage, and
native validation adapters. It accepts trusted server actor/project identities
and a readable stream. HTTP upload, identity extraction, and catalog screens are
subsequent increments; this service has no browser-facing route yet.

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
The later report/API increments provide authorized findings and projections.

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
