# Archive, deletion and retention design

## 1. Scope

This design covers reversible project/mapping archive, permanent deletion, individual resource deletion and organization retention. Read it alongside [organization switching](organization-switching-design.md) for request-local organization admission.

### Required contracts

* The append-only journal uses identifiers independent of project/mapping/principal foreign keys and only an organization FK. Source capture and source writes share a transaction. Reads require current organization or exact-project administration authority.
* Usage and fixed failure categories remain independent of chat, with only organization and internal usage-child FKs. Coverage is immutable. Preserve independently known input/output/total counts, unknown-round counts and admission time when deleting content.
* Quota reservations have no chat/project FKs. Reservation identity includes private fingerprints, conversation and turn IDs. Incomplete, cancelled and interrupted work does not release reserved capacity. The cross-family gate below precedes conversation and quota-state locks.
* `0007_private_object_maintenance.sql` and `ObjectCleanupRetirement` admit only orphan reasons, enforce their 24-hour grace and protect committed revisions and successful reports. Publication/retirement rows reference ingestion/derivation owners, and cleanup current/completed pointers form cycles. `PostgresPrivateObjectMaintenanceStore.Materialization.cs` reconstructs full publication owners from live metadata, not only object IDs.
* Filesystem v2 and Blob use exact-object physical fencing. `IPrivateObjectRetirementStore.RetireAsync(ObjectCleanupClaim)` accepts an orphan claim, not catalog-deletion authority. Blob replaces the final slot with a permanent empty fence; filesystem v2 unlinks the admitted payload inode and retains gates/fences. Neither is a recursive directory deletion API.
* `0018_build_publication.sql`, `PostgresBuildObjectStore` and [build persistence](build-persistence.md) store build artifacts as private PostgreSQL `bytea`, not filesystem/Blob maintenance objects. Approval/qualification bodies and results can contain private intent, samples and execution data. Retain the particular artifact/audit evidence required by policy, not all build rows.
* `local_release_targets.active_release_id` is the authoritative active pointer. A historical release with `phase='active'` need not be current. Disabling a target does not clear its pointer; deletion requires the separate retirement operation specified below.
* `0011_private_chat.sql`, `0032_workspace_chat.sql` and `ChatPersistence.cs` make chat creator-private, including from administrators. Baseline expiry erasure requires expiry and no active turn, and retires identities with content erasure in one transaction. Workspace conversations have immutable optional focus FKs but may discuss other projects. Immediate owner or project-deletion erasure requires new explicit authority.
* Immediate RESTRICT FKs and append-only guards prohibit a generic cascade purge. In PostgreSQL, `DEFERRABLE` does not make `ON DELETE RESTRICT` defer like `NO ACTION`; `SET CONSTRAINTS ALL DEFERRED` is not a purge strategy.
* Baseline lock order differs across administration, builds/releases/cases/runs and chat. A late project-lock trigger cannot repair those conflicting orders.
* Existing preview retention has a 24-hour default configurable from 1 minute to 7 days; invocation response retention is a fixed 24 hours in schema; chat host configuration requires `retentionDays`. These are existing intrinsic contracts, not selected organization defaults. There is no universal 30-day production default or equivalent general regression-run expiry column.

## 2. Product contract and authority

### Resource lifecycle

Use four states: `active`, `archived`, `deleting`, `deleted`.

* Project archive does not rewrite each mapping. Effective mapping state is the intersection of project and mapping lifecycle. Restoring a project leaves independently archived mappings archived.
* Archive is reversible only until a hard-delete request commits. It hides resources from ordinary lists/search, but exact authorized reads continue, including revisions, reports, runs, operations history and creator-owned chat. Existing expiry still applies; archive never extends it.
* Explicit archived/all filters reveal only resources the actor can already read. Search, workspace tools and lists share filter implementation and cursor binding.
* Archive denies new native uploads/reservations, append/commit, metadata changes, import/export review creation/acceptance that writes state, derivations/repairs, preview/regression execution admission, automation evaluations, approvals, builds, qualification, release registration/activation/rollback and new scoped chat/provider/tool work. Existing output may be read without deriving missing data.
* Cancellation, safe terminal failure/accounting, grant revocation, administrative access management, deletion and restore are explicit control-plane exceptions. They cannot publish revisions, reports, successful output, artifacts or new provider rounds. There is no generic administrator bypass.
* Archive increments an authoring write epoch. Work admitted before archive cannot publish new catalog content after archive commits, even if restore occurs before it returns. Restore increments the epoch again. A fresh deliberate request is required; old claims cannot revive.
* **Existing deployed endpoints keep serving while archived.** Archive controls authoring/deployment, not production shutdown. Runtime admission and required invocation/audit writes are a narrow exception, bound to the existing active target/release, current enabled state and current grant authority. They cannot create deployments, change releases or publish catalog content. Runtime operations use target/release lineage, not the authoring archive epoch. Disable and retire remain separate explicit actions. A non-null active pointer blocks permanent deletion even when the target is disabled.

### Permission matrix

Every user operation requires a current active internal principal and active admission to the exact organization. HTTP identity comes only from the verified cookie/CSRF or delegated bearer flow. Never accept actor, organization, creator, role, connection string or worker identity from a request body. Machine invocation tokens do not authorize application routes.

