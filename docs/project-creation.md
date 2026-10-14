# Explicit-capability project creation

P3d4a / [#39](https://github.com/DeandreT/weft/issues/39) implements the persistence
prerequisite for project creation. Its implementation and actual PostgreSQL
qualification follow the merged upload API in
[PR #40](https://github.com/DeandreT/weft/pull/40). The HTTP route, organization
actions and server composition are a separate dependent increment,
[P3d4b #31](https://github.com/DeandreT/weft/issues/31). Its request and host
contract is documented in [Project creation HTTP](project-creation-http.md).

## Creation authority

An active internal principal needs active admission to the selected organization
and an explicit `can_create_projects` capability. The capability belongs to that
exact organization/principal pair. An absent capability is false. A project role
does not grant creation authority, and identity provisioning does not grant it.

`IOrganizationProjectCreationReader` observes this admission without requiring an
existing project. Its result is advisory; `IProjectCreator` checks current
authority again inside the creation transaction. The actor comes from the trusted
server identity boundary, not a submitted role or principal claim.

Creation takes shared locks on principal, organization admission and capability
rows in a fixed order and rechecks them while locked. Those locks remain until the
caller transaction commits or rolls back. A revocation committed before that
check denies creation; a revocation that waits behind accepted creation affects
the next operation. Different creation keys can use compatible shared authority
locks rather than serializing an entire organization.

## Atomic creation and retries

The caller supplies a nonzero UUID idempotency key, a name and a description. Key
scope is the organization, with an immutable original actor. The database
generates a new project UUID; callers cannot choose or adopt an existing project.
One short transaction inserts all three facts or rolls them all back:

1. The new project, with version one and its original creator.
2. That creator's first active Administrator membership, with version one.
3. An immutable completed creation receipt.

The name uses the existing ingestion metadata validator: trim once, require
1–200 Unicode scalars and at most 800 UTF-8 bytes. Description is preserved
exactly, with at most 4,000 scalars and 16,000 UTF-8 bytes. Invalid Unicode and NUL
are rejected. Direct SQL must supply an already normalized name; SQL does not
apply a second normalization or change the description.

A version-one SHA-256 fingerprint binds the accepted intent. Its bytes are the
length-framed UTF-8 tag `weft.project-creation-intent`, a big-endian version,
network-order organization/actor/key UUIDs, then length-framed accepted name and
exact description. The database computes it, and the application independently
recomputes it when restoring a receipt. Timestamp and generated project UUID are
receipt facts rather than intent inputs.

Matching replay returns the original project, metadata, timestamp and initial
membership facts. Changed intent or another actor using that organization/key
gets an opaque conflict. Replay checks current identity, organization admission
and creation capability before looking up a receipt. It does not modify current
project membership: later demotion, inactivation or deletion remains effective.
The receipt's initial Administrator fact is historical and does not establish
current project access. Existing catalog reads check that access independently.

Same-key contenders arbitrate through the exact receipt primary key. The narrow
SQL exception block rolls back a losing attempt's temporary project/member
inserts, then reads the committed winner in a fresh snapshot. Only that exact
primary-key violation is handled as contention. Other SQL errors, cancellation
and timeouts remain failures. A connection failure during commit can be
ambiguous; retrying the same key recovers the durable result without creating a
new retry identity or restoring membership.

## Restricted database boundary

Migration `0005_project_creation.sql` adds the capability and immutable creation
tables, scoped foreign keys/RLS, a new-table transition guard and pinned routines.
Applied migrations 0001–0004 and predecessor role profiles remain unchanged.

The separately provisioned `weft_project_creator` login receives schema usage and
EXECUTE on exactly `get_project_creation_admission_v1` and `create_project_v1`.
It has no direct application-table SELECT or DML rights. The owner-defined
routines use qualified static SQL and a fixed trusted search path, scope every
query explicitly, generate IDs/fingerprints themselves and inspect the required
relation definitions. PUBLIC and all older application roles have no EXECUTE on
the new routines. Owner-only helpers are inaccessible to the creator login.

`PostgresProjectCreationDatabase` owns a dedicated reset-safe bounded pool. It
leases a short read-write Read Committed transaction, validates the installed
role/routine/source contract and binds the organization exactly once. Successful
receipts are returned only after commit. The existing one-second lock and
five-second statement limits apply. No body stream, native store or worker is
held inside this transaction.

The migration owner is trusted. Binary-pinned source checks can reject altered
routines at application admission; an owner-editable routine cannot securely
attest itself against a malicious owner. Relation locks protect admitted
relation definitions while held. Role, ACL and function-body checks are
admission checks and do not fence subsequent owner changes.

## Explicit owner setup

A fresh local compose database provisions the separate creator password/login.
For an existing database, stop application clients, supply
`WEFT_LOCAL_CREATOR_PASSWORD` in the updated local compose environment, and run
`deploy/local/postgres-add-project-creator.sh` explicitly as that database's
administrator before migration. The helper creates only the new login and its
CONNECT grant. It refuses an already existing login rather than rotating its
password or adopting a different profile. Updating an environment file alone
does not create a role in an existing volume.

Using the dedicated migration-owner connection in
`WEFT_MIGRATION_CONNECTION_STRING`, apply current migrations and then explicitly
grant or revoke a capability:

```sh
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool -- migrate
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool -- set-project-creation-capability ORGANIZATION_UUID PRINCIPAL_UUID true
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool -- set-project-creation-capability ORGANIZATION_UUID PRINCIPAL_UUID false
```

Both identifiers must be canonical lowercase nonzero UUIDs. Only the exact
lowercase `true` and `false` values are accepted. Granting requires an existing
active identity and organization admission. Revocation of an existing pair
remains possible when that identity/admission is inactive. Provisioning changes
only the capability; it never creates/reactivates identity, organization
admission or project membership. The web application does not use this owner
connection or expose this command.

## Qualification

Actual PostgreSQL gates cover fresh/current-0004 upgrade and no-op preservation,
direct-login restrictions, routine/role/relation tamper restoration, exact
fingerprint/text vectors, atomic counts and rollback, same-key contention,
distinct-key independence, both authority-revocation lock orders, immutable
replay after membership revocation, cancellation, same-backend pool reset and
cluster restart. The late-insert rollback case blocks only the new facts table
with an owner-held SHARE lock and observes the creator's actual backend locks
before cancellation; it adds no product fault hook. Owner-tool subprocess checks
exercise explicit grant/revoke and bounded credential-free output.

The source and qualification records belong to the focused #39 PR. A usable HTTP
creation feature is qualified separately in #31, including actual creation,
ordinary catalog reads and native revision upload in the new project.
