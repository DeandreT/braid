# Project creation HTTP

[Project creation persistence](project-creation.md) owns capability, atomicity and
replay. This page describes HTTP admission, status codes and local host options;
[catalog reads](catalog-reads.md) describes shared read/request rules.

## Organization actions

`GET /api/v1/catalog/context` reports the server feature flag
`features.createProjects` and the freshly observed organization action
`organizationActions.createProjects`. The action requires explicit capability
for the configured actor and organization; an existing project's Administrator
role does not supply it. Project-scoped `access.actions.createProjects` stays
false. An advisory action is not permission for a later write.

When creation is disabled, context uses the ordinary catalog reader and reports
the action as false without resolving a creator. When enabled, it reads fresh
ordinary admission and fresh creation admission before issuing the request
token. Read failures remain errors rather than a false capability.

## Create and replay

`POST /api/v1/organizations/{organizationId}/projects` accepts one canonical
nonzero UUID `Idempotency-Key`, the framework request token, and an exact matching
Origin. Its media type is `application/vnd.braid.project-create.v1+json`, with only
an optional UTF-8 charset. The document contains exactly two required strings:

```json
{"name":"Customer imports","description":"Mappings for customer imports."}
```

The body is bounded to 65,536 bytes. Missing or duplicate fields, unknown fields,
invalid Unicode, unsupported content encodings, query parameters, and conditional
headers are rejected. Name acceptance trims once and permits 1–200 Unicode
scalars; description is preserved exactly and permits 0–4,000 scalars. The
existing metadata rules also bound their UTF-8 sizes and reject NUL.

Feature and identity checks, canonical organization binding, fresh creation
admission, and header/known-length validation precede body reads. A separate
immediate gate admits at most two creation operations; it does not queue requests.
No database transaction or authority lock spans body I/O. The final persistence
operation rechecks current authority under its locks and is the sole source of a
successful creation or replay.

A connection reset while reading the body aborts the request without writing a
response, even when the framework's cancellation notification arrives later.
The creation permit is released so another request can proceed.

Creation returns 201 with outcome `created`; matching replay returns 200 with
outcome `replayed`. Both return the same immutable receipt and scoped project
URL in the result and Location header. The receipt records the original creator,
accepted metadata, UTC creation time, initial project version `"1"`, and initial
active Administrator membership at version `"1"`. Those membership facts are
history. Current catalog reads and native uploads check current project access
independently, and replay never restores a revoked membership.

Admitted capability denial returns 403 `permissionDenied`; changed intent or
another actor using the same organization/key returns opaque 409
`idempotencyConflict`. Missing admission is 404. Invalid documents, oversized
bodies, unsupported media, and a full creation gate return 400, 413, 415, and 503
respectively. Persistence failure is a closed 500 error. Responses use
`Cache-Control: no-store`; errors expose no receipt, key, winning actor, private
object reference, or provider exception text.

## Host configuration

Creation defaults off. Explicitly enable it with
`BRAID_PROJECT_CREATION_ENABLED=true` and supply the separate restricted login in
`BRAID_POSTGRES_PROJECT_CREATION_CONNECTION_STRING`. The enabled flag accepts only
the exact values `true` and `false`. For the Development profile, enabled creation
requires the existing enabled catalog and explicit loopback Development identity
configuration. Protected Session hosts use [Session host](session-host.md),
including its protected credential delivery and identity admission, rather than
this local recipe.

The creator connection has no fallback to reader, metadata editor, ingestion, or
owner credentials. Disabled creation does not parse an unused connection or
register a creator pool. The enabled pool is lazy and is independent of native
worker and private-store availability. Owner-controlled capability provisioning
remains the separate DatabaseTool command; the web application cannot grant its
own capability.

HTTP tests compose creation, catalog reads and native upload into the project
created by the POST. Published-host checks cover setup, replay and API/database
restart.