| Operation | Additional authority |
| --- | --- |
| Read archived project/mapping | Existing exact-project read membership; payload and own-run restrictions unchanged |
| Archive/restore project or mapping; request permanent delete | Active `administrator` membership of the exact project, with `administration_authorized(org, actor, project)` semantics |
| Read deletion summary/job | Current exact-project administrator while the project exists; after removal, current organization administrator, or original requester still actively admitted to the organization for a minimal own receipt only |
| Read/configure organization retention | Current organization `administrator`; no additional project/chat content access |
| Delete regression case | Exact-project `publisher` or `administrator`, matching fixture-write authority; not limited to the fixture creator |
| Delete unreferenced build environment | Exact-project `administrator`; all versions considered and no reference anywhere to any version |
| Delete preview | Original submitter with current preview authority for that project, not an arbitrary administrator |
| Delete conversation | Exact original creator and existing conversation admission; administrator is not a content owner |
| System retention/deletion | Dedicated restricted worker functions acting on committed jobs or configured due policies; no user impersonation or administrator content-read path |

Organization and project Administrator are not interchangeable. Do not add a broad organization-admin catalog-delete override.

### Active release protection

At deletion admission, join every current target pointer through its release, qualification and build approval/run ancestry. Reject if it resolves to any mapping/revision in the target set. Check target scope too; malformed cross-scope ancestry must fail closed. Disabled targets with non-null active pointers still block. Any inconsistency blocks the job with a categorical integrity error.

Add a separately confirmed Administrator-only `retire` action to release operations. Require the exact target, expected target version and active release ID, and operation ID. Atomically disable the target, clear its active pointer, advance its version and append target/release-operation audit. Preserve release/build history. This is not ordinary `disable`, and delete never invokes it automatically. Keep the active-pointer FK and historical activation records intact.

Archive/delete and target registration/activation/retire share the lifecycle gate. Recheck pointers within deletion admission, not only in the preview. A release that wins first blocks deletion; a delete fence that wins first denies activation.

## 3. Additive schema

The following are design names for new schema. Use typed columns, checked enums, nonzero UUIDs, finite database timestamps, explicit bounds, organization RLS and `REVOKE ALL FROM PUBLIC`. Private evidence tables have no user-readable grant. Narrow owner-defined functions require fixed search paths and explicit `session_user`/organization checks. The application host never receives migrator credentials.

### 3.1 Lifecycle and durable identities

`resource_lifecycles`:

* PK `(organization_id, resource_kind, resource_id)`; kinds include `project`, `mapping` and supported individually deletable resources where needed.
* `project_id` and nullable `mapping_id` express immutable ancestry. For projects, `resource_id=project_id`; for mappings, `resource_id=mapping_id`.
* Fields: `state`, `version bigint > 0`, `write_epoch bigint > 0`, `changed_at`, nullable `archived_at`, `archived_by`, `deletion_job_id` and `deleted_at`.
* FK only to organization and, where needed, durable project/mapping lifecycle identity, not erasable catalog rows. Surviving identities prevent reuse; retain no names, descriptions or filenames.
* Every live project/mapping has exactly one matching row. Project creation/ingestion-create inserts lifecycle identity atomically. Missing lifecycle metadata fails writes closed.
* Guard transitions and immutable ancestry. `deleting`/`deleted` cannot reopen. Generation overflow fails explicitly.

`lifecycle_operations`:

* PK `(organization_id, actor_id, operation_id)`, bound to resource kind/ID, action, exact precondition, confirmation ID where applicable, creation time and categorical result/job ID.
* Canonical identity contains fixed action/IDs/versions, not arbitrary request JSON or private display names. Retain the receipt after catalog removal.
* Same ID and exact request returns its receipt under fresh authority; any changed field conflicts. Receipt replay never authorizes dispatch or another mutation. Clients do not automatically resend mutations.

`lifecycle_work_admissions`:

* PK `(organization_id, work_kind, work_id)` with immutable project/mapping IDs, admitted project/mapping epochs, time and categorical settlement state. Reuse exact turn/run/operation/build IDs.
* Ingestion-create requires the project epoch and reserves its future mapping UUID in durable lifecycle identity. It cannot materialize after its project starts deleting.
* Insert with existing admission/identity reservation. `lifecycle_work_scopes` children record every actual scope for multi-resource work, sorted for locking. Original worker lease/token remains mandatory.
* These do not replace idempotency, creator or claim checks. Compact finished admissions only under explicit policy to no-content identity tombstones; erased IDs never become fresh work.

Individual preview/environment/case deletion reserves the original ID in lifecycle identity or typed `resource_identity_tombstones`. Preserve preview idempotency keys in their original submitter+project/mapping namespace, without sample hash/body, so repeated submissions cannot execute again. Chat retains its stronger existing `chat_identities` contract.

Ingestion also retains the original client lookup namespace `(organization_id, project_id, actor_id, kind, idempotency_key)`, bound to the original operation and resulting mapping/revision identity. Both create and append consult these no-content tombstones before allocating any operation/mapping ID. A deleted operation's key returns a fixed deleted/conflict receipt, even when its ingestion rows were purged while the project remains active. Never expose client keys in public lifecycle/audit records or retain payload bytes merely for deleted-request comparison.

