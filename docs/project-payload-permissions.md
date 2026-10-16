# Explicit project payload permission

Implementation and qualification are in progress for
[P4b #93](https://github.com/DeandreT/weft/issues/93). This document describes the
selected contract; it does not establish passed database/tool qualification or
working production-payload access. It builds on the qualified, merged
[external identity foundation](external-identity-bindings.md). Migration
`0009_project_payload_read_grants.sql` adds the separate permission schema.

## Permission and current project authority

`run.payload.read` requires an explicit grant for one complete organization,
project and internal principal tuple. `weft.project_payload_read_grants` stores
only `organization_id`, `project_id`, `principal_id`, `active` and a positive
Int64 `version`. The tuple is immutable and unique, with composite references to
the exact project and project membership. Rows are retained when revoked.

Every known project role, including Administrator, is denied without its explicit
active grant. Viewer, Runner, Publisher and Administrator keep their existing
mapping actions; this permission introduces no second role matrix. Organization
admission, project creation, membership promotion/demotion, external identity
binding or login never creates or changes this grant. Azure RBAC, Entra directory
roles and claims do not supply it.

`IProjectPayloadPermissionReader.ObserveAsync` intersects the exact active grant
with the existing project, active internal principal, active organization
admission and active exact project membership in one fresh Read Committed
statement. An organization has existence, not an active flag. The reader uses
known project roles and one organization-bound read-only transaction; it does
not combine a cached membership with a later grant lookup.

An absent project, missing/inactive authority or absent/inactive grant returns
the same null observation. A positive `ProjectPayloadGrant` contains only its exact scope,
principal, active state and observed grant version. Invalid facts/schema, storage
failures and cancellation remain failures. A positive result is an observation,
not a lease, token or storage capability.

Revoking principal activity or either admission makes the next observation deny,
even while the grant row stays active. Restoring that authority can make the same
still-active grant effective again; it does not issue a new grant. An explicitly
revoked grant stays inactive until a separate owner regrant. Future sensitive
payload I/O must freshly combine these facts with the actual run/sample ownership
policy at its own boundary. This foundation makes no promise about revocation
racing with an in-flight payload operation.

## Restricted reader and separate owner

The dedicated `weft_payload_permission_reader` login receives schema USAGE and
exactly seventeen non-grantable column SELECT privileges:

| Table | Readable columns |
| --- | --- |
| `principals` | `id`, `active` |
| `organization_memberships` | `organization_id`, `principal_id`, `active` |
| `projects` | `organization_id`, `id` |
| `project_memberships` | `organization_id`, `project_id`, `principal_id`, `role`, `active` |
| `project_payload_read_grants` | All five grant columns |

It has no table-level SELECT, data writes, helper EXECUTE, role memberships,
ownership, creation privilege or RLS bypass. It cannot read external bindings,
sessions, mappings, native/report objects, runs or project-creation capabilities.
Organization existence follows the existing project's exact foreign key, so it
needs no `organizations` SELECT. Organization RLS supplements the explicit tuple
checks. Existing runtime/catalog/identity profiles gain no payload-table access.

`ProjectPayloadGrantProvisioner` uses the separate migration owner and validates
the current exact migration/schema/profile contract. It locks authority parents
before the grant and rechecks current authority after waits. An active request,
including active-state replay or regrant, requires the existing project, active
principal and active organization/project admissions. Revoke can proceed when
those facts are inactive. Neither operation creates or reactivates a parent,
membership, identity binding or project-creation capability.

Expected version zero means the grant is absent. Granting an absent tuple creates
active version 1; revoking an absent tuple with expected 0 returns unchanged and
creates no row. An existing row requires its exact current version before deciding
whether the requested state is unchanged. A stale version conflicts even for a
same-state request. A state change increments once; same-state replay preserves
the version. Explicit later owner regrant is permitted. At Int64 maximum, mutation
is refused and exact-version same-state replay remains unchanged.

The owner acknowledges an outcome only after COMMIT. It performs no automatic
retry and never reports ambiguous commit as success. A later payload read still
needs its own fresh authorization.

## Explicit owner command

Set `WEFT_MIGRATION_CONNECTION_STRING` privately for the separate migration-owner
process. Never pass that credential to a server or runtime reader. The selected
DatabaseTool command is:

```text
set-project-payload-read ORG PROJECT PRINCIPAL EXPECTED_VERSION true|false
```

For a source checkout, prefix the argument list with the existing tool entry:

```sh
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool --
```

The three IDs must be canonical nonzero lowercase `D`-format UUIDs.
`EXPECTED_VERSION` is a canonical decimal from 0 through 9223372036854775807,
without signs, whitespace or leading zeroes. The state must be exactly lowercase
`true` or `false`. With an absent grant, expected 0/true creates version 1; then
expected 1/false revokes it to version 2. Repeating expected 1 conflicts, while
expected 2/false is unchanged. Obtain a current version through a separately
authorized owner observation; this command prints no grant tuple or version.

| Exit | Output and meaning |
| --- | --- |
| 0 | stdout `{"outcome":"created"}`, `updated` or `unchanged`, after the owner operation and pool settle. |
| 3 | stdout outcome `authorityUnavailable`, `versionConflict` or `versionExhausted`; no implicit repair follows. |
| 2 | stderr error `invalidRequest`, or `configurationInvalid` when the owner connection environment value is missing. |
| 1 | stderr error `persistenceFailure`; no SQL, provider exception, connection text or identifiers are printed. |
| 130 | stderr error `canceled` for handled cancellation; do not infer rollback after a dispatched COMMIT. |

Errors use the fixed JSON shape `{"error":"category"}`; refusals and successes
use `{"outcome":"category"}`. These are categorical results, not a grant-management
HTTP API. Existing identity preparation/binding and Development provisioning
commands remain separate and never invoke this operation.

## Local initialization and existing-volume upgrade

Follow [project ownership setup](project-ownership.md#migrations-and-local-setup)
and all earlier role prerequisites. The selected new-volume setup adds a separate
`WEFT_LOCAL_PAYLOAD_PERMISSION_PASSWORD` to `deploy/local/.env.example` and compose.
First initialization creates the dedicated reader with CONNECT only; the additive
migration supplies its exact schema/column grants. It creates no application
parent or payload-grant rows.

An existing volume does not rerun initialization when its environment file changes.
Once this increment is integrated and released, use the explicit coordinated
stop/migrate/start procedure for an existing identity-prefix database:

1. Stop application processes using that database and retain its volume. Complete
   any missing earlier role-provisioning prerequisites.
2. The database administrator explicitly runs
   `deploy/local/postgres-add-payload-permission-role.sh` with the separate
   `WEFT_LOCAL_PAYLOAD_PERMISSION_PASSWORD` and the local `POSTGRES_USER`/
   `POSTGRES_DB` environment, following the
   [identity upgrade pattern](external-identity-bindings.md#installation-and-upgrade).
   It creates only the new CONNECT-only login and refuses an existing role; it
   does not adopt, alter or rotate it.
3. Run the existing DatabaseTool `migrate` command with the private migration
   credential, then restart application processes with their separate runtime
   credentials after the upgrade succeeds.

Migration 0009 is additive. Prior SQL bodies/checksums and strict profiles remain
unchanged. Fresh and genuinely populated identity-prefix upgrade qualification must preserve old rows, bindings,
journals, source definitions and effective authority, allowing only the precise
additive schema/reader/FK metadata and journal entry.

## Scope and qualification

This foundation adds grant facts, fresh observation and explicit owner operations.
It adds no server registration or configuration key for a permission reader,
HTTP grant/payload route, sign-in, session, claims synchronization, Angular control,
download token, run/sample storage, payload bytes, preview or Wolverine execution.
Future consumers retain their own activation and qualification scope.

Implementation and qualification remain in progress. Planned pure, actual
PostgreSQL, genuine-prefix upgrade and compiled-tool checks are acceptance work,
not executed passes. No full-suite, browser, native worker or hosted-auth result
is established by this document.
