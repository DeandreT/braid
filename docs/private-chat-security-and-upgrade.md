# Private chat persistence security and local upgrade

Chat metadata uses independent `weft_chat_reader`, `weft_chat_writer` and
`weft_chat_lifecycle` logins and resetting pools. No login is an API, model-provider or authentication
session credential. The host does not register these stores; activating them
is a separate change.
These are trusted server credentials, never end-user logins; transaction-local
organization/creator settings narrow scope but do not authenticate a caller.

## Admission

Every operation admits all three profiles afresh, including reads, exact replays and
refusals. Each login is CONNECT-only before installation, LOGIN/NOINHERIT,
NOSUPERUSER/NOBYPASSRLS/NOCREATEDB/NOCREATEROLE/NOREPLICATION, with no membership,
role settings, expiration, parameter rights, sequence rights or default grants.
Installation refuses missing or pre-broadened profiles rather than repairing or
adopting them. No older role or guard is broadened.

The migration grants only named columns, conversation DELETE for the writer,
schema USAGE, and exact named semantic routines. The lifecycle login receives
only exact-target observe/close/erase and bounded schema-lock EXECUTE, never
content-column SELECT or DML. Non-content
`schema_migrations(version,checksum)` SELECT permits exact installed-history
admission; no journal mutation, table-wide SELECT or session-table right is
granted. The source migration is the canonical column privilege matrix.

The guard binds trusted organization/creator context, fixes the SQL
environment, holds the runner's shared transaction advisory migration lock and
takes transaction-held ACCESS SHARE locks through `chat_schema_lock` on the
fixed journal, authority, five ledger and report-source relations. The exact
owner/source/ACL of this no-content capability is admitted before invocation;
the complete contract is checked again with those locks held. Reader/writer
admission retains its allowed-column no-row SELECT locks. No lifecycle table
read capability or reader tombstone listing is needed. These locks do not lock authority rows or grant
access to immutable native payload metadata. Authority is read in a fresh
statement after any conversation-row wait.

Admission compares the embedded and installed history, permanent owned tables,
physical ordered columns/types/defaults/collations, exact keys/checks/FKs and
their internal enforcement, indexes, exact creator RLS policy, ordinary and
deferred triggers, full routine bodies/security/search paths and exact new-table
ACLs. Existing checksum-pinned source and maintenance descriptors are reused
without relaxing their authority. A dropped attribute is not a restored schema.
Namespace admission excludes only the literal reserved `pg_` prefix, never a
SQL `LIKE 'pg_%'` pattern: `_` is a wildcard there, so a normal schema such as
`pgx` must remain in application-privilege inspection. Temporary-schema
exclusions likewise use the literal `pg_temp_` and `pg_toast_temp_` prefixes.

ACCESS SHARE relation locks block ACCESS EXCLUSIVE requests, such as ADD COLUMN,
and the shared migration lock coordinates with `MigrationRunner`. SHARE ROW
EXCLUSIVE is compatible with ACCESS SHARE: ENABLE/DISABLE TRIGGER changes are
not fenced by this capability. Exact trigger admission detects drift on a fresh
checkout; it does not prevent compatible DDL after admission. This is not an
ACL, routine-owner or compatible-DDL commit fence against an administrator.
Role or grant changes require a coordinated stop/migrate/start boundary.

## Direct writer invariants

The database rejects immutable-field rewrites, completed/terminal resurrection,
unfenced output, unordered/over-budget output and replayed reservations. Deferred
conservation also requires:

`turn.version = event_count + round_count + completed_round_count - accounted_round_count`

Each start/event/terminal record, new reservation and one-way round completion
accounts for exactly one version step. A typed completion publishes its single
accounting event and immutable round metadata in that same step, not two steps.
The check also requires a one-to-one exact round/fence/metadata match for every
accounting event. Version-only updates, orphan accounting events and child-only round
completion cannot commit. Application CAS predicates still express the caller's
expected version; database facts never recreate outbound dispatch permission.

Typed metadata admission checks exact required key sets and JSON types before
casting or comparing their values. Missing keys (SQL NULL), explicit JSON null
and coercible wrong types cannot stand in for mandatory version, round, scope,
report, provenance or enum fields. Predicate results are definite booleans.
Required nullable keys still accept their specified JSON null values: absent
token counts remain unknown, optional accounting facts remain optional, and
whole-report citations have null resource/ordinal fields. Diagnostic ordinals
must be numeric integers; resource targets require strings. Citation scope also
uses a null-safe complete-row comparison against the retained conversation.