### 3.2 Jobs and restricted manifests

`deletion_jobs`:

* PK `(organization_id, job_id)`, unique target lifecycle identity, original requester and operation ID (or retention policy ID/version), target kind/ID, project/mapping IDs, admitted epochs and immutable plan version.
* States: `fenced`, `inventory`, `quiescing`, `catalogPurging`, `objectsPending`, `completed`, `blocked`. Store phase cursor, version, timestamps, categorical blocker and counts.
* Public projections omit private conversation creator IDs, payloads, internal hashes, locators, claim tokens, raw exceptions and provider errors. Dependent private-record counts are available only to the authorized deleting administrator, without titles/content.
* Claim ownership has generation, instance ID, lease token/expiry; append-only `deletion_job_attempts` retains each claim/outcome. Deleting never rolls back to archived. Blocked work retains its fence and resumes the same intent after repair.

`deletion_manifest_rows`:

* PK `(organization_id, job_id, entity_kind, entity_key)` with exact frozen ancestry, discovery phase/version, evidence-copy and row-purge flags. Keys use typed UUID/version fields, not caller-selected tables or SQL.
* Discover the immutable target closure in bounded keyset pages after fencing. Only the final post-quiescence reconciliation may set `inventory_complete`, after every predefined stream reaches EOF and an exact anti-join proves no omitted dependent row. A bounded first page is not a complete inventory.
* Use a static SQL allowlist of the graph below. New referencing tables require graph-contract tests and a new plan version, not generic catalog traversal or user-provided predicates.

`deletion_objects`:

* PK `(organization_id, job_id, object_target_id)`; unique exact `(storage_binding_id, object_class, organization, project, mapping, revision, object_id, target_kind, publication_id-or-null)`.
* `target_kind` is final or tracked temporary. Inventory native/report objects and candidates from immutable source metadata; temporaries require positive registrations. Unknown digests/lengths remain nullable.
* Store approved storage kind and root/control identity, origin snapshot ID, first retirement time, current/completed attempt and categorical disposition. Never accept arbitrary paths, blob URLs, SAS tokens or connection strings.
* `deletion_object_attempts` retains generation, attempt/instance/token/lease, times and physical observation. Terminal success requires actual `Deleted` or safe `Missing`. No FK to live catalog data.

Resolve physical bindings from trusted server configuration before destructive work. Provide an operator-managed `private_storage_bindings` registry if needed. Requests cannot select adapters. Publication `root_id`/kind must match the binding. Unsupported roots block deletion rather than becoming Missing. No drive/path discovery or relocation is part of deletion.

### 3.3 Retained evidence

`retained_build_artifacts` has PK `(organization_id, build_id)`, immutable scope IDs, original actor, environment ID/version, artifact profile identity, creation/publication facts, exact artifact bytes/digest/length, trusted retention-policy ID/version, nullable `retain_until` and explicit `retention_unconfigured`. Copy and compare bytes/digest in the purge transaction before removing `build_publications`. Unknown obligation means hold, not permission to expire. Keep the store private without a new general download capability. Deleted-project membership cannot authorize reads. Public receipts expose only authorized counts and retention categories/deadlines.

Keep `audit_log_events`, `ai_usage_turns`, `ai_usage_failures`, `ai_usage_coverage`, `ai_quota_reservations`, quota policy/state and `chat_payload_audit` outside the purge manifest. Preserve private quota fingerprints, never exposing them in audit/status/diagnostics. Chat/content removal does not imply usage deletion or quota settlement.

For source audits with live FKs (`local_release_audit`, `invocation_grant_audit`, `machine_invocation_audit`), use detached typed `retained_release_audit`, `retained_grant_audit` and `retained_invocation_audit` tables. Preserve every original row, identity and time with the same typed fields plus immutable scope IDs. Exact copy-and-compare precedes source removal; the central journal remains untouched. These private records are not copied wholesale into the public journal. Include no payload, response, intent or arbitrary before/after JSON. Exact retained keys cannot duplicate or reinterpret events. Source deletion is a narrowly authorized move, not general audit deletion permission.

For maintenance, add detached `retained_object_origins`, `retained_object_publications`, `retained_object_retirements` and `retained_object_cleanup_attempts`:

* Snapshot minimal reconstructable materialization fields: full canonical report key and typed original attempt or native owner, all publication/binding/format facts, retirement identities and attempt generations/results.
* Do not copy whole ingestion evidence JSON or payloads. Owner/key material, tokens, inode/ETag evidence and digests remain private, never audit/status output.
* Validate restored `PrivatePublicationRecord` field-for-field and byte-for-byte before source removal. An opaque hash cannot replace reconstructable owner evidence.
* Retain existing orphan retirement policy/identity. Attach a catalog job to the exact completed permanently fenced target, or explicitly transfer pending work. Do not invent eligibility times or rewrite attempts. One shared exact-target arbitration table prevents conflicting old-maintenance/new-deletion authority.
* Commit immutable snapshots and exact-target arbitration together before copy-and-remove of source rows. Existing orphan commands must resolve transferred identities to detached evidence without losing attempts. Orphan eligibility remains unchanged.

