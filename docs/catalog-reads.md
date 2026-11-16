# Persistent catalog reads

This page owns catalog reads, conditional mapping metadata and the explicit
Development request boundary. [Revision uploads](catalog-revision-uploads.md),
[project creation HTTP](project-creation-http.md) and [saved reports](saved-reports.md)
own their separate actions. [Session host](session-host.md) owns protected Session
configuration; do not reuse the local HTTP identity recipe for hosted sign-in.
The original requirements are indexed in the
[catalog contract-owner map](catalog-persistence-plan.md#current-contract-owners).

The API reads saved projects, mapping metadata and immutable revision history.
It does not need private storage or a running mapping worker. A committed
decoded-invalid revision remains visible history; admission is not permission to
preview, edit or deploy it. The [catalog browser](catalog-browser.md) and
[saved reports](saved-reports.md) describe their own workflow and verification
boundaries.

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

Read availability does not enable writes. Explicitly enabled metadata editing is
available on mappings to current Publishers and Administrators. Creation, uploads,
reports, chat, preview and administration have their own feature and authority
gates; a read-only deployment must not advertise them as usable. This metadata
endpoint edits mappings, not projects. Submitted edits recheck fresh authority
rather than trusting advisory action flags.

## Conditional mapping metadata

`GET M/metadata` returns scope, name, description and metadata version, with a
quoted strong ETag bound to that complete scope and version. Head changes and
different admitted readers do not change this representation or its tag. After
fresh read authority, optional `If-Match` uses strong comparison and returns 412
on failure; then `If-None-Match` uses weak comparison and can return 304 with no
body. Combined conditions are bounded to 4096 characters and each header to 16
tags. Ordinary mapping responses do not carry this metadata-only ETag.

The quoted tag's opaque value is
`metadata-v1:<org>:<project>:<mapping>:<decimalVersion>`. EqualHead upload conditions
select a revision, not this independent metadata version.

When editing is enabled, `PATCH M/metadata` requires the custom media type
`application/vnd.weft.mapping-metadata-edit.v1+json`, a cookie-bound request token,
the canonical browser Origin and one strong metadata `If-Match` tag. Its document
contains exactly the case-sensitive string properties `name` and `description`;
both are required. This is a complete metadata replacement edit. Name is trimmed
and limited to 200 Unicode scalars; description is preserved and limited to 4000.
Strict UTF-8, no BOM, no NUL, and a 65,536-byte body limit apply. Duplicate,
unknown, missing, null or non-string properties are rejected. Content-Encoding
permits identity only; Content-Type permits only an optional UTF-8 charset.

The handler checks feature enablement, transport/token admission, scope, fresh
edit authority, content policy and preconditions before reading the body. It then
checks authority and version again in one short atomic write. A disabled route
returns 404 before resolving a reader or writer. Missing/denied/foreign resources
share 404; missing If-Match returns 428; malformed/list/wildcard or empty header
returns 400. A syntactically valid quoted empty opaque tag (`""`), weak tag or
nonmatching tag returns 412. An unsupported media type returns 415 and a body
above the limit returns 413. Errors expose neither metadata nor its current tag.

Success returns 200, the exact resulting metadata and its ETag. A matching
unchanged replacement preserves version and time; a stale unchanged replacement
still fails. Actual changes increment the metadata version once and preserve the
committed head. A changed mapping at the maximum version returns 409; an unchanged
replacement remains possible. Metadata transactions never span body reading.
Authorized enabled metadata replies advertise the custom type through Accept-Patch.

## Explicit local setup

The catalog and local identity are disabled by default. This local profile accepts
the local provider only in Development, on a concrete loopback listener. Hosted
configuration rejects an explicitly enabled local provider even when catalog
routes are disabled. All routes remain in the generated OpenAPI contract when
disabled, and requests cannot resolve a reader pool or create an anonymous actor.

Provision the separate `weft_catalog_reader` login before applying migration 0003
and `weft_catalog_editor` before migration 0004.
New local Compose installations require `WEFT_LOCAL_CATALOG_PASSWORD` along with
the existing separate passwords. For an existing installation, deliberately run
`deploy/local/postgres-add-catalog-reader.sh` with the local database administrator
and its environment before running the owner migration command. That script creates
only the new login and CONNECT grant; it does not replace existing credentials.
Likewise, existing installations deliberately run
`deploy/local/postgres-add-metadata-editor.sh` with
`WEFT_LOCAL_METADATA_PASSWORD` before applying 0004. New local Compose installs
require that distinct password too. These one-shot role creation scripts refuse
an existing login rather than silently changing credentials or grants. Stop old
hosts, provision, migrate and start the new host; strict old binaries are not
admitted against the changed mapping guard.

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

Metadata edits default to disabled. Enable them explicitly with
`WEFT_METADATA_EDITS_ENABLED=true` and the separate restricted editor connection
string in `WEFT_POSTGRES_METADATA_CONNECTION_STRING`. Catalog enablement is also
required. Read-only hosts need no editor credentials or writer pool; credentials
never fall back to catalog, ingestion or owner profiles.

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
fallback. Metadata PATCH uses this boundary before media or body handling.
[Uploads](catalog-revision-uploads.md) and [project creation](project-creation-http.md)
use the same request boundary and retain their own body and mutation checks.
Hosted sessions and secure cookie/key lifecycle belong to [Session host](session-host.md).

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

The dedicated editor receives full SELECT on the five ownership tables and
mappings, plus UPDATE on mapping name, description, metadata version and
updated-at only. It cannot advance heads or write revisions, reservations,
memberships, grants or DDL. Its short scoped transaction checks exact effective
rights and the current guard, then locks the admitted mapping and checks the
expected version. Concurrent metadata edits and head appends preserve both facts.

Qualification must distinguish synthetic contracts/setup from actual PostgreSQL and
Kestrel transport. Native upload HTTP, preview, identity, hosting and catalog-browser
qualification are separate boundaries, not implied by read or metadata checks.

## Historical implementation checkpoint

The following records P3d1/P3d2 evidence only. Counts and statements about later
increments are not current availability or setup instructions.

Status: P3d1 is qualified and merged in [PR #33](https://github.com/DeandreT/weft/pull/33)
at `e5915598d608b4df4b19c113d53a8bbda326cf9f`, closing
[#28](https://github.com/DeandreT/weft/issues/28). Local and independent fresh-build
runs each passed 1,162 tests with no skips; the independent run included 138 actual
PostgreSQL cases, fresh/0002-upgrade owner CLI and published API checks, and 22
browser cases. All 105 workers created by that run were automatically removed.
The PR records the frozen evidence and its limits. P3d2 metadata editing is
qualified and merged in [PR #38](https://github.com/DeandreT/weft/pull/38) at
`2a0557e554eafcd72bf7d0f71b34d9b23d2de398`, closing
[#29](https://github.com/DeandreT/weft/issues/29). Local and independent runs each
passed 1,246 C# cases with zero skips, including 193 PostgreSQL-gated cases. The
independent run also passed 22 browser cases without retries and fresh/upgrade
published CLI/API checks. All 110 actual workers were automatically removed.
These checks do not qualify the following raw-upload increment or a metadata
editing frontend.
The accepted sequence is in [Catalog API plan](catalog-api-plan.md).

The API reads saved projects, mapping metadata and immutable revision history.
It does not need private storage or a running mapping worker. A committed
decoded-invalid revision remains visible history; its admission is not permission
to preview, edit or deploy it. Projection and validation reports follow in P3e,
and the Angular catalog follows in P3g.
