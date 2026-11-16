# P3d: persistent catalog and revision HTTP API

This is the historical API delivery contract, not the current backlog or host
setup. The [contract-owner map](catalog-persistence-plan.md#current-contract-owners)
routes each retained requirement to its focused reference. For current API work,
use [catalog reads and metadata](catalog-reads.md), [uploads and recovery](catalog-revision-uploads.md)
and [project creation HTTP](project-creation-http.md). [Session host](session-host.md)
owns hosted identity configuration; the Development boundary here is not that setup.
The detailed original contract and anchors remain intact below. Its future-tense
sequence and checkpoint results do not describe current feature availability.

## Historical delivery checkpoint

Status: accepted delivery contract; implementation and qualification follow in
five focused increments. Native ingestion is qualified and merged in
[PR #27](https://github.com/DeandreT/weft/pull/27). Parent
[#16](https://github.com/DeandreT/weft/issues/16) completes only after all five
increments are qualified and merged. P3e report/cache work then follows.

P3d1 and P3d2 are qualified and merged in
[PR #33](https://github.com/DeandreT/weft/pull/33) and
[PR #38](https://github.com/DeandreT/weft/pull/38). P3d3's raw upload and
original-actor recovery are qualified and merged in
[PR #40](https://github.com/DeandreT/weft/pull/40). Project creation follows in
the separate [persistence prerequisite](project-creation.md) and HTTP increment.

| Increment | Cohesive scope | Dependency |
| --- | --- | --- |
| [P3d1 #28](https://github.com/DeandreT/weft/issues/28) | Catalog read models/queries, narrow reader grants, generated HTTP contracts, explicit Development identity and request boundary | Qualified/merged P3c |
| [P3d2 #29](https://github.com/DeandreT/weft/issues/29) | Conditional mapping metadata maintenance, dedicated writer and metadata ETag | Qualified/merged P3d1 |
| [P3d3 #30](https://github.com/DeandreT/weft/issues/30) | Exact raw native upload, original-actor receipt and explicit resume HTTP routes | Qualified/merged P3d2 and C2 service |
| [P3d4a #39](https://github.com/DeandreT/weft/issues/39) | Explicit organization creation capability, restricted creator persistence, atomic first Administrator membership and durable idempotency | Qualified/merged P3d3 |
| [P3d4b #31](https://github.com/DeandreT/weft/issues/31) | Project creation HTTP routes, fresh organization actions and host composition | Qualified/merged P3d4a |

This sequence preserves the complete accepted API boundary. Project creation is
included so authorized users can create projects without manual database inserts.
Each row remains buildable and reviewable independently. None introduces Angular
catalog components, native editing/download, reports, authentication providers,
chat, preview, maintenance or hosting.

## Routes and wire contract

Define `O=/api/v1/organizations/{organizationId}`, `P=O/projects/{projectId}` and
`M=P/mappings/{mappingId}`. IDs are nonzero canonical UUID strings; every operation
retains full ancestry and checks the server's allowed organization.

| Route | Increment / behavior |
| --- | --- |
| GET `/api/v1/catalog/context` | d1: explicit identity mode, current actor/allowed organization, fresh admission, supported actions and request-token issuance |
| GET `O/projects` | d1: bounded visible project page |
| GET `P` | d1: visible project metadata and fresh action availability |
| GET `P/mappings` | d1: bounded mapping page, metadata, backend and current committed head |
| GET `M` | d1: current mapping metadata/head, independent of worker availability |
| GET `M/metadata` | d1: exact metadata representation; strong ETag added with d2 |
| GET `M/revisions` | d1: immutable history in descending committed sequence |
| GET `M/revisions/{revisionId}` | d1: exact scoped immutable revision metadata |
| PATCH `M/metadata` | d2: versioned name/description edit document with concrete If-Match |
| POST `P/mappings` | d3: raw native body creates mapping plus first admitted revision |
| POST `M/revisions` | d3: raw native body append, AnyHead or EqualHead |
| GET `P/revision-ingestions/{operationId}` | d3: original actor's safe durable receipt |
| POST `P/revision-ingestions/{operationId}/resume` | d3: one explicit source-free recovery claim |
| POST `O/projects` | d4: authorized atomic project/first-admin creation |

Catalog records are explicit public DTOs, separate from internal ingestion facts.
Versions and sequences use positive invariant decimal strings because PostgreSQL
bigints can exceed JavaScript's exact integer range. Bounded byte/claim counts may
use JSON integers. Dates are UTC. No native bytes, private references/paths,
lease/profile/image data, raw evidence or provider/database exception text enter
catalog replies. Validation reports remain P3e.

Catalog JSON uses `Cache-Control: no-store`. API routing never falls back to the
Angular document. Committed decoded-invalid revisions remain readable history;
validity does not confer execution/deployment capability. Runtime feature disable
must not omit build-time OpenAPI routes: handlers return a closed unavailable
response without creating a default actor or fixture repository.

## Read authority, grants and pagination

Project/mapping/revision reads and visible project-list rows join active principal,
active organization admission and exact active project membership. Context and
organization-level creation admission require an active principal and allowed
organization admission without requiring a project that does not yet exist;
creation additionally requires the explicit capability in d4. Organization
admission or administration of another project grants no project visibility.
Denied/missing/foreign project resources share 404 behavior. Source identity
failures remain distinct from persistence errors; failed queries never become
fabricated empty catalogs.

d1 uses a dedicated `weft_catalog_reader` login/read-only pool. A focused new
migration grants schema USAGE and SELECT on the five ownership tables plus
`mappings` and `mapping_revisions`. The reservation join receives only
`organization_id`, `project_id`, `operation_id`, `display_filename` column SELECT.
It grants no attempts/evidence/lease access, writes, grant options, ownership or
DDL. Existing runtime/ingest profiles and migrations 0001/0002 remain unchanged.
The existing organization policies apply to all roles and need no widening.
The new pool verifies exact effective/inherited/PUBLIC/column rights, trusted
definitions and scope reset; no raw connection or caller write flag is exposed.

Typed project queries retain existing `IProjectReader` semantics; mapping/history
queries use a narrow catalog port. Any reuse must preserve each pool's explicit
role/profile boundary. Statements scope every join by all applicable ancestors,
finish transactions before returning and never call storage/worker services.
Immutable revision admission is already stored in `mapping_revisions`; d1 need
not read native validation evidence to describe it.

Pages default to 50, accept 1..100 and fetch limit+1, without OFFSET/unbounded totals.
Projects/mappings order by UUID ascending; revisions order by sequence descending.
Opaque v1 base64url JSON cursors are bounded to 512 decoded bytes, identify kind,
complete applicable scope, fixed order and last UUID/decimal sequence. Strict
shape/version/scope mismatch is 400. Cursors grant no authority; every page reads
fresh memberships. Inserts behind a UUID cursor may be absent from that continuation;
new revisions above a sequence cursor do not duplicate older history. No snapshot
transaction or cross-request lock is retained.

Action availability is advisory and includes supported-feature information rather
than presenting a future route as usable. Submitted actions always check fresh
authority. Metadata reads do not require worker health or backend registration.

## Explicit local identity and request protection

Catalog enablement and Development identity enablement are separate explicit
options. Principal/organization IDs and the exact browser origin are required;
no default account, role, tenant, startup seed or caller identity header exists.
Enabled catalog without an admitted identity provider fails startup. The initial
provider runs only in Development with the existing concrete loopback listener;
hosted/non-Development use rejects that provider. Disabled catalog stays closed.

Each catalog request requires the actual loopback peer and exact configured Host.
Forwarded/proxy headers cannot become local identity. The initial browser profile
is one canonical IP-literal loopback origin with an exact effective port, serving
SPA/API together. Browser serialization omits default HTTP port 80;
cross-origin CORS remains disabled. Binding Kestrel to loopback alone does not
validate Host, so this policy checks Host explicitly.
[Microsoft host filtering guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0)

All unsafe catalog methods require one canonical matching Origin plus framework
cookie/request-token validation before body consumption or mutation. Context GET
issues the cookie-bound request token; clients send `X-XSRF-TOKEN`. The local
cookie is HttpOnly, SameSite Strict and path scoped. No token is logged. Reject
missing/null/multiple/malformed/foreign origins and missing/invalid tokens.
Raw/JSON endpoint protection is explicit rather than relying on form-binding
defaults. d1 qualifies identity, issuance and boundary behavior; d2-d4 additionally
qualify those protections on each real mutation.
[Microsoft antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)

This is a Development boundary, not hosted sign-in qualification. P4 owns verified
Entra sessions, hosted cookie/key lifecycle and provider-independent identity.
Local provisioning deliberately creates the configured principal/admission/grants
through an owner-controlled tool, never web startup.

## Metadata maintenance

d2 uses a dedicated restricted writer with fresh Publisher/Administrator authority.
A new migration allows exactly two disjoint mapping transitions: head advance
preserves metadata/version; metadata change preserves head and increments metadata
version by exactly one. Identity/backend/creator/owner/created-at and immutable
revision facts remain fixed. Compile the new exact trigger definition into guards;
do not alter old migration bytes or weaken other transition checks.

Grant UPDATE only on name/description/version/updated-at plus required reads. No
head, revision, membership, INSERT/DELETE, ownership or DDL rights are added. One
fresh authorized conditional operation compares version and updates once; checked
no-op replacement preserves version/time. Name/description retain C2's strict
Unicode-scalar/UTF-8 bounds. Metadata/head races must preserve both operations.

`M/metadata` contains only scope, name, description and metadata version; it excludes
head-driven timestamps and actor-varying actions. Its quoted strong ETag is
`metadata-v1:<org>:<project>:<mapping>:<decimalVersion>`. PATCH requires one concrete
If-Match: absent 428, malformed/list/wildcard 400, weak/nonmatching 412 after fresh
resource authority, denied/missing 404. Success returns 200 and the current ETag.
An empty header is malformed; a quoted empty opaque tag (`""`) is syntactically
valid and nonmatching, so it returns 412.
Ordinary GET mapping has no metadata-only strong ETag. EqualHead remains an exact
revision condition rather than a metadata version.
[HTTP If-Match semantics](https://httpwg.org/specs/rfc9110.html#field.if-match)

The edit document uses `application/vnd.weft.mapping-metadata-edit.v1+json` and
requires exactly two unique string properties, `name` and `description`. The
server trims the name and preserves the description; the resulting metadata
representation receives its current strong ETag. This accepted PATCH refinement
is recorded in [#29](https://github.com/DeandreT/weft/issues/29#issuecomment-6151039087).
Edits are independently default-off through `WEFT_METADATA_EDITS_ENABLED`; enabled
edits require the dedicated `WEFT_POSTGRES_METADATA_CONNECTION_STRING`. Read-only
configurations need no writer credentials or pool. A coordinated stop/migrate/start
upgrade provisions the editor role and installs migration 0004; old strict binaries
are not admitted against a changed trigger definition.

Metadata GET first checks current read authority, then evaluates bounded If-Match
and If-None-Match conditions. A strong If-Match failure returns 412; a weak matching
If-None-Match returns 304 without a body. Edit request bodies are manually read
under a 65,536-byte limit only after the request boundary, scope, current edit
authority, content policy and preconditions pass. No metadata transaction spans
body reading. Errors expose neither current metadata nor its ETag.

## Raw uploads and recovery HTTP

d3 accepts only `application/octet-stream`, without multipart/base64 envelopes,
request decompression or native JSON binding. Create metadata uses bounded query
name/description/backendId/originalFilename; append uses filename and
`head=any|equal` with expectedHeadRevisionId only for equal. Reject duplicate,
unknown or contradictory query fields. Freeze a 64 KiB request-line ceiling to
retain the full maximum Unicode description after percent encoding; suppress
query/native-body logging. Idempotency-Key is one nonzero UUID header.

The C2 service remains authoritative for exact bounded bytes, admission, profiles,
fencing and outcomes. Content-Length can reject above the fixed 4 MiB ceiling;
current reductions cannot preempt existing-key complete-body comparison. Chunked
bodies remain bounded without draining. Native upload concurrency is server
bounded; request input cannot relax limits. Caller disconnect propagates through
C2's bounded ordinary cancellation bookkeeping.

| Condition | HTTP behavior |
| --- | --- |
| Committed valid/decoded-invalid, including replay | 200 safe operation result and immutable revision link |
| InProgress | 202 own receipt/Location; polling never claims or executes |
| NeedsSource / idempotency or head conflict / exhausted / lost claim | 409 distinct categorical result |
| Empty native or malformed request | 400 |
| Native byte limit | 413 |
| Unregistered/decode/unsupported/native or evidence policy rejection | 422 with own durable receipt when one exists |
| Actual retryable storage/worker failure | 503 safe receipt/category, no retry loop |
| Missing/denied/other original actor | 404 |
| Persistence/schema/internal failure | 500 closed category; do not parse exception messages |
| Caller cancellation | Abort response; no fabricated HTTP/validator outcome |

Committed requests consistently return 200 because C2 has no new-versus-replay
flag; no racy pre-read invents 201. Receipts omit private facts. Explicit resume
is one synchronous claim, not a background promise. Metadata reads/edits remain
available when the worker is missing. No transaction spans request/store/worker I/O.

## Project creation

d4 adds an explicit `can_create_projects` organization membership capability,
default false and granted by owner-controlled provisioning. Ordinary admission
and client-supplied roles/creator IDs confer no creation or other-project access.
A dedicated narrow creator persists immutable original-actor/key/accepted-metadata
creation facts and atomically creates one new project and its actor's first
Administrator membership. Full ancestry, exact counts and same-key replay/conflict
are required. First creation returns 201; matching replay 200; conflict 409.
Capability denial on an admitted organization is 403 without exposing a target.
Project/member administration and metadata edits remain separate policies.

## Qualification and scope

Each increment records implementation and local/independent exact-source proof
separately. Current remote CI results are additional evidence; do not assume the
earlier billing block persists or change billing. Required gates include actual
PostgreSQL and API transport, narrow grants/guards, every ancestor/role/revocation,
pool cleanup/cancellation, fresh/upgrade/restart, pagination/precision/concurrency,
Host/Origin/token/identity failures and generated contract/narrowing/404 checks.
d3 additionally composes the real private store and qualified native worker with
exact bytes, bounded streaming, conflicts and restart recovery. All existing
ingestion/native/viewer invariants remain required. UI checks use an alternate
virtual display. A planning PR does not qualify the API features.