Retained stores have only organization/durable-evidence FKs; they cannot pin live mappings, revisions, ingestions, derivations, environments, conversations or project memberships. Audit and usage records keep their own independent fixed retention, and retained artifacts keep their own obligations. Ordinary retention settings cannot shorten them or erase permanent identity tombstones or storage fences.

### 3.4 Retention policy

`organization_retention_policies` has PK `(organization_id, family)` with exactly `preview`, `regressionRun`, `conversation`, `invocationHistory`; version; mode `retain` or `expire`; nullable whole-second duration; effective time and changed actor. Distinguish explicit retain from missing/unconfigured policy. Expiry needs positive bounded duration. A separate organization policy generation supplies a strong ETag.

`resource_retention` records exact durable resource identity, immutable policy version, basis time, calculated deadline or explicit hold, and optional `protected_not_before`/reference-hold category. Admission and deadline are atomic. Use database time, UTC and overflow-checked arithmetic.

Chat's immutable `retain_until` belongs to canonical turn context. Never rewrite it, replay an old envelope with another deadline or make expired data readable. A sidecar deadline may shorten retention only through explicit confirmed erasure; reads use the earliest applicable deadline. Existing intrinsic preview/chat/invocation expiry remains a maximum readability deadline unless a separately designed contract migration changes it.

Organization retention has no default durations. Nothing is deleted automatically under organization policy until an organization administrator configures a policy for that family; until then, items are held and shown as “not configured”. Audit and usage records keep their independent fixed retention, which these settings cannot shorten. Preserve explicit deadlines; never derive policy from test settings.

## 4. Transaction and race protocol

### Gate and lock order

Use a namespaced transaction-scoped PostgreSQL advisory organization gate, deterministically keyed from organization ID. Collisions add serialization, never authority. Every relevant mutating entrypoint acquires EXCLUSIVE before principal, organization, quota, lifecycle, target, conversation, job, origin or child locks. Reads that acquire row locks take SHARED first. Simple non-locking reads need no gate and retain fresh ACL checks. Never upgrade SHARED to EXCLUSIVE.

Short database mutations serialize per organization, not across external work. Do not replace this with project-only locking until cross-family tests establish a safe order. No database/advisory lock spans storage I/O, provider calls, compiler/container work, HTTP streaming or notification transport.

Within the gate, acquire:

1. Organization row and policy-state rows (`administration_state`, quota-state, retention-state) in fixed order as needed.
2. Principals by UUID, then organization memberships/admin roles by principal.
3. Project lifecycle/catalog by project UUID, then project memberships by project/principal.
4. Mapping lifecycle/catalog by project/mapping UUID.
5. Shared environment versions, release targets, then workload/conversation/job identities by declared table rank and UUID.
6. Ingestion header or derivation key, then origin/publication/exact-object arbitration, then attempts.
7. Child data and journal inserts.

Collect all scopes before workload locks. Discovering another project after lower-ranked locks requires stopping and restarting through the normal explicit transaction boundary with the full scope set. Sweeps use one organization per transaction. Online owner/bootstrap mutations participate in the gate; migrations/bootstrap stay offline where existing procedures require it.

Entrypoint changes are mandatory. Late triggers are defense in depth, not the lock-order implementation. Restricted direct-SQL adapters, particularly ingestion/derivation/chat, acquire the gate at transaction start; admitted statement-level guards also acquire/recheck it before writes. A deliberately misordered direct client may be aborted by PostgreSQL deadlock detection but cannot gain unsafe success. Never automatically retry a transaction that may have produced a user mutation.

### Admission and post-wait checks

Use READ COMMITTED. After gate/row waits, run a separate statement to check current identity/admission/role, lifecycle state/epoch, strong preconditions, ownership and active pointers. Do not reuse pre-wait snapshots or pre-wait `statement_timestamp()` for deadlines. Use a post-wait database clock; HTTP prechecks do not replace it.

Archive/restore atomically updates state/epoch, stores a durable receipt and appends one event. Exact read-only replay neither increments epochs nor appends events. Archive establishes its fence without enumerating all work synchronously.

Every authoring admission, claim, materialization, publication and continuation checks captured epochs and original lease. Pre-archive work may be observed/cancelled but cannot become new work or publish success. Replaying committed immutable results is a read, not an upload recommit/provider dispatch. Deployed runtime follows its narrow exception with target-version, enabled-state and current-grant fences.

Cover every port: project creation; native reserve/claim/stage/commit; import/export review; metadata; derivation reserve/repair/materialize/finish and publication reserve/bind; preview/regression submit/claim/complete; automation enqueue/claim/settle; build environment/approval/stage/publication/claim/finish/qualification; target operations; invocation claim/reclaim/result; chat create/start/reserve-round/append/citations/data-sharing; workspace focus/tool resolution. Automatic workers cannot re-enqueue archived work as a backend retry.

### External work and quiescence

