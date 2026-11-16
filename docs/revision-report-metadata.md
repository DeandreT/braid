# Revision report metadata

PostgreSQL stores metadata for projection and validation reports of an immutable
catalog revision. [The report workflow](revision-report-workflow.md) coordinates
derivation and stored report bytes.

## Identity and permissions

A report key includes the complete organization/project/mapping/revision scope,
native object identity and class, digest and byte count, logical document,
dependency identity, backend profile, report schema, algorithm, codec, and all
policy limits. Canonical UTF-8 policy and key bytes have stable SHA-256
fingerprints. A fingerprint locates a candidate; every restored field and the
full committed source tuple must still match.

The reader and derivation writer use separate restricted database logins.
`braid_report_reader` can read the seven ownership/catalog source tables and two
report tables. `braid_deriver` adds only the named report INSERT/UPDATE columns.
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

## Installation

Migration `0006_revision_derivations.sql` adds two tables, their constraints,
policies and transition guards, and one standalone unique index on the native
source tuple. Dedicated roles must exist with CONNECT-only provisioning before
the schema is applied. Both runtime pools validate the accepted schema, checks, transition bodies,
privileges, session settings, and full source index on every lease.

Local Compose installations provision both logins from separate
`BRAID_LOCAL_REPORT_PASSWORD` and `BRAID_LOCAL_DERIVATION_PASSWORD` values. Apply the
schema before opening either runtime pool.
