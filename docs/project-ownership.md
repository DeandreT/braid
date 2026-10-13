# Project ownership foundation

P3a adds PostgreSQL ownership and scoped application reads. It does not expose
catalog HTTP routes or supply a browser identity. The existing Development
mapping examples continue through their separate fixed-example routes.
P3a is qualified and merged in
[PR #21](https://github.com/DeandreT/weft/pull/21), with local and independent
clean-container evidence for 23 actual PostgreSQL cases and the full C# suite.

## Ownership and application reads

The `weft` schema retains organizations, internal principals, organization
admission, projects, and explicit project memberships. Composite foreign keys
preserve organization/project and organization/principal ancestry. History
references use restrictive deletion. Organization admission grants no automatic
project access. A role in one project grants no access to another.

`ActorContext` carries an internal principal ID supplied by a trusted server
identity boundary. It carries no cached role grant. P4 will connect verified
Entra sessions to this boundary; P3a adds no default HTTP actor or trusted client
identity header.

`IProjectReader` lists visible projects, retrieves one project, and observes its
role/actions. Each operation checks active principal, organization admission,
and exact project membership together in a fresh Read Committed statement.
Revocation affects the next operation; an already running statement retains its
database snapshot. Writes in later increments must check authority again at
publication. Database/configuration failures throw rather than becoming an
empty authorized result.

Project pages contain at most 100 records, ordered by PostgreSQL UUID order.
The next cursor is the last emitted project UUID when another row exists.
Pagination does not freeze the catalog across later inserts or membership
changes. IDs remain GUIDs in these internal contracts; public string wire
records follow in P3d.

## Database boundary

`PostgresDatabase` owns a restricted runtime pool. It leases one connection,
begins an explicit read transaction, validates the runtime role and fixed
organization policies, and binds one organization with transaction-local
`set_config`. Rebinding or a preset/stale context is refused. The public API
does not expose an unscoped connection. Disposal rolls back incomplete
transactions and returns a reset connection to the pool.

Runtime must remain separate from the schema owner, with only the exact P3a
read grants. Owner/superuser/RLS-bypass and privileged role memberships, schema
creation, table writes, truncation and migration-journal access are refused.
Organization RLS supplements the explicit project checks. Fixed policy
validation rejects weakened or additional policies, and transaction-held read
locks prevent policy DDL changing the admitted guard before reads finish.

Connection settings preserve pooled-state reset and disable ambient enlistment
and multiplexing. Explicit unsafe settings are rejected. This local baseline
uses Npgsql 10.0.3 and PostgreSQL 16.15 on the pinned linux/amd64 manifest in
the local compose file; it does not qualify Azure PostgreSQL configuration.

## Migrations and local setup

Cluster/database/role provisioning is separate from ordinary migrations.
`MigrationRunner` uses the `weft_migrator` owner login and its own pool. It takes
a database-local transaction advisory lock before inspecting history, validates
ordered exact SQL checksums, and commits DDL plus journal records atomically.
Unknown or changed history and an unversioned owned schema are refused. Add a
new migration to evolve installed data; do not edit an applied script.

For a new local database, copy `deploy/local/.env.example` to
`deploy/local/.env` and replace all four passwords. The compose project owns
its own volume and exposes PostgreSQL only on `127.0.0.1:54329`:

```sh
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml up -d --wait
```

Its first initialization creates separate `weft_migrator` and `weft_runtime`
logins, transfers database ownership, and restricts public privileges.
The ingestion increment also provisions its separate restricted `weft_ingest`
login. Existing ownership-only databases require that login before upgrading;
see [ingestion metadata](ingestion-metadata.md).
Initialization reads passwords from the environment inside `psql`; they are
absent from command arguments. Initialization does not run again on an existing
volume. Updating the environment alone does not rotate an existing role password.

Set `WEFT_MIGRATION_CONNECTION_STRING` to the migration login's connection
string for that database, then run the migration-only tool:

```sh
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool -- migrate
```

The tool does not create principals, grant project membership, or enable server
identity. Use controlled owner setup for explicit membership records. P3d adds
catalog configuration after storage and immutable ingestion are qualified.
Stop this compose project with `down` to retain its volume; adding `--volumes`
removes that project's data and is an explicit development reset.

## Verification

Actual PostgreSQL tests opt in with `WEFT_POSTGRES_IMAGE` set to the immutable
manifest used by the compose file. A partially configured or invalid test
environment fails. Qualification must execute these tests; an unconfigured
skip is not database evidence.

The fixture creates uniquely owned container, network and persistent volume
resources, separate synthetic owner/runtime logins, and per-case databases.
Host mode publishes an ephemeral loopback port. An isolated qualification
client can be named with `WEFT_TEST_CLIENT_CONTAINER`; only that client joins
the owned internal network, and PostgreSQL exposes no host port. Cleanup checks
resource ownership before removal and never selects an existing user database.

Tests cover migration races and rollback, history tampering, role restrictions,
RLS policy weakening, composite scope constraints, membership revocation,
literal metadata, bounded reads, and restart persistence. Pool-size-one probes
confirm the same backend is reused without context after commit, rollback,
cancellation or SQL failure. Qualification evidence is recorded in the scoped
implementation PR before merge.