* Archive commit is the point after which new authoring/deployment work is denied. Wake controllers to cancel affected preview/compiler/provider work without claiming remote execution never happened. Do not cancel/disable deployed traffic because of archive.
* A narrow closure/accounting path preserves actual acknowledged provider-round terminal usage, but cannot append prompts, responses, tool output or citations. Uncertain rounds remain uncertain; no refund or redispatch.
* Workspace admission checks immutable focus; tools check their resolved scopes. One archived project does not disable unfocused organization chat. Do not feed newly fetched archived/deleting data into another provider round. Previously disclosed bytes cannot be retracted.
* Hard deletion waits for or safely terminates admitted writers/controllers using their real fences/leases. Lease expiry does not prove a filesystem writer released a file; physical gates/leases exclude publication.
* `quiescing` is not `completed`. An unreachable controller can remain uncertain while cleanup proceeds only once no append/resume path remains and one-shot identities/usage reservations are durable. Do not recreate provider permission to settle the job.

## 5. Hard delete and graph

### Request protocol

1. An administrator requests a read-only preview for an archived resource. Return a short-lived opaque server-protected token bound to random confirmation ID, actor, organization, exact resource, effective lifecycle versions, plan version, expiry and impact categories. GET does not persist/consume it. The destructive transaction validates protection and reserves its ID/digest atomically with the job so another operation cannot consume it. Return it only in no-store authorized responses, never URLs/logs/audit. Display names are not identity.
2. Deliberate confirmation submits operation/confirmation IDs and exact `If-Match`. Within the organization gate, recheck authority, archive state, versions, expiry and all active pointers; persist the job and irreversible `deleting` state/epoch, consume confirmation and append `deleteRequested`.
3. Return `202` and job/status location only after acknowledged commit. Unknown commit outcome is unconfirmed; read the original operation ID to resolve it. Do not automatically retry DELETE/POST.
4. A worker may collect preliminary inventory, then quiesces all admitted work and completes terminal closure/accounting. **Final bounded inventory and retained-evidence copy reconciliation occur after quiescence**, including terminal chat events and invocation source audits created during closure. Only that generation may set `inventory_complete` and permit purge. No later closure path can create live catalog dependents; independent retained usage/accounting remains outside the purge. Fences deny all new reference attachments, including restricted SQL/worker paths.
5. Physical deletion uses immutable manifests after catalog removal. Final reconciliation verifies no manifest live row remains, every retained copy exists and every physical target is acknowledged Deleted/Missing before `deleted`, job `completed` and `deleteCompleted`. Catalog absence alone is not completed erasure.

Purge large mappings in bounded keyset batches of closed independent components: conversations, runs, build graphs, derivation keys, then non-head revision/ingestion pairs. Keep the head revision and ingestion/attempt component until the transaction that removes the mapping. No committed surviving mapping may have a dangling head. Batch older unreferenced case versions but retain the head until its header is removed atomically. Project deletion includes mapping closures, uncommitted future mappings and project-only data before membership/project removal. Nested mapping deletion joins the project job closure, not a racing purge. Retries use committed progress without duplicate copies or lost evidence.

### Exact graph and order

| Family | Required disposition/order |
| --- | --- |
| Chat | Retire identities, close/account for active turns, remove conversation/turn/round/event content in existing cascade direction. Never cascade into reports, runs or usage. Immediate erasure needs job-bound authority, not falsified expiry. |
| Search | Remove `catalog_search_report_index`, including private snippets, before derivation attempts. Fenced workers cannot recreate it. |
| Notifications/automation | Stop claims/publication; remove matching delivery attempts, deliveries, recipient notifications, events, endpoint windows, evaluations, suites and mapping/project automation. Match exact ancestry. Keep project subscriptions/destinations for sibling mappings; remove them only for project deletion. Never deliver erased-content notifications. |
| Invocations/grants/releases | Copy source audits; remove response bytes/operations metadata, then invocations; remove grants after audit copy. Remove release-operation metadata/target events and releases only after active-pointer checks. Preserve rollback audit facts. Delete targets after dependent releases/grants/invocations. |
| Builds | Copy required artifact bytes/audit facts. After release references are gone, remove qualifications including private proof/results, publications, runs and approvals in FK order. Mapping deletion retains shared project environments; project deletion removes them after all references. |
| Regression | Remove automation evaluations first and run/private results after qualification references. Delete case versions/headers with their deferred head constraint. Do not delete sibling fixtures. |
| Preview | Remove terminal runs and `preview_documents` through existing ownership/cascade after saving replay tombstones. Source-preview expiry never deletes a copied regression fixture. |
| Native/reports | Snapshot/detach object evidence; remove search/chat report pins, derivation attempts/keys, import/export review/provenance, revision/ingestion/attempt components and mapping head links. Include unfinished create/append operations with future IDs in scope. |
| Project-only | Remove payload grants, subscriptions/destinations, creation receipts after identity tombstones, then memberships after creator/owner dependencies, then project. Preserve organization memberships, identities, sessions, invitations/roles/quotas and unrelated projects. |

Inventory soft references, including build intent/qualification/operations JSON, `project_creations`, import review snapshots and notifications with explicit predicates. Migration tests enumerate every FK touching these families against the graph. Unknown inbound relations are integrity blockers.

### Cycles and immutable guards

Change only these intended cycle edges to `ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED`, retaining full-scope columns and UPDATE restrictions:

* `mappings_head_fk`;
* `revision_ingestions_lease_fk`, `_eligible_fk`, `_committed_fk`;
* `revision_ingestion_attempts_reused_fk`;
* `revision_derivations_current_fk`, `_selected_fk`, `revision_derivation_attempts_repair_target_fk`;
* `private_object_retirements_current_fk`, `_completed_fk`, solely for atomic copy/detach;
* release rollback self-reference for a closed release component.

