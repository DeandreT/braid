# Persistent catalog reads

Status: P3d1 implementation in [draft PR #33](https://github.com/DeandreT/weft/pull/33),
tracked in [#28](https://github.com/DeandreT/weft/issues/28). Local verification passed
1,151 tests without failures or skips. Independent qualification is pending.
The accepted sequence is in [Catalog API plan](catalog-api-plan.md).

The API reads saved projects, mapping metadata and immutable revision history.
It does not need private storage or a running mapping worker. A committed
decoded-invalid revision remains visible history; its admission is not permission
to preview, edit or deploy it. Projection and validation reports follow in P3e,
and the Angular catalog follows in P3g.

## HTTP surface

Let `O=/api/v1/organizations/{organizationId}`, `P=O/projects/{projectId}` and
`M=P/mappings/{mappingId}`.

| GET route | Returns |
| --- | --- |
| `/api/v1/catalog/context` | Freshly admitted local identity, allowed organization, supported features and cookie-bound request token |
| `O/projects` | Visible project page with current role/action availability |
| `P` | Visible project metadata and current role/action availability |
| `P/mappings` | Mapping page with metadata, backend and committed head |
| `M` | Mapping metadata, committed head and current role/action availability |
| `M/metadata` | Scope, name, description and metadata version only |
| `M/revisions` | Committed history in descending sequence |
| `M/revisions/{revisionId}` | Exact scoped immutable revision metadata |

Resource IDs are canonical nonzero lowercase UUID strings. All versions and
sequences are positive decimal strings, including values above JavaScript's
integer precision. Native byte counts remain bounded integers. Timestamps are
UTC. Replies use explicit generated public contracts and `Cache-Control: no-store`.

Pages accept only `limit` and `cursor`, with default 50 and limits 1 through 100.
Projects and mappings use ascending PostgreSQL UUID order; history uses descending
committed sequence. Continuations are canonical base64url JSON, bounded to 512
decoded bytes and checked for exact version, shape, kind, order and full scope.
They carry no authority. Each page checks current access; paging has no retained
snapshot, lock or unbounded count. Unknown/duplicate query parameters and malformed
IDs or continuations return the closed invalid-request category.

Every resource statement checks an active principal, active organization admission
and active membership in that exact project. Context needs organization admission
and can succeed before the actor has any projects. A missing/inaccessible collection
parent returns 404; an accessible empty collection returns an empty page. Missing,
foreign and inaccessible project resources share the same 404 response. Database
or schema failures return a closed failure category and never an invented empty
catalog. No caller actor, role, proxy header or digest establishes access.

Only catalog viewing is available in this increment. Creation, metadata maintenance,
uploads, reports, chat, preview and membership actions remain explicitly unavailable,
including for Publishers and Administrators. Later mutation endpoints recheck fresh
authority rather than trusting these advisory flags.

## Explicit local setup

The catalog and local identity are disabled by default. This increment accepts
the local provider only in Development, on a concrete loopback listener. Hosted
configuration rejects an explicitly enabled local provider even when catalog
routes are disabled. All routes remain in the generated OpenAPI contract when
disabled, and requests cannot resolve a reader pool or create an anonymous actor.

Provision the separate `weft_catalog_reader` login before applying migration 0003.
New local Compose installations require `WEFT_LOCAL_CATALOG_PASSWORD` along with
the existing separate passwords. For an existing installation, deliberately run
`deploy/local/postgres-add-catalog-reader.sh` with the local database administrator
and its environment before running the owner migration command. That script creates
only the new login and CONNECT grant; it does not replace existing credentials.

```sh
dotnet run --project dotnet/tools/Weft.DatabaseTool -- migrate
```

The owner tool requires `WEFT_MIGRATION_CONNECTION_STRING`. Configure explicit
nonzero `WEFT_DEV_ORGANIZATION_ID`, `WEFT_DEV_PRINCIPAL_ID` and
`WEFT_DEV_ORGANIZATION_NAME`, then deliberately provision the local identity:

```sh
dotnet run --project dotnet/tools/Weft.DatabaseTool -- provision-development-identity
```

This atomic operation creates the supplied organization, principal and admission
when absent. Matching repeats preserve existing facts. It refuses inactive
identities/admission or conflicting organization metadata. It creates no projects.
An optional paired `WEFT_DEV_PROJECT_ID` and `WEFT_DEV_PROJECT_ROLE` grants the
explicit role on an existing project in that organization; an inactive or different
existing grant is refused. Roles are `viewer`, `runner`, `publisher` or
`administrator`. Runtime, catalog and ingestion logins cannot run this operation.
The provisioner is never registered or invoked by the web host.

For the API, configure all of these values:

- `ASPNETCORE_ENVIRONMENT=Development`.
- `WEFT_CATALOG_ENABLED=true` and `WEFT_DEV_IDENTITY_ENABLED=true`.
- The explicitly provisioned `WEFT_DEV_ORGANIZATION_ID` and `WEFT_DEV_PRINCIPAL_ID`.
- `WEFT_DEV_LISTEN_ADDR=127.0.0.1:4100` and
  `WEFT_DEV_BROWSER_ORIGIN=http://127.0.0.1:4100`, or matching concrete loopback
  IPv6 address and port.
- `WEFT_CATALOG_CONNECTION_STRING` for the separate catalog login, without migration
  or ingestion credentials.

The browser origin must exactly match the configured listener's address and effective
port. Canonical browser origins omit default HTTP port 80; redundant explicit `:80`
is refused rather than producing a configuration whose Host/Origin cannot match.
Use the packaged Angular assets served by the API for one browser origin; the
separate Angular development server is not an admitted catalog origin. Boolean
enablement accepts explicit `true`/`false`, and malformed identity configuration
fails rather than becoming a default account.

## Request and database boundaries

Actual loopback peer, HTTP scheme and exact configured Host are checked on each
catalog request. Forwarding headers do not change identity or transport admission.
Context issues a framework antiforgery token only after fresh organization admission.
Its cookie is HttpOnly, SameSite Strict and scoped to `/api/v1`; local HTTP uses a
non-Secure development cookie. The request token is returned only in context and
is omitted from diagnostic record text.

The reusable unsafe-request boundary requires one exact canonical Origin and one
`X-XSRF-TOKEN`, validates the cookie/request-token pair bound to configured organization
and principal, then permits a handler to proceed. A required header prevents form-body
fallback. No production mutations are introduced here; test-only mutations qualify
this boundary, and d2 through d4 additionally qualify each real mutation. Hosted
sessions and secure cookie/key lifecycle remain P4.

`weft_catalog_reader` receives seven full-table SELECT grants: the five ownership
tables, mappings and mapping revisions. Its reservation access is column-only SELECT
on organization ID, project ID, operation ID and display filename. It cannot read
attempts, evidence, reservation worker profiles or lease data, or write
catalog/ownership facts. Full revision-table grants include stored native object IDs;
the catalog queries and public DTOs omit those IDs and do not expose private object
I/O. Existing runtime and ingestion profiles remain unchanged.

The dedicated pool leases short read-only, bounded, organization-scoped transactions.
It checks exact effective rights and trusted permanent source tables, ancestry,
constraints, RLS policies, transitions and trigger enforcement before reading. It
refuses privileged/inherited/delegation/PUBLIC/default grants and unaccepted schema
changes. Pool scope is transaction-local and checked/reset on reuse. These checks
are admission observations, not a promise to freeze membership or grant changes
until commit.

Qualification must distinguish synthetic contracts/setup from actual PostgreSQL and
Kestrel transport. Entra, native upload HTTP, preview, hosting and the eventual
catalog browser are separate later increments.
