# External identity bindings

## Identity without implicit permissions

A provider-neutral exact `(provider, authority, subject)` key binds to one existing
internal principal. The provider is a lowercase ASCII slug of 1–64 bytes, starting
with a letter and continuing with letters, digits, `.`, `_` or `-`. Authority and
subject are nonempty valid Unicode, limited to 1,024 and 512 UTF-8 bytes. Control
characters and NUL are rejected. Case, spaces and Unicode composition are
preserved exactly: there is no trimming, URL normalization or email-based join.
The provider adapter verifies authentication before constructing its key.

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
fact. Project authority uses fresh database checks; payload reads also need a
separately managed explicit grant and ownership policy.
Azure RBAC and directory roles are not application grants.

## Restricted fresh reads

`IExternalIdentityReader` resolves by exact key or immutable binding ID. Each call
reads the binding and current principal activity together in one read-committed,
read-only statement snapshot. It has no positive identity or authority cache.
Unknown keys, disabled bindings and inactive principals resolve closed. Storage,
schema/integrity failures and cancellation remain failures rather than invented
unknown identities. A resolved binding is an observation, not a permission lease;
a later session/request must check current facts again.

The separate `braid_identity_reader` login has only schema usage, the exact binding
columns and principal `id`/`active` SELECT. It cannot write, read project/grant or
artifact tables, administer roles, bypass RLS, create schema objects or execute
application helper functions. Owner operations use the separate `braid_migrator`
profile and current schema/role guards, never a web runtime reader credential.
Neither reader nor owner ports are registered as a browser login/bootstrap path.

## Installation

Follow [project ownership setup](project-ownership.md#migrations-and-local-setup).
Local initialization provisions `braid_identity_reader` with its separate
`BRAID_LOCAL_IDENTITY_PASSWORD`; the migration tool installs the schema and narrow
reader grants. Do not pass migration credentials to the server or use Development
provisioning as an external-account binding mechanism.

## Explicit owner commands

Set `BRAID_MIGRATION_CONNECTION_STRING` privately for the separate owner process.
These commands use the existing DatabaseTool entry point; for a source checkout,
prefix each argument list below with:

```sh
dotnet run --configuration Release --project dotnet/tools/Braid.DatabaseTool --
```

| Arguments after `--` | Explicit effect |
| --- | --- |
| `ensure-principal PRINCIPAL_ID` | Create that missing active internal principal; existing active is unchanged, existing inactive is refused. |
| `ensure-organization-admission ORGANIZATION_ID PRINCIPAL_ID` | Require the existing organization and active principal; create only their missing active organization admission. Existing active is unchanged; inactive admission is refused. |
| `bind-external-identity PRINCIPAL_ID` | Read the key from stdin; create an enabled version-one binding or preserve same-owner replay. Does not prepare any parent or grant. |
| `disable-external-identity PRINCIPAL_ID EXPECTED_VERSION` | Read the same exact key from stdin; compare owner/version and disable once. Unknown key, different owner or stale version refuses. |
| `bind-entra-user PRINCIPAL_ID` | Read an Entra user's tenant and object ID from stdin and bind their [tenant-stable key](#entra-users), with the same semantics as `bind-external-identity`. |
| `disable-entra-user PRINCIPAL_ID EXPECTED_VERSION` | Read the same tenant and object ID from stdin and disable that key, with the same semantics as `disable-external-identity`. |

Replace placeholders with canonical nonzero lowercase `D`-format UUIDs. The
disable version is a canonical positive decimal Int64, without signs, spaces or
leading zeroes. Organizations have existence, not an active flag. Preparing an
organization admission does not create its organization or principal and does
not supply a project membership or `createProjects` capability.

Bind/disable read one UTF-8 JSON object from stdin through EOF, with exactly the
string members `provider`, `authority` and `subject`, in any order; the Entra user
commands read exactly `tenantId` and `objectId`, each a canonical nonzero lowercase
`D`-format UUID, and refuse the Microsoft consumer tenant. The entire
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

The `provision-development-identity` and
`set-project-creation-capability` commands remain separate. Login, binding replay
and these preparation commands never invoke a project/payload-grant operation.
No CLI result enables the Development identity provider in a hosted environment.

## Entra users

An Entra user is keyed on the tenant-stable pair the Microsoft identity platform
issues in both ID tokens and access tokens: provider `entra.oid`, authority the
canonical tenant ID (`tid`) and subject the user's canonical object ID (`oid`) in
that tenant. Browser sign-in requests the `profile` scope, without which Entra omits
`oid` from the ID token, and builds this key from the verified ID token. Access
tokens carry the same `tid` and `oid`, so one person keeps one key, binding and
principal across client applications. The per-application pairwise `sub` is checked
but never used as a key, because a token issued to another client application
carries a different `sub` for the same person. [Bearer access](api-authentication.md)
resolves a delegated access token's `(tid, oid)` through the same binding, so one
`bind-entra-user` admits a person through both browser sessions and delegated API access.

Use `bind-entra-user` for Entra keys; `bind-external-identity` refuses Entra
providers so their canonical form stays fixed.