`regression_case_head` and `local_target_active_release` already use deferrable NO ACTION. Other parent/child FKs remain restrictive. Remove every strongly connected component in one bounded transaction, force its named constraints IMMEDIATE, then commit. No temporary NULL head, invented revision, trigger disabling, `session_replication_role` or per-job FK removal/recreation.

Affected append-only guards need explicit DELETE branches checking dedicated worker session, exact committed job scope, manifest entry, required copied evidence, phase and permanent fence. A caller-set boolean GUC cannot enable them. Public/request roles retain no DELETE/TRUNCATE grants; workers receive functions only. Preserve live INSERT/UPDATE and audit tamper guards. Exact schema admission pins changed guards/additive triggers before accepting them.

## 6. Physical deletion

Never fake orphan failure/rejection or broaden `ObjectCleanupPolicy` to admit successful catalog objects.

Add `CatalogObjectDeletionClaim` and `IPrivateObjectDeletionStore.DeleteAsync(claim, ct)` as a separate capability from `ObjectCleanupClaim`. It contains committed job, exact target/binding, immutable origin/publication snapshot, attempt/generation/instance/token and database lease. Adapters may share an internal exact-target fencing core after validating the appropriate capability. Orphan constructors, reasons and grace stay unchanged. Only `claim_deletion_objects` creates deletion authority; HTTP cannot deserialize it.

1. Claim bounded pages in short DB transactions with `FOR UPDATE SKIP LOCKED`; recheck fence, inventory completion, eligibility and arbitration. Wait for positive commit acknowledgement before storage I/O. Uncertain claim acknowledgement grants no physical authority.
2. Resolve the trusted adapter and validate kind, root/control identity and exact scope. Final deletion uses permanent gate/fence protocols. Tracked temporaries require original publication plus every present seal/incarnation/inode/ETag check. A live gate is Busy, not assumed absence after lease expiry.
3. Release storage gates/leases before fresh DB Finish. Match the full claim and live generation; record actual observations for that target only. Resolve lost Finish replies by reading that attempt, not repeating physical work merely for acknowledgement.
4. A new lease holder after worker death may repeat exact retirement; permanent fence plus actual absence yields Missing. Expired claims cannot finish newer attempts. Busy/Io use bounded backoff and capped attempts, reusing maintenance limits where applicable. UnsafeStorage/Unsupported remain blocked for operator resolution, never successful.
5. Retain empty directories, gates/seals, permanent fences and identity tombstones. Never sweep unregistered files. Live-blob replacement does not prove removal of uncommitted blocks, versions, snapshots, soft-deletes or backups; provider retention needs separate configuration/documentation. PostgreSQL deletion likewise does not immediately erase backups or old physical pages.

Status may truthfully show `catalogRemoved=true` and `objectsPending>0`. Zero claims, a directory listing or catalog DELETE cannot establish that every private copy is erased. Deployment documentation distinguishes active-store removal from provider/backup retention.

## 7. Individual deletion and retention scheduler

### Individual operations

* Preview: original submitter only, current authority and terminal state. Active work returns `409 activeWork` with separate cancellation. Atomically reserve original request/run identities before deletion of run/documents. Archive permits this erasure action without granting administrator ownership.
* Conversation: creator only with current organization/project admission, regardless of unexpired TTL. New `chat_delete_owned` checks exact kind/key and job/operation, retires identities and erases closed content atomically. Active turn returns `409 activeWork`; cancellation is explicit. Trusted parent deletion may erase dependent other-creator conversations after closure without revealing content. Never manipulate `retain_until` to exploit expiry erasure.
* Regression case: delete logical fixture and all versions. Any exact reference from runs, qualification/build intent, automation or other evidence yields `409 referencedResource` with counts/categories only. Item deletion cannot cascade other users' runs; confirmed parent deletion has its own broader closure.
* Environment: all versions of one identity. Reject references from approvals, qualifications, release targets, retained-artifact policy requirements or other known graphs, including inactive versions/disabled targets. Prefer self-contained retained environment facts rather than unnecessary live pins. Lock identity and all versions before the final anti-reference check. No deactivate-then-delete bypass.

### Scheduler

Use durable due-work scanning in the existing worker host with restricted `braid_lifecycle_worker` pool and explicit feature configuration, not one timer per API instance. No schema CREATE grant. If Wolverine provides wakeups, messages carry organization/job IDs only; committed DB due state is authoritative. Restart scans recover lost wakeups; duplicate delivery cannot duplicate operations. Use existing organization-managed deployment wiring, not new pipeline definitions/registration.

