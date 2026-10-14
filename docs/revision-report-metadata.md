# Revision report metadata

This increment adds durable metadata for projection and validation reports of an
immutable catalog revision. It does not generate, store, decode, or serve report
payloads. Native derivation and the persisted-report API follow in separate
increments; the existing example viewer continues to use its current routes.

## Identity and permissions

A report key includes the complete organization/project/mapping/revision scope,
native object identity and class, digest and byte count, logical document,
dependency identity, backend profile, report schema, algorithm, codec, and all
policy limits. Canonical UTF-8 policy and key bytes have stable SHA-256
fingerprints. A fingerprint locates a candidate; every restored field and the
full committed source tuple must still match.

The reader and derivation writer use separate restricted database logins.
`weft_report_reader` can read the seven ownership/catalog source tables and two
report tables. `weft_deriver` adds only the named report INSERT/UPDATE columns.
Neither profile can change the native revision, ingestion history, membership,
mapping metadata, or project-creation capabilities.

Every read checks current active principal, organization admission, project
membership, and the full revision ancestry. All four project roles can access
report metadata. A derivation claim does not preserve an old permission grant:
completion checks the original actor's current authority after any key wait.
Unauthorized and missing metadata have the same result. Database/schema/session
failures remain errors rather than being reported as missing data.

## Durable transitions

The six application ports read the authorized source, current metadata, or a
retained attempt; claim a derivation; finish it; or request repair of an exact
selected result. Current states are `NotDerived`, `Pending`, `Available`, and
`Failed`. A read never starts work or recovers an expired claim.

Claims use database-owned identities, generation, a token, candidate object ID,
and a 60-second lease. There is no lease renewal. Each transition holds only a
short database transaction, with a one-second lock timeout and five-second
statement timeout. It does not span worker or storage I/O. Failed attempts have
a 30-second retry delay; an explicit claim recovers expiry. Generation overflow
is refused.

Completed attempts are immutable. Repair checks the exact selected derivation,
creates a new attempt, advances the generation, and clears that selection
atomically. It retains the original successful result and all previous attempts.
Observation absence and an explicitly present empty observation remain
different. Nullable validity, failed operations, actual issue counts, and
projection coverage retain their meanings; a valid=false validation report can
be usable.

## Installation and upgrade

Migration `0006_revision_derivations.sql` adds two tables, their constraints,
policies and transition guards, and one standalone unique index on the native
source tuple. The five predecessor migration files remain unchanged. Dedicated
roles must exist with CONNECT-only provisioning before this migration runs.
Both new runtime pools validate the accepted schema, checks, transition bodies,
privileges, session settings, and full source index on every lease.

New local Compose installations provision both logins from separate
`WEFT_LOCAL_REPORT_PASSWORD` and `WEFT_LOCAL_DERIVATION_PASSWORD` values. An
existing installation needs an explicit administrator step before the
coordinated stop/migrate/start upgrade: run
`deploy/local/postgres-add-report-roles.sh` inside its PostgreSQL container with
both new passwords supplied in the environment. The script creates both roles
and grants CONNECT in one transaction. Existing role names cause failure and
rollback; the script does not adopt or rotate existing credentials.

Metadata qualification uses actual PostgreSQL transitions and independently
reopened pools/processes. Report completion facts and owner-controlled clock or
counter setup in these tests are explicitly synthetic. They do not qualify
report object bytes, the report codec, Ferrule derivation, or report HTTP routes.
