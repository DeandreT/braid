# Audit log

The audit view is read-only and contains event metadata, never payload bytes,
conversation text, tokens, credential hashes, arbitrary before/after JSON or
private object digests. Organization administrators can read their organization;
project administrators see only events from projects they currently administer.
Organization-level events remain hidden from project-only administrators.

## Retained events

The journal retains administration, release, machine grant/invocation, mutating
bearer-request admission, chat payload disclosure (including specifications), build and approval events.
Build staging and publication are separate events: staged bytes are not a
published artifact.
Capture is part of the source write's database transaction. Idempotent replays do
not create a second event. The journal contains resource identifiers independently
of the source rows so retiring a resource does not erase its audit identity.
Archive and delete operations from the resource lifecycle subsystem appear under
`administration`, in the affected project's scope.

Migration 0055 backfills the existing records without changing them. Known event
timestamps are preserved. Earlier approval revocations and build states without
retained transition timestamps, including prior artifact publication, are clearly marked **historical observations** at
migration time, with unknown/system actors. An observation is not a claim about
when the original transition occurred or who performed it. A request-admission
event likewise means admitted, not successfully completed.

The existing restricted administration connection serves the audit read through
one owner-defined function, in a read-only transaction. No runtime login receives
direct journal access. The journal, functions and additive capture triggers are
checked before existing schema fingerprints project those known triggers away;
a missing, disabled or altered installation is refused.

Complete object removal also fails admission. An older migration prefix is accepted
only when readable migration history establishes it; restricted runtime logins
without that access require the complete current schema. Upgrade fixtures verify
the historical prefix as its owner and seed it through the original restricted SQL
ports, then use current stores after migration.

## API and clients

`GET /api/v1/organizations/{organizationId}/audit` accepts `projectId`, `actorId`,
`kind`, `since`, `until`, `limit` and `cursor`. Kinds are `administration`, `release`,
`build`, `approval`, `request`, `payload`, `grant` and `invocation`. UTC `since` is
inclusive and `until` exclusive. The default window is the last 30 days; each page
contains at most 100 events. Results are newest first, with random UUID event
identities and opaque cursors tied to the filters and returned time window.

`GET .../audit/export` returns the same page as CSV. Its `X-Braid-Audit-Since`,
`X-Braid-Audit-Until` and optional `X-Braid-Audit-Next-Cursor` headers allow bounded
continuation. Every page rechecks authority. A session change or revoked role
does not inherit a previous page's admission. All responses are `no-store`.

The app's `/audit` view offers filtering, paging and a filtered CSV export bounded
to 10,000 rows and 8 MiB of event data. For a larger explicitly selected window, use individual API pages
instead. CSV fields are quoted and formula-like cell values are escaped.