* Strong ETags protect policy reads/writes; the browser never auto-retries. Changes increment generation and append `retentionChanged` once with family/version/mode/safe scalar duration only.
* A policy applies prospectively to new data. It applies to existing data only when an administrator explicitly applies it, with separate confirmation of a fixed policy version and counted categories. Preserve canonical identities/deadlines; sidecars control earlier erasure. Saving settings cannot silently shorten every conversation.
* Calculate deadlines from verified intrinsic times or an explicit administrator policy application. Record actual regression/automation terminal timestamps; never substitute creation time for completion time.
* Select bounded candidates by `(due_at, kind, id)`, one organization per transaction. Claim current policy/version, recheck due time after locks and create/reuse an exact job/receipt. Extension before claim prevents it; extension after irreversible admission cannot restore data. Explain this in UI.
* Preview/chat become unreadable at effective expiry even if purge lags. Close active expired work safely before purge. Retain reference-held regression evidence until obligations end and report holds rather than silently retrying forever.
* Separate invocation response bytes, operational history and replay identity. Erase response bytes without extending maximum readability. Remove eligible history only after required audit copy and a no-content caller/target/request-ID tombstone. Repeated expired requests return expired/retired, never execute again. Keep private `request_digest` only if required by exact replay under an approved retention obligation, never as an audit field.
* TTL cannot erase journal, usage/coverage, unsettled reservations, permanent identities/fences or artifacts before their obligations. Content erasure never resets usage, coverage or quota capacity.
* Isolate per-item failures. Protected items, corrupt snapshots and storage errors belong to that item/job; independent due items continue. Shared DB/storage outage may pause affected work categorically. Failed erasure is never acknowledged as successful batch work.

## 8. Application and persistence interfaces

Place lifecycle types under `Braid.Application/Lifecycle`, retention under `Braid.Application/Retention`, and public DTOs under `Braid.Contracts`. Private claims remain in application/persistence/storage.

| Interface | Required operations |
| --- | --- |
| `IResourceLifecycleReader` | `ReadAsync(actor, key, ct)`; `ReadOperationAsync(actor, organizationId, operationId, ct)` |
| `IResourceLifecycleWriter` | `ChangeAsync(actor, change, ct)`; `PreviewDeleteAsync(actor, key, ct)`; `RequestDeleteAsync(actor, request, ct)` |
| `IDeletionJobReader` | `ReadAsync(actor, organizationId, jobId, ct)` |
| `IDeletionJobStore` | `ClaimAsync(organizationId, instanceId, ct)`; `AdvanceAsync(claim, ct)`; `ClaimObjectsAsync(claim, limit, ct)`; `FinishObjectAsync(claim, observed, ct)`; `ReadAttemptAsync(exactAttemptKey, ct)` |
| `IRetentionPolicyStore` | `ReadAsync(actor, organizationId, ct)`; `ReplaceAsync(actor, change, ct)` |
| `IRetentionSweepStore` | `EnqueueDueAsync(organizationId, cursor, limit, ct)` |
| `IPrivateObjectDeletionStore` | `DeleteAsync(CatalogObjectDeletionClaim, ct)` returning actual `PrivateObjectRetirementOutcome` |

Worker interfaces have no `ActorContext` or content-returning methods. `ResourceKey` is a checked discriminated full scope, not an isolated mapping UUID. `LifecycleChange` includes action, operation ID and expected effective version; confirmed deletion adds confirmation identity and immutable plan version. Closed results distinguish Archived, Restored, Accepted, Replayed, NotAuthorizedOrNotFound, PreconditionFailed, ActiveRelease, ActiveWork, ReferencedResource, ConfirmationExpired, Conflict, Blocked and Unavailable. Database failures cannot become not-found.

Adapters: `PostgresResourceLifecycleStore`, `PostgresDeletionJobStore`, `PostgresRetentionPolicyStore`, `PostgresRetentionSweepStore` and a storage claim materializer. Administrator operations use the restricted administration connection; owner/item operations use exact existing writer connections. None gains blanket DELETE. Workers use an independent connection, not maintenance/chat/administration pools, with protected `_FILE` configuration. Missing credentials refuse/disable activation without fallback to migrator or another pool.

Integration includes `PostgresRevisionIngestion*`, `PostgresRevisionDerivationStore*`, publication registries, `PostgresChatStore*`, `PostgresChatLifecycleStore`, preview/regression/build/release/invocation/automation stores, `PostgresCatalogReader*`, `PostgresCatalogSearch`, workspace orchestration and exact schema/role guards. Keep `AuditLogSchemaAdmission`, `AiUsageSchemaAdmission` and `AiQuotaSchemaAdmission` independent and exact; do not whitelist arbitrary changed schema.

## 9. HTTP and browser

Retain authentication, CSRF/origin, organization selection, bounded JSON and no-store behavior. Generate OpenAPI from endpoint definitions, not standalone edits to `contracts/braid-v1.json`.

Routes under `/api/v1/organizations/{organizationId}`:

* `GET /projects/{projectId}/lifecycle` and `/projects/{projectId}/mappings/{mappingId}/lifecycle`: state, effective state, safe capabilities, strong ETag.
* `POST .../lifecycle/actions/{operationId}`: archive/restore with exact `If-Match`.
* `GET .../deletion-preview`: non-destructive impact and confirmation.
* `DELETE /projects/{projectId}` or `/projects/{projectId}/mappings/{mappingId}`: exact `If-Match`, operation ID and confirmation ID in validated headers; empty body.
* `GET /lifecycle/operations/{operationId}` and `/deletion-jobs/{jobId}`: fresh authorized read-only recovery/status.
* DELETE on existing preview/conversation/case/environment resource paths: operation ID and exact precondition, retaining creator-scoped conversation authority.
* `GET/PUT /administration/retention`: complete four-family policy, strong policy ETag; separate explicit apply-existing confirmation when offered.
* Existing target actions gain `retire`, including exact current active pointer in the validated request.

