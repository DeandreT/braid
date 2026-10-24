# P3c: immutable native revision ingestion

Status: P3c1 metadata and P3c2 native workflow are qualified and merged in
[PR #26](https://github.com/DeandreT/weft/pull/26) and
[PR #27](https://github.com/DeandreT/weft/pull/27).
The implemented service boundary and recovery flow are described in
[Native revision ingestion](native-revision-ingestion.md).
P3b is qualified and merged in [PR #22](https://github.com/DeandreT/weft/pull/22) at `3da725b6b723eb5203c3d955e1aaef109a379eb9`.
This plan refines [parent #15](https://github.com/DeandreT/weft/issues/15) without changing the later P3 sequence.

## Ordered, focused delivery

| Increment | Source ownership and purpose | Dependency and qualification |
| --- | --- | --- |
| [P3c1 #23](https://github.com/DeandreT/weft/issues/23) | Domain facts, Application persistence contracts, PostgreSQL migrations/scoped metadata DAL, role guards and database tests | Qualified/merged P3b; actual PostgreSQL with explicitly synthetic admission evidence |
| [P3c2 #24](https://github.com/DeandreT/weft/issues/24) | Application stream/admission/recovery workflow and actual PostgreSQL/private-store/native-worker integration tests | Qualified/merged P3c1; full actual storage/worker/PostgreSQL workflow |
| Parent P3c | Complete immutable ingestion boundary | Complete only after both increments are qualified and merged; P3d waits for this |

Each increment has its own issue, feature branch and independently buildable PR.
P3c1 is a trusted server metadata DAL: it does not accept evidence from an HTTP client, perform storage/native I/O or claim native ingestion qualification.
P3c2 must prove the exact object exists and verifies before linking it; neither increment changes HTTP, Angular, P2 protocols or P3b's store contract.
Implementation and qualification state remain separate; local and independent
exact-source evidence is required. Remote CI was initially billing-blocked and
later passed for PR #27; only each increment's observed result is recorded as
additional evidence.

## Application boundary and admission defaults

Use existing `ActorContext`, `ProjectScope`, `RevisionScope` and a BCL `MappingScope`.
Application exposes create-with-first-revision, append-revision, resume and get-own-receipt operations; PostgreSQL exposes only typed workflow-specific persistence methods.
No raw connection, generic repository, caller-controlled write flag, private object reference, lease token, worker build or validation evidence is exposed to a client.
Create/append consume caller-owned streams from their current positions; reject empty input or more than the effective server-owned native limit.
That limit is at most 4 MiB and the minimum of Application/native-store/backend limits; configured reductions are allowed.
The 16 MiB derived-object limit never widens native admission; native bytes are copied/hashed/stored unchanged, without a JSON round-trip.
Initial backend is `ferrule`; logical entry is `entry.native.json` with the existing empty dependency manifest.
Backend selection is an ID from a server registration, not a caller-supplied engine/image; append preserves the mapping's selected backend.
Persist the reserved trusted engine revision, immutable qualified worker image build, adapter build, policy identity `weft.native-admission.v1` and effective limits.
Profile text limits: backend ID 128 UTF-8 bytes, engine/adapter 256 each, backend build the existing 2,048-byte bound.
The current profile uses Ferrule revision `576012aa17c403d9c3fcb8659ed4d3a47ba06864` and adapter `weft.ferrule-adapter.v3`; the immutable worker build is server configured. Retained ingestion evidence keeps the engine, image and adapter identities observed at admission and is not relabeled when the current profile changes.
Filename is display metadata only: strict UTF-8 basename up to 255 bytes, no NUL/control/slash/backslash or `.`/`..`; default `entry.native.json`.
Create name is trimmed/nonempty, 1–200 characters; description is at most 4,000. Creator and initial owner equal the actor; duplicate names are allowed.
Append cannot edit mapping metadata, owner or backend; those policies remain later work.
Outcomes explicitly distinguish committed valid/decoded-invalid, InProgress, NeedsSource, RetryableFailure, Rejected, HeadConflict, AttemptExhausted and IdempotencyConflict.

| Native result | Admission outcome |
| --- | --- |
| Confirmed matching Succeeded / IsValid true | Commit valid immutable revision |
| Confirmed matching InvalidMapping / IsValid false | Commit decoded-invalid revision and findings |
| Confirmed matching InvalidMapping / IsValid null / `ferrule.nativeDecodeFailed` | Reject; no mapping/revision |
| UnsupportedArtifact or known enforced ResourceLimit | Reject; preserve actual provenance/nullable validity |
| Digest mismatch, unexpected null-invalid, protocol/provenance fault, unavailable/failure/timeout/isolation/cancellation | Operational attempt; resumable within claim budget |

A known response-budget rejection need not have confirmed native provenance; it never claims decode success or semantic invalidity.
Null validity never becomes a successful empty report. Admission never grants preview/deployment capability.
Rejected/conflicted/exhausted staged objects remain private/unlinked for P3f; P3c performs no deletion or sweep.

## Four tables and immutable ancestry

New embedded migrations begin at `0002`; preserve `0001` and its exact checksum.
Use PostgreSQL 16.15/Npgsql 10.0.3, migrator ownership, restrictive FKs and exact organization RLS USING/WITH CHECK guards on all four tables.
In this section `S` means `organization_id, project_id`; all identity UUIDs are nonzero.
`mappings` PK `(S,id)`: `name, description, backend_id, creator_principal_id, owner_principal_id, created_at, updated_at, metadata_version, head_revision_id, head_sequence`.
Project FK is `(S)`; creator/owner each FK `(S,principal_id)` to project membership. Active authority remains a fresh SQL predicate.
Metadata version starts at 1; head is nonnull/positive. Backend/creator/owner/metadata are immutable in this increment.
Head FK `(S,id,head_revision_id,head_sequence)` references revision scope/ID/sequence and is DEFERRABLE INITIALLY DEFERRED.
A new mapping is inserted with its first final head; no committed placeholder mapping exists. Head updates increase sequence by exactly one.
`revision_ingestions` PK `(S,operation_id)`: immutable `actor_principal_id, operation_kind, idempotency_key, fingerprint_version, fingerprint, target_mapping_id, mapping_id, revision_id, object_id, object_class, native_digest, native_byte_count, document_id, dependency_manifest_id, backend_id, engine_revision, backend_build, adapter_build, admission_policy_id, effective_native_limit, filename, create_name, create_description, head_condition, expected_head_revision_id`.
Its mutable columns are `phase, lease_owner, lease_token, lease_generation, lease_expires_at, claim_count, staged_at, eligible_attempt_generation, last_status, completed_at, committed_revision_id`.
Project/original actor use scoped FKs. Append target is the existing scoped mapping/backend and equals reserved mapping ID; create target is null and mapping ID is only reserved.
Unique scoped future revision/object identities and an exact reservation-facts unique tuple support the eventual revision FK; committed pointer must equal the reserved revision.
`revision_ingestion_attempts` PK `(S,operation_id,generation)`, reservation FK: immutable `claimant_instance, claim_token, started_at`.
Completion/progress columns are `object_verified_at, finished_at, backend_status, admission_classification, confirmed, is_valid, total_issue_count, emitted_issue_count, truncated, evidence_schema, evidence, reused_attempt_generation`, plus exact scope/digest/backend-profile provenance.
Completed evidence is immutable. Reused generation is a scoped same-operation FK to original completed evidence; recovery does not invent a new worker run.
`mapping_revisions` PK `(S,mapping_id,id)`: `sequence, object_id, object_class, native_digest, native_byte_count, document_id, dependency_manifest_id, backend_id, creator_principal_id, committed_at, operation_id, initial_attempt_generation`.
Unique mapping/sequence and mapping/revision/sequence tuples support ordering/head integrity; mapping/backend, creator, reservation-facts and original admission-attempt FKs preserve full ancestry.
Native class/count/digest/logical entry and creator must match reservation facts. No UPDATE/DELETE, native byte/JSON column, physical path or mutable validation status.
Native counts are 1..4,194,304; digests are lowercase SHA-256. Fixed document/dependency identity and bounded metadata/profile checks reject inconsistent facts.
SECURITY INVOKER transition triggers enforce terminal receipts, immutable completed evidence/headers and monotonic heads/claims; no SECURITY DEFINER functions.
Evidence is versioned typed validation JSONB, not native JSON: retain decimal string IDs, native owners/locators, counts, null validity and truncation.
Enforce the existing qualified model item/text/depth budgets, at most 8 MiB serialized UTF-8 and a database `octet_length(evidence::text)` ceiling.
If normalized evidence exceeds its bound, record a minimal ResourceLimit rejection; never silently truncate or create success. No projection locators/viewer links are fabricated.

## Exact roles and transaction boundaries

Provision `weft_ingest` in owned bootstrap before migration; `weft_migrator` retains no CREATEROLE.
Existing `weft_runtime` keeps its five SELECT grants and read-only transaction API, including SET TRANSACTION READ ONLY.
Introduce a separate `weft_ingest` pool/internal write helper: ReadCommitted, transaction-local bound organization, no preset/rebinding, rollback/dispose/reset.
Use transaction-local one-second lock/five-second statement timeouts; lock failures are operational retries.
Ingestion grants: schema USAGE, SELECT on old five/new four tables, INSERT on the four new tables, and only the following column UPDATE grants:
`mappings`: `head_revision_id, head_sequence, updated_at`.
`revision_ingestions`: exactly its mutable columns listed above.
`revision_ingestion_attempts`: exactly its completion/progress columns listed above; immutable claim/provenance columns remain ungrantable for UPDATE.
No ownership-table mutation, revision UPDATE, DELETE/TRUNCATE/REFERENCES/TRIGGER, journal access, owner/privileged membership, BYPASSRLS or schema/database CREATE.
Preflight checks exact effective table/column/PUBLIC/inherited/GRANT OPTION privileges, installed policies and transition triggers; no broad/default grants.
Strengthen the existing reader guard against column-only INSERT/UPDATE/REFERENCES and unknown SELECT grants without changing its read-only API.
Protect exact scoped policies against concurrent DDL with Access Share locks; do not lock memberships or grant them UPDATE.
RLS only guards organization; every meaningful workflow operation fresh-joins active principal, organization admission and exact-project Publisher/Administrator.
Server-issued identity remains mandatory; no HTTP/auth wiring or synthetic hosted identity is added.

## Idempotency, claims and one-statement publication

Key scope: actor/organization/project/kind/target mapping (append)/nonzero UUID key. Separate create/append filtered unique indexes prevent null-target duplicate creates.
Fingerprint v1 uses specified length-prefixed canonical UUID/UTF-8/null encoding of accepted intent/head/backend/document/dependency identity plus exact digest/count.
Exclude generated IDs, timestamps and current deployed image; reservation separately freezes its profile.
Same key with changed bytes/name/description/filename/backend/head conflicts, including after completion; bounded changed-body retries must be digested before replay.
Receipts never expire/reuse keys in P3c. Matching retries recover the same future identities; uniqueness races compare in a fresh scoped statement.
Only the original actor with fresh exact-project upload permission can resume/read its receipt; another publisher/admin cannot adopt the operation.
Leases use 120 seconds of DB time, random token/instance UUID, increasing generation and at most eight explicit claims; no auto retry/heartbeat/Wolverine.
States are reserved/staged/ready, then committed/rejected/head_conflict/attempt_exhausted; released/expired leases may be reclaimed, live claims return InProgress.
Old tokens cannot publish/replace evidence/reset receipts; a stale physical writer may finish create-only I/O and leave private unlinked bytes.
Default AnyHead locks the mapping and allocates committed head sequence +1; out-of-order request/worker completion cannot regress a head.
Optional EqualHead compares the exact expected revision under that lock; conflict records a terminal receipt without revision/head mutation.
Create inserts mapping/revision/receipt together with sequence 1; different create keys may create separate same-named mappings.
Before source read obtain fresh permission, then bound/hash outside a transaction. Reserve/claim with fresh authority in a short transaction and commit.
Outside transactions publish create-only, fully ReadVerified, admit exact NativeArtifact, and invoke qualified ValidateAsync once.
Persist stage/evidence only under current fencing; eligible evidence requires exact scope/digest/reserved engine/image/adapter/policy agreement.
Final publication is ONE scoped SQL statement freshly joining current authority, original actor, ready evidence and current unexpired lease.
Lock reservation/mapping, use RETURNING dependencies, insert revision, change head once and finalize receipt once; create INSERT already sets head. Roll back inconsistent counts.
P3a statement-snapshot revocation semantics remain: revocation committed before final statement prevents publication; concurrent revocation after its start does not promise commit-time serialization.
No membership UPDATE, SECURITY DEFINER authority helper or stronger serializable-membership promise; no row is modified twice by sibling CTEs.
After crash, verify native object under a reclaimed lease; reuse matched completed evidence through its original generation, or rerun only the exact reserved profile.
Missing uncommitted object returns NeedsSource; matching resubmit can recreate that reference. Conflict/corruption cannot overwrite/repair/delete.
Lost reply after commit replays the same receipt after fresh permission. No object/worker await occurs with a database transaction open.
No cross-store atomicity or power-loss durability claim: P3b process-restart profile is preserved; cleanup/deletion claims and retention policy wait for P3f.

## Qualification gates and exclusions

P3c1: actual fresh/upgrade/concurrent/restart migrations and atomic journal/checksums; exact roles/column grants/policies/triggers; read-only preservation and same-pool scope reset.
Also actual PostgreSQL composite ancestry/immutability/terminal checks, original-actor authority, key/body conflicts, same-key creates, head ordering/CAS and lease fencing with explicit synthetic evidence.
P3c2: actual private store/qualified worker/PostgreSQL byte preservation and admission outcomes, wrong scope/profile/class/digest/count, independent restart/idempotency and out-of-order concurrent uploads.
Kill/fault before object, after object, after validation, after ready and after commit; demonstrate same IDs, no duplicates/overwrites/linked missing objects, no deletion.
Revoke authority during blocked inspection; test fresh final denial and documented inflight snapshot semantics; stale claim and another actor cannot publish.
Block actual source/store/worker and observe no held metadata transaction; qualify limits/cancellation, missing/corrupt object recovery and bounded nullable-validity reports.
Keep restricted-worker host-path/network sentinels and absence of storage/database mounts/credentials; exact-source clean gates must run actual dependencies with no skipped required integration cases.
Defer HTTP/development identity (P3d), cache/viewer links (P3e), retention/deletion (P3f), Angular catalog (P3g), Entra/AI/preview/Azure/deployment and native editing.
SQL references: [CTE/statement semantics](https://www.postgresql.org/docs/16/sql-select.html), [column privileges](https://www.postgresql.org/docs/16/functions-info.html), [locking](https://www.postgresql.org/docs/16/explicit-locking.html), [constraints](https://www.postgresql.org/docs/16/ddl-constraints.html).
