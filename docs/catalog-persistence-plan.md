# P3: persistent mapping catalog and ownership

This is the retained P3 design and delivery record, not a current setup guide or
backlog. [ROADMAP](../ROADMAP.md) owns future priorities. Use the focused owners
below for current contracts and their stated activation and verification limits.
The original requirements, issue links and heading anchors remain below.

## Current contract owners

| Retained requirement | Current owner | Original detail |
| --- | --- | --- |
| Server-issued actor, explicit project roles, full ancestry, organization RLS, fresh authority and scoped pools | [Project ownership](project-ownership.md#ownership-and-application-reads) | [Ownership and immutable history](#ownership-and-immutable-history), [smallest P3a](#smallest-p3a) |
| Applied checksums, separate migration/runtime roles and explicit provisioning | [Project ownership setup](project-ownership.md#migrations-and-local-setup) | [Accepted direction](#accepted-direction), [P3a scope](#smallest-p3a) |
| Exact private bytes, generated keys, create-only writes and verified bounded reads | [Private storage](private-storage.md), [current store activation](private-storage-activation.md) | [Immutable history](#ownership-and-immutable-history), [P3b increment](#ordered-increments) |
| Immutable mappings/revisions/reservations/attempts, exact restricted grants, evidence and one-statement publication | [Ingestion metadata](ingestion-metadata.md#authority-and-immutable-publication); [service contract details](native-revision-ingestion.md#persisted-admission-contract) | [Four-table specification](catalog-ingestion-plan.md#four-tables-and-immutable-ancestry), [role specification](catalog-ingestion-plan.md#exact-roles-and-transaction-boundaries) |
| Native admission, server limits/profile, nullable validity, idempotency, head ordering, leases, fencing and original-actor recovery | [Native revision ingestion](native-revision-ingestion.md) | [Admission defaults](catalog-ingestion-plan.md#application-boundary-and-admission-defaults), [publication/recovery specification](catalog-ingestion-plan.md#idempotency-claims-and-one-statement-publication) |
| Read DTOs, precision, cursors, privacy, request identity/protection and conditional metadata edits | [Catalog reads](catalog-reads.md); [Session host](session-host.md) for hosted activation | [API routes](catalog-api-plan.md#routes-and-wire-contract), [read authority](catalog-api-plan.md#read-authority-grants-and-pagination), [local identity](catalog-api-plan.md#explicit-local-identity-and-request-protection), [metadata](catalog-api-plan.md#metadata-maintenance) |
| Raw-byte HTTP upload, status categories, original receipt and explicit resume | [Revision uploads](catalog-revision-uploads.md) | [Upload HTTP specification](catalog-api-plan.md#raw-uploads-and-recovery-http) |
| Separate organization creation capability, atomic first Administrator, immutable intent and same-key replay | [Project creation](project-creation.md), [HTTP boundary](project-creation-http.md) | [Creation specification](catalog-api-plan.md#project-creation) |
| Versioned report/cache identity, retryable attempts, validity versus coverage and provenance-safe viewer links | [Report metadata](revision-report-metadata.md), [derivation](revision-report-workflow.md), [native reports](ferrule-revision-reports.md#observations-and-links) | [Viewer/report boundary](#reusing-the-viewerreport-boundary) |
| Orphan eligibility, grace, protected originals/results and interrupted cleanup | [Maintenance metadata](private-object-maintenance-metadata.md), [maintenance command](maintenance-command.md) | [P3f increment](#ordered-increments), [later decisions](#decisions-at-the-relevant-increment) |
| Browser catalog, immutable revision navigation and report workflows | [Catalog browser](catalog-browser.md), [saved reports](saved-reports.md) | [P3g increment](#ordered-increments) |
| Separate synthetic, database/storage/native, browser and hosted qualification | Each focused owner's verification section; [v1 integration record](v1-integration.md) for historical combined evidence | [Delivery evidence](#delivery-evidence), [ingestion gates](catalog-ingestion-plan.md#qualification-gates-and-exclusions), [API gates](catalog-api-plan.md#qualification-and-scope) |

This map assigns contracts, not blanket completion. The detailed table/column and
qualification requirements in the original plans are retained design evidence;
installed SQL and exact schema admission remain authoritative for an installation.
Do not apply a plan's hypothetical schema or treat a merged/local checkpoint as
provider or cloud acceptance. The early four-increment API shorthand below was
refined into the five delivery rows retained in the [API record](catalog-api-plan.md).

## Historical delivery checkpoint

Status: accepted implementation sequence. P2c is qualified and merged in
[PR #12](https://github.com/DeandreT/weft/pull/12). P3a is qualified and merged in
[PR #21](https://github.com/DeandreT/weft/pull/21). P3b storage is qualified and
merged in [PR #22](https://github.com/DeandreT/weft/pull/22). P3c follows the
two-part [ingestion plan](catalog-ingestion-plan.md). P3c1 metadata is qualified and
merged in [PR #26](https://github.com/DeandreT/weft/pull/26). P3c2's storage/native
workflow is qualified and merged in [PR #27](https://github.com/DeandreT/weft/pull/27).
P3d follows the four focused increments in the [catalog API plan](catalog-api-plan.md).
Each increment has its
own focused issue, feature branch and PR, and must be qualified and merged
before dependent implementation starts.


## Accepted direction

I will keep Angular and the C#/.NET 10 modular application. PostgreSQL stores
ownership, catalog facts and history; private object storage preserves exact
native artifacts and bounded derived objects. Ferrule remains behind the
qualified inspection/validation/projection adapters.

For P3a I will use Npgsql 10.0.3 with parameterized SQL and explicit scoped
transactions, matching the relevant local Wareboxes conventions. Its official
package includes a net10.0 target. I will qualify PostgreSQL 16.15, using a
verified immutable container image. This is a local baseline, not an Azure
qualification claim.

Primary references:
[Npgsql package](https://www.nuget.org/packages/Npgsql/10.0.3),
[Npgsql usage](https://www.npgsql.org/doc/basic-usage.html),
[PostgreSQL supported versions](https://www.postgresql.org/support/versioning/).

## Ordered increments

| ID | Scope | Dependencies | Qualification boundary |
| --- | --- | --- | --- |
| [P3a](https://github.com/DeandreT/weft/issues/13) | Project ownership schema, application access/read contracts, scoped Npgsql transactions, checked SQL migrations and disposable PostgreSQL setup | Qualified and merged [P2c](https://github.com/DeandreT/weft/issues/9) / [PR](https://github.com/DeandreT/weft/pull/12) | Actual PostgreSQL scope, grant, role, migration, pool-reset and restart checks |
| [P3b](https://github.com/DeandreT/weft/issues/14) | Private storage contract and real local adapter; generated keys, create-only writes, bounded verified reads | Qualified and merged P3a | Exact-byte, isolation, overwrite, failure and restart checks against actual local storage |
| [P3c1](https://github.com/DeandreT/weft/issues/23) | Scoped mapping/revision/reservation metadata, restricted ingestion role/pool, typed DAL and atomic publication | Qualified and merged P3b | Actual PostgreSQL grants, migrations, ancestry, immutable facts, receipts, leases and head concurrency; explicitly synthetic admission evidence |
| [P3c2](https://github.com/DeandreT/weft/issues/24) | Exact native upload/admission and recovery using the scoped metadata foundation | Qualified and merged P3c1; qualified P2 native adapters | Actual PostgreSQL/storage/worker checks including retries, concurrent uploads and process failure between stages |
| [P3d](https://github.com/DeandreT/weft/issues/16) | Scoped catalog/revision HTTP API, raw-byte upload, generated contracts and explicit loopback-only Development identity wiring | Qualified and merged both P3c increments | Actual API/database/storage checks; no trusted client identity; hosted configuration rejects development identity |
| [P3e](https://github.com/DeandreT/weft/issues/17) | Persisted projection/validation attempts and cache, revision-specific reports and provenance-safe viewer links | Qualified and merged P3d; qualified P2c public report contracts | Actual native worker, cache version/scope, concurrency, report budget and restart checks |
| [P3f](https://github.com/DeandreT/weft/issues/18) | Bounded orphan and replaceable-derived-object cleanup; retention policy, leases and interrupted-deletion recovery | Qualified and merged P3e | Actual PostgreSQL/storage cleanup races; referenced revisions and active reservations remain intact |
| [P3g](https://github.com/DeandreT/weft/issues/19) | Angular catalog, upload and revision history integrated with immutable viewer routes | Qualified and merged P3f and P2c | Browser workflows on an alternate display against actual API/PostgreSQL/storage and qualified native worker |

P4 starts after the P3 chain. It supplies verified Entra identities and browser
sessions to the existing ownership boundary. P3 does not qualify hosted login.
Parent [P3c #15](https://github.com/DeandreT/weft/issues/15) is complete only
after both focused ingestion increments are qualified and merged.

## Smallest P3a

I will implement ownership only: organizations, internal principals,
organization admission, projects and explicit project memberships. Organization
admission grants no automatic access to projects. Project membership has the
planned Viewer/Runner/Publisher/Administrator roles.

P3a owns small domain value objects, new application project access/read
contracts, new PostgreSQL persistence, migration scripts, database test
infrastructure, solution/lockfile updates and verification documentation.
It excludes mappings, revisions, storage, HTTP endpoints, authentication,
Angular changes and Ferrule changes.

Organization-owned tables receive an organization RLS guard. Application reads
also check fresh active principal/organization/project memberships. Runtime and
migration roles/pools remain separate; runtime must not own the schema, bypass
RLS, or perform DDL. Organization context is transaction-local and cannot be
rebound or carried through pooled connection reuse.

## Ownership and immutable history

I will carry full organization/project/mapping/revision scope into persisted
references and queries. Composite foreign keys preserve ancestry. Digests and
object keys do not grant access.

Actor context is issued by the server boundary and carries internal identity,
not cached role grants. P3a adds no HTTP identity fallback. P3d's development
adapter must be explicit, Development-only and loopback-only; P4 replaces that
adapter for hosted use.

A committed revision's scope, native digest/byte count/private reference,
logical entry document, selected backend, creator and sequence are immutable.
Inspection, validation and projection attempts have their own versioned
provenance and statuses. Derivation completion cannot rewrite a revision or
move its mapping head.

Uploads remain byte streams under server limits; no browser or C# JSON
round-trip changes native bytes. Private keys are generated by braid. Metadata
transactions stay short and do not await object writes or worker execution.
Durable reservations and guarded publication reconcile PostgreSQL and storage.

## Reusing the viewer/report boundary

I will reuse P2c's provider-independent projection/validation reports and
operation status/provenance. Fixed development examples and their synthetic
scopes are not catalog records.

Validation false is a usable findings report; null means no validator result.
Coverage and execution capability remain separate. Native owners/locators are
retained; a viewer link is computed only against a matched confirmed projection
with exactly one emitted origin. Cached validation links cannot be assumed
stable after a projection upgrade.

Cache identity must include complete scope/digest/backend revision and build,
adapter/schema, and an explicit algorithm/policy identity or the existing
enforced adapter-build configuration invariant. Operational failures stay
retryable attempt evidence and never become apparently successful empty graphs.

## Decisions at the relevant increment

I have accepted P3a's ownership-only scope, Npgsql/PostgreSQL baseline and
organization RLS plus application project checks. I will resolve implementation
details such as the verified image digest, schema/role names and migration
entry point in that issue.

P3b selects the exact local private-storage implementation. P3c settles upload
admission/retention and idempotency/head rules. P3d settles HTTP project creation
authority, routes and development mutation protections. P3e settles cache
algorithm/policy versions and retry behavior. P3f settles expiry/grace periods
and recovery scheduling. These later choices are not represented as already
implemented or qualified.

## Delivery evidence

For every increment I will record separate implementation and qualification
state and link its exact PR/head and local plus independent clean-container
evidence. GitHub Actions was initially blocked by account billing; the user chose
local and clean-container verification. Remote CI subsequently passed for
[PR #27](https://github.com/DeandreT/weft/pull/27). I will check and report each
increment's actual remote result without changing billing or replacing the
independent evidence.

I will use actual disposable PostgreSQL/private storage for persistence and
recovery gates. Native integration uses the qualified restricted worker.
Browser tests use an alternate virtual display. Fixtures stay self-authored.
Entra, model-provider, preview/Wolverine, Azure, deployment, comparison and
regression work remain their later roadmap increments.