Missing precondition is `428`, stale is `412`, authority follows indistinguishable existing `404`/refused conventions, active release/work/reference and identity conflicts are `409`, accepted jobs are `202`. Authorize before exposing blockers. Operation recovery remains reachable after original catalog parents disappear.

### Browser acceptance

* Default lists/search hide effectively archived/deleting/deleted entries; explicit archive view shows badges. Exact archived pages show read-only banner and restore only to exact-project administrators, without deriving missing reports.
* Disabled controls explain state but are not security. Keep unsent private drafts in memory for the same confirmed identity. Warn before navigation/account change, hide after unconfirmed authority refresh, clear on confirmed identity/organization change or sign-out. No private drafts, payloads, confirmations or pending bodies in localStorage, URL query or telemetry. Organization switching applies the coordinated leave/generation rules in its specification.
* Permanent-delete review identifies the exact resource and irreversible action, shows impact, retained evidence/identity/fences, backup limits and active-release blockers. Typed names are UX only. Stale previews require refresh/review.
* Show truthful progress/blocked cleanup. Unknown submission remains unconfirmed; resolve original operation ID before a new action. Never claim complete deletion on network failure.
* Retention shows units, explicit retain/expire, original deadlines/current policy and application scope. Unknown policy is “not configured”, not preselected destruction. Read errors cannot clear unrelated unsaved fields.

## 10. Required verification

1. **Schema:** populate committed/failed/unfinished ingestion, selected/older/repair reports, both chat kinds, builds/qualifications, disabled/active targets, automation and maintenance generations. Verify exact guards/functions/grants/RLS, rejection of unknown tables, restricted owner/runtime privileges and no payloads in audit/receipts.
2. **Archive/restore:** every authoring/deployment port refuses after fencing; authorized exact reads remain. Existing enabled endpoints serve under original target/release/current grants with invocation/audit writes; disabled/revoked endpoints refuse. Restore preserves mapping archives; every surface agrees; parent archive changes effective mapping ETag; old epochs never revive. Deleted-ingestion create/append keys cannot allocate work in a surviving project. Create terminal chat/invocation rows during quiescence and prove final copy/purge includes them without weakening manifest guards.
3. **Concurrency:** real separate DB connections with deterministic barriers, not sleeps. Both winner orders for archive against native commit, report finish, preview/run completion, chat start/round reserve, build stage/commit, automation enqueue, target registration/activation and administrator revocation. Verify post-wait ACL/time, no partial audit/receipt or post-fence publication, and no unsafe deadlock retry. Include restricted direct SQL.
4. **Active releases:** disabled target with pointer, historical non-current active release, shared environments, inconsistent ancestry, activation/delete and retire/activation races, stale target ETags. Delete never auto-disables/clears; retire preserves history and increments actual target version.
5. **Graph:** real SQL for every cycle, adjacent batched components, head/reused/repair pointers, maintenance current/completed pointers, build/regression proof, future mappings, focus FKs, soft references and sibling preservation. Force named constraints IMMEDIATE and assert anti-join/residual completeness without FK/trigger suspension.
6. **Privacy/retention:** byte-exact artifact/source-audit/maintenance copies; unchanged usage counts, failures, coverage and unsettled reservations. Retained identifiers remain accessible only under fresh audit/usage authority. Deleted-project administrators lose project-derived access without promotion. Other creators' chat never leaks into API, receipts or logs.
7. **Idempotency:** erased preview/case/environment/project/create/invocation identities and original ingestion client namespaces never become fresh work. Chat request/round/turn IDs stay retired. Original operation reads resolve unknown commits; changed payload/version conflicts. Expired machine requests never re-execute.
8. **Physical cleanup:** real qualified filesystem-v2/Blob contracts, not fabricated Deleted results. Process death around claim commit, durable fence, unlink/replacement, Finish and lost reply; stale generations/leases; live readers/writers; kind/root mismatches; bad seals/ETags/inodes; unregistered files and unsupported roots. Verify permanent fences and Busy/unsafe categories. Windows mocks cannot qualify Linux storage.
9. **Retention:** unit policy clocks and real post-wait DB deadlines; exact boundaries, negative/overflow duration, unconfigured families held, policy races, prospective/explicit-existing application, missing terminal time, reference holds, active expiry, scheduler restart/duplicates and per-item failure isolation. Quota windows/uncertain reservations cannot disappear into free capacity.
10. **HTTP/browser:** generated routes/outcomes/headers, strict duplicate/unknown JSON rejection, no-store/CSRF/bearer matrix, wrong organization/owner/submitter, mid-request revocation, expired confirmation, stale parent ETag, decimal-string large counts and fresh authority on every page/status. Exercise unsent/unconfirmed work, identity refresh/change and no automatic resend.

## 11. Workspace history privacy

Structural deletion includes revision-bound and explicitly focused conversations without inspecting text. Unfocused/other-focus organization conversations remain independent and may contain quotes/citations; their own retention applies and deletion confirmation must disclose this. Broader erasure needs an immutable disclosure-dependency design and a policy for untracked quotations. Never scan private text, erase all organization chat or promise complete quotation erasure from citation rows alone.
