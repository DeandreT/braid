# External identity bindings

Status: P4a [#91](https://github.com/DeandreT/weft/issues/91) is selected source
preparation awaiting integration and qualification. Its P3g prerequisite is
complete: revision append is qualified and merged through
[PR #96](https://github.com/DeandreT/weft/pull/96), after all 140 fresh browser cases
passed with no retries or skips, completing
[#19](https://github.com/DeandreT/weft/issues/19). This document describes the
selected contract; it does not establish a working sign-in or a passed P4a suite.

## Identity without implicit permissions

A provider-neutral exact `(provider, authority, subject)` key binds to one existing
internal principal. The provider is a lowercase ASCII slug of 1–64 bytes, starting
with a letter and continuing with letters, digits, `.`, `_` or `-`. Authority and
subject are nonempty valid Unicode, limited to 1,024 and 512 UTF-8 bytes. Control
characters and NUL are rejected. Case, spaces and Unicode composition are
preserved exactly: there is no trimming, URL normalization or email-based join.
The future provider adapter must verify authentication before constructing its
key; this storage foundation does not validate a token or configure Entra.

Each binding has an immutable ID, key, internal owner and UTC creation time.
Creation starts enabled at version 1. Disabling compares that exact key, owner
and current version, then changes only enabled/version/UTC disable time; it is
one-way and increments once. There is no enable, reassignment or deletion API.
Same-key/same-owner bind replay returns the original record unchanged, including
disabled state; another owner conflicts. Binding requires an existing active
principal. Disabling remains possible when that principal is inactive.

Binding or resolving a key never creates an organization, principal, membership,
project role, project-creation capability or production-payload grant. Separate
owner preparation can create only its explicitly named missing principal or
organization-admission row. Neither preparation command reactivates an inactive
fact. Project authority remains the existing fresh database check; a future
payload read also needs its separately managed explicit grant and ownership policy.
Azure RBAC and directory roles are not application grants.

## Restricted fresh reads

`IExternalIdentityReader` resolves by exact key or immutable binding ID. Each call
reads the binding and current principal activity together in one read-committed,
read-only statement snapshot. It has no positive identity or authority cache.
Unknown keys, disabled bindings and inactive principals resolve closed. Storage,
schema/integrity failures and cancellation remain failures rather than invented
unknown identities. A resolved binding is an observation, not a permission lease;
a later session/request must check current facts again.

The separate `weft_identity_reader` login has only schema usage, the exact binding
columns and principal `id`/`active` SELECT. It cannot write, read project/grant or
artifact tables, administer roles, bypass RLS, create schema objects or execute
application helper functions. Owner operations use the separate `weft_migrator`
profile and current schema/role guards, never a web runtime reader credential.
Neither reader nor owner ports are registered as a browser login/bootstrap path.

## Installation and upgrade

The already selected migration is `0008_external_identity_bindings.sql`; prior
migration bodies/checksums remain unchanged. Follow [project ownership
setup](project-ownership.md#migrations-and-local-setup) and all earlier role
prerequisites. Fresh local initialization includes `weft_identity_reader` with its
separate `WEFT_LOCAL_IDENTITY_PASSWORD`. For an existing seven-migration database,
the database administrator explicitly runs
`deploy/local/postgres-add-identity-role.sh` with that password and the local
`POSTGRES_USER`/`POSTGRES_DB` environment before the coordinated stop/migrate/start
upgrade. The script creates a CONNECT-only login and refuses an existing role;
it does not rotate a password or grant data access. Changing an environment file
does not rerun initialization on an existing volume.

The ordinary `migrate` command applies 0008 with the dedicated migration login;
that migration supplies only the narrow reader grants. It creates no application
principals or admissions. Do not pass migration credentials to the server or use
Development provisioning as an external-account binding mechanism.

## Four explicit owner commands

Set `WEFT_MIGRATION_CONNECTION_STRING` privately for the separate owner process.
These commands use the existing DatabaseTool entry point; for a source checkout,
prefix each argument list below with:

```sh
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool --
```

| Arguments after `--` | Explicit effect |
| --- | --- |
| `ensure-principal PRINCIPAL_ID` | Create that missing active internal principal; existing active is unchanged, existing inactive is refused. |
| `ensure-organization-admission ORGANIZATION_ID PRINCIPAL_ID` | Require the existing organization and active principal; create only their missing active organization admission. Existing active is unchanged; inactive admission is refused. |
| `bind-external-identity PRINCIPAL_ID` | Read the key from stdin; create an enabled version-one binding or preserve same-owner replay. Does not prepare any parent or grant. |
| `disable-external-identity PRINCIPAL_ID EXPECTED_VERSION` | Read the same exact key from stdin; compare owner/version and disable once. Unknown key, different owner or stale version refuses. |

Replace placeholders with canonical nonzero lowercase `D`-format UUIDs. The
disable version is a canonical positive decimal Int64, without signs, spaces or
leading zeroes. Organizations have existence, not an active flag. Preparing an
organization admission does not create its organization or principal and does
not supply a project membership or `createProjects` capability.

Bind/disable read one UTF-8 JSON object from stdin through EOF, with exactly the
string members `provider`, `authority` and `subject`, in any order. The entire
input is limited to 8,192 bytes, including whitespace. Missing/duplicate/unknown
members, comments, trailing commas, invalid Unicode and trailing non-whitespace
input are rejected. Decoded key components still obey their smaller byte limits.
Use a protected input source or private file redirection; never place a key in
command arguments, an environment variable, shell history, repository fixtures
or diagnostic attachments. No key is echoed.

The tool emits only a fixed category, such as `{"outcome":"created"}`. It does
not print a binding ID, key, owner or version. Supply the expected version from a
separately authorized owner observation; for an unchanged newly created binding
it is 1. A successful disable makes it 2. Repeating expected version 1 then
conflicts; an already disabled binding with its current version returns
`alreadyDisabled` without writing. An `existing` bind result never means a
disabled identity was enabled or given application access.

| Exit | Output channel and meaning |
| --- | --- |
| 0 | stdout outcome: `created`, `existing`, `disabled` or `alreadyDisabled`. Success is printed only after the owner transaction and owned pool settle. |
| 3 | stdout refusal category: inactive/missing parent or admission, unknown binding, owner/key/version conflict or exhausted version. No implicit repair follows. |
| 2 | stderr `invalidRequest` or `configurationInvalid`; fix the explicit input/configuration. |
| 1 | stderr `persistenceFailure`; no raw exception, SQL, key or credential is printed. An ambiguous commit is not reported as success. |
| 130 | stderr `canceled` for handled cancellation; do not infer rollback after a dispatched commit. |

The legacy `provision-development-identity` and
`set-project-creation-capability` commands remain separate. Login, binding replay
and these preparation commands never invoke a project/payload-grant operation.
No CLI result enables the Development identity provider in a hosted environment.

## Verification boundary and later work

The selected source declares 34 pure, 18 actual-PostgreSQL and 8 compiled-CLI
cases: 60 intended cases, not compiled discovery or executed passes. Qualification
must separately exercise fresh/real-prefix upgrades, old data/definitions/ACL and
effective-right conservation, direct least-privilege refusal, current
binding/principal changes, collision/disable concurrency, durable restart and the
actual compiled command's private-safe outcomes. Synthetic policy facts do not
replace those database/tool checks.

This increment adds no OIDC callback, provider credentials, cookie/session,
HTTP authentication route, claims-to-role synchronization, payload access or
Angular sign-in control. Existing catalog Development identity and ordinary
project authorization remain unchanged. Payload permission, server sessions,
Entra integration and frontend authentication retain their separate scopes.
