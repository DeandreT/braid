# Project ownership foundation

PostgreSQL ownership and scoped application reads provide the authorization
foundation for catalog routes and the workbench. Browser identity is supplied by
the separate Development or verified Session boundary.

This page describes shared authority, transaction and migration rules. Use
[development](development.md) for the full local stack and [Session host](session-host.md)
for protected hosted composition.

## Ownership and application reads

The `braid` schema retains organizations, internal principals, organization
admission, projects, and explicit project memberships. Composite foreign keys
preserve organization/project and organization/principal ancestry. History
references use restrictive deletion. Organization admission grants no automatic
project access. A role in one project grants no access to another.

`ActorContext` carries an internal principal ID supplied by a trusted server
identity boundary. It carries no cached role grant. Verified Entra sessions resolve
to this boundary; a client-supplied identity header cannot select an actor.

`IProjectReader` lists visible projects, retrieves one project, and observes its
role/actions. Each operation checks active principal, organization admission,
and exact project membership together in a fresh Read Committed statement.
Revocation affects the next operation; an already running statement retains its
database snapshot. Writes check authority again at
publication. Database/configuration failures throw rather than becoming an
empty authorized result.

Project pages contain at most 100 records, ordered by PostgreSQL UUID order.
The next cursor is the last emitted project UUID when another row exists.
Pagination does not freeze the catalog across later inserts or membership
changes. IDs remain GUIDs in these internal contracts; public string wire
records and their stricter cursor rules are owned by [catalog reads](catalog-reads.md#http-surface).

## Database boundary

`PostgresDatabase` owns a restricted runtime pool. It leases one connection,
begins an explicit read transaction, validates the runtime role and fixed
organization policies, and binds one organization with transaction-local
`set_config`. Rebinding or a preset/stale context is refused. The public API
does not expose an unscoped connection. Disposal rolls back incomplete
transactions and returns a reset connection to the pool.

Runtime must remain separate from the schema owner, with only the exact
read grants. Owner/superuser/RLS-bypass and privileged role memberships, schema
creation, table writes, truncation and migration-journal access are refused.
Organization RLS supplements the explicit project checks. Fixed policy
validation rejects weakened or additional policies, and transaction-held read
locks prevent policy DDL changing the admitted guard before reads finish.

Connection settings preserve pooled-state reset and disable ambient enlistment
and multiplexing. Explicit unsafe settings are rejected. This local baseline
uses Npgsql 10.0.3 and PostgreSQL 16.15 on the pinned linux/amd64 manifest in
the local compose file.

## Migrations and local setup

Cluster/database/role provisioning is separate from ordinary migrations.
`MigrationRunner` uses the `braid_migrator` owner login and its own pool. It takes
a database-local transaction advisory lock before inspecting history, validates
ordered exact SQL checksums, and commits DDL plus journal records atomically.
Unknown or changed history and an unversioned owned schema are refused. Add a
new migration to evolve installed data; do not edit an applied script.

For a new local database, copy `deploy/local/.env.example` to
`deploy/local/.env` and replace all passwords. The compose project owns
its own volume and exposes PostgreSQL only on `127.0.0.1:54329`:

```sh
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml up -d --wait
```

Its first initialization creates separate `braid_migrator` and `braid_runtime`
logins, transfers database ownership, and restricts public privileges.
Each feature uses a separate restricted login: `braid_ingest` for ingestion,
`braid_catalog_reader` for catalog reads, `braid_project_creator` for project
creation, `braid_report_reader` and `braid_deriver` for reports, and
`braid_maintenance` for cleanup. See [ingestion metadata](ingestion-metadata.md),
[catalog reads](catalog-reads.md), [project creation](project-creation.md),
[report metadata](revision-report-metadata.md),
[maintenance metadata](private-object-maintenance-metadata.md),
[payload permissions](project-payload-permissions.md) and
[session metadata](postgres-server-sessions.md) for their grants and credentials.
Initialization reads passwords from the environment inside `psql`; they are
absent from command arguments. Initialization does not run again on an existing
volume. Updating the environment alone does not rotate an existing role password.

Set `BRAID_MIGRATION_CONNECTION_STRING` to the migration login's connection
string for that database, then run the migration command:

```sh
dotnet run --configuration Release --project dotnet/tools/Braid.DatabaseTool -- migrate
```

The migration command does not create principals or memberships. The separate
explicit `provision-development-identity` owner command supplies local catalog
admission and optional grants on existing projects; neither command enables server
identity. Its configuration and refusal rules are in the catalog-read documentation.
Stop this compose project with `down` to retain its volume; adding `--volumes`
removes that project's data and is an explicit development reset.

## Verification

Actual PostgreSQL tests opt in with `BRAID_POSTGRES_IMAGE` set to the immutable
manifest used by the compose file. A partially configured or invalid test
environment fails.

The fixture creates uniquely owned container, network and persistent volume
resources, separate synthetic owner/runtime logins, and per-case databases. The
network takes an explicit small subnet from the test range described in
[Docker test networks](development.md#docker-test-networks).
Host mode publishes an ephemeral loopback port. An isolated qualification
client can be named with `BRAID_TEST_CLIENT_CONTAINER`; only that client joins
the owned internal network, and PostgreSQL exposes no host port. Cleanup checks
resource ownership before removal and never selects an existing user database.

Tests cover migration races and rollback, history tampering, role restrictions,
RLS policy weakening, composite scope constraints, membership revocation,
literal metadata, bounded reads, and restart persistence. Pool-size-one probes
confirm the same backend is reused without context after commit, rollback,
cancellation or SQL failure.
