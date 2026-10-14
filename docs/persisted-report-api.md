# Persisted revision-report API

Issue [#51](https://github.com/DeandreT/weft/issues/51) connects the qualified
[revision-report workflow](revision-report-workflow.md) to the catalog API.
Implementation is in progress; HTTP, published-host and upgrade qualification
must pass before this increment is complete.

## Requests and authority

Every report belongs to an immutable revision. Let `R` be
`/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}`.

| Request | Meaning |
| --- | --- |
| `GET R/reports/projection` | Read the server-selected current projection state. |
| `GET R/reports/validation` | Read current validation; optionally select `projectionDerivationId` for verified links. |
| `GET R/reports/projection/{derivationId}` | Read that exact retained successful projection. |
| `GET R/reports/validation/{derivationId}` | Read that exact retained validation; optionally select a matching projection. |
| `POST R/reports/projection/ensure` | Explicitly ensure the current projection. |
| `POST R/reports/validation/ensure` | Explicitly ensure current validation. |

Ensure accepts an optional `repairDerivationId`. Repair requires that exact
currently selected success and proven object absence or integrity failure. Reads
never derive, renew or automatically repair. Current selection refers to the
server's complete key for this revision, independently of the mapping head.

Requests use the existing catalog identity, Host, request-token and unsafe Origin
boundary. All four project roles retain their `ReadMetadata` eligibility. IDs are
canonical, nonzero lowercase UUIDs. Only the operation's declared selector is
accepted; duplicate, malformed and unknown queries are refused. These routes
require known-empty bodies without Content-Type or ambiguous/chunked framing.
No request supplies an actor, backend profile, policy, digest or private object
reference.

Scope admission precedes deferred report dependencies. Present authority and
immutable source facts are checked again after response preparation, immediately
before delivery. Actual caller cancellation remains observable.

## Representation and responses

Typed projection and validation wrappers distinguish `notDerived`, `pending`,
`available` and `failed` lifecycle from the backend operation status. Nullable
fields are explicit. Generations cross as exact decimal strings. Successful
metadata identifies the immutable derivation and its actual start/finish times;
pending or failed attempts do not become retained successful-report links.

The nested Weft report preserves exact values, typed paths, native evidence,
coverage gaps and validation findings. False validation is a usable report.
Validation links require an explicitly selected, independently verified matching
projection; stored validation remains unlinked. Private storage references,
lease/instance tokens, complete cache keys and credentials are excluded.

Authorized observations return 200. Explicit Ensure returns 202 for a live claim
or retry delay, with a current-read Location. Local capacity returns 429;
claim/selection/generation conflicts return distinct 409 errors. Disabled, foreign,
missing or revoked scopes and unavailable selected projection links remain opaque
404s. Report dependency/configuration failures return safe 503 errors. A selected
object read refusal can retain available metadata with a null payload.

Every complete wrapper, including metadata and linked findings, is prepared under
intersected immutable and current byte/value/container-depth limits before any
response bytes. Resource overflow retains honest metadata and a null payload;
findings and fields are never clipped. If even that wrapper cannot fit, a fixed
categorical 503 resource-limit error uses a separate 1,024-byte error budget.
Responses use `Cache-Control: no-store` and no metadata ETag.

## Host composition and qualification

Report reads and explicit derivation have independent default-off flags:
`WEFT_REVISION_REPORTS_ENABLED` and `WEFT_REVISION_REPORT_DERIVATION_ENABLED`.
Derivation requires reads and catalog access. Dedicated report-reader and deriver
credentials are separate. One singleton service shares its work/read capacity
through both interfaces; writer and worker resolution remain lazy, including
cached Ensure and historical retained reads.

The host opens an explicitly provisioned existing private root. Its physical
native/derived ceilings remain 4 MiB/16 MiB. A lazily parsed report-local
`WEFT_DERIVED_READ_BYTE_LIMIT` can reduce report reads without changing ingestion.
Pure stored-format validation requires no live worker. Missing report dependencies
remain local to admitted report requests so catalog metadata can remain usable.

Qualification covers actual scoped HTTP and native derivation, access revocation,
capacity, cancellation, repair, complete-response boundaries and generated
contracts. Published verification separately covers fresh setup, a genuine 0005
upgrade with retained native facts, fresh API/pool tokens and database restart.
Angular catalog screens, Entra sign-in, chat, preview and cloud deployment follow
their own delivery increments.