Deferred checks are independent of the caller's scope at constraint evaluation
or COMMIT. Each queued row supplies its immutable organization/creator IDs; the
`SECURITY INVOKER` trigger binds those IDs only within its own function-local
settings before reading the ledger. Function `SET` entries make PostgreSQL
restore the incoming scope on normal return, error and the retained-parent
deletion path. Clearing, replacing or making a final caller GUC malformed cannot
hide a live mutated turn from conservation checks. Both conservation triggers
remain invoker routines, without callable EXECUTE grants or RLS bypass.
Only actual row absence under the exact admitted creator policy permits the
deferred check to skip a turn removed by the guarded retention cascade.

Trusted lifecycle close/erase routines own the chat constraint boundary: they
defer only the chat conservation checks for the multi-row command, then flush
them before leaving their owner-defined scope, and leave those checks deferred.
The invoker triggers consequently run while the narrowly trusted routine is
active, not later under a lifecycle login which deliberately lacks content reads.
Their queued-row scope and the routine's input scope are function-local and
restore the caller's exact settings. Other constraint modes are untouched.
Lifecycle entry points return only phase/version or a closed outcome; no content
or accounting payload is returned. Identity tombstones survive erasure and never
automatically expire or authorize identity reuse.

## New local database

Use the existing pinned `deploy/local/postgres.compose.yml` flow and private
`.env` file. Supply **independent** `WEFT_LOCAL_CHAT_READER_PASSWORD`,
`WEFT_LOCAL_CHAT_WRITER_PASSWORD` and `WEFT_LOCAL_CHAT_LIFECYCLE_PASSWORD` along with the existing role passwords. The new
volume initializer creates all three restricted logins with CONNECT only. The explicit
migration owner subsequently installs schema and named grants.

Chat routines pin `weft.organization_id` and `weft.chat_principal_id` in function
`SET` clauses. PostgreSQL lets a non-superuser owner declare custom settings there
only with `SET` privilege on them, so the administrator grants
`SET ON PARAMETER weft.organization_id, weft.chat_principal_id` to `weft_migrator`
before migration. The initializer and the existing-volume script both do this.
A managed host must allow its administrator to grant that privilege.

Do not put passwords or connection strings in source control. The three passwords
must not be equal or reused from another role. There is no public host setting,
provider registration, browser route or DI activation in this persistence source.

## Existing local volume: explicit operator step

Back up the database, stop all consumers and use the existing database
administrator to run the additive script once **before** the new migration.
First update the existing private `deploy/local/.env` with all three independent chat
passwords and all existing required values. An already running container retains
its old environment: `compose --env-file ... exec` does not forward newly added
`.env` values into it. Recreate **only** the database service with the same compose
file, project identity and existing named `postgres-data` volume, wait for health,
then execute the script:

```sh
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml \
  up -d --no-deps --force-recreate --wait postgres &&
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml \
  exec -T postgres bash -s < deploy/local/postgres-add-chat-roles.sh
```

This replaces the container, not its volume; the existing database is preserved
and the new-volume initializer does not rerun. Keep consumers stopped until the
coordinated migration and verification finish. Stop if recreation or health fails.
Do not change the compose project
name, remove/reinitialize the volume, or use `down --volumes`. Passwords are read
from the private environment by the existing compose definition and `psql`
`\getenv`, not placed in command arguments. Do not enable shell tracing or print
`compose config`, container inspection, environment or password values into logs.
This is an explicit local operator procedure, never application-startup behavior.

The script targets the local `weft` database and creates all three logins plus CONNECT
in one transaction. It fails if any role already exists; it never rotates a
password, resets another role, widens authority or silently repairs a failed
installation. Have the owner inspect any pre-existing profile and resolve it
through a separate approved procedure instead of rerunning with broader grants.

Then apply migrations using the existing migration tool and migration-owner
credential, verify exact admission using independent reader/writer/lifecycle connections,
and restart only the explicitly approved consumers. Never run provisioning from
application startup. An old volume does not rerun `postgres-init.sh`.

`ChatSchemaContract` binds the `0011_private_chat` checksum and the expected
migration prefix. Correct the schema with a new migration; never rewrite applied
migrations, adopt live definitions, or broaden old expected grants.
