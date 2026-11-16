# Scoped ingestion metadata

PostgreSQL stores immutable revision ingestion metadata. The
[ingestion service](native-revision-ingestion.md) coordinates storage and native
validation around this metadata.

## Authority and immutable publication

Four scoped tables separate mappings, immutable revisions, upload reservations
and receipts, and admission attempts. Composite keys preserve complete ancestry
and exact native reference facts. A mapping and first revision commit together
with a final head. Completed attempts and committed revision facts cannot change.

`PostgresIngestionDatabase` owns a restricted pool separate from the read-only
`PostgresDatabase`. `PostgresRevisionIngestionStore` exposes typed workflow
methods without a public raw connection or write flag. Short Read Committed
transactions bind one organization locally, with one-second lock/five-second
statement timeouts. Incomplete work rolls back and pooled context resets.

The ingestion role has SELECT/INSERT and only named mutable-column UPDATE grants.
It cannot mutate ownership, update/delete revisions, create schema, or bypass
RLS. Preflight checks table/column/grant-option rights, policies and transition
guards. The existing reader keeps its five-table whitelist and read-only API;
column-only writes are also refused. Every workflow statement fresh-checks active
principal, organization admission and exact-project Publisher/Administrator.
Recovery and receipts require the original actor.

One final SQL statement checks current authority, fenced lease and eligible
evidence; it locks the reservation/mapping, allocates committed order, inserts
the revision, changes the head once and finalizes the receipt. CTEs communicate
through `RETURNING`. Revocation committed before that statement prevents
publication; an already-running statement retains its snapshot. External I/O
never runs with a metadata transaction open.

## Requests, leases and evidence

Intent has accepted display metadata, backend ID and exact native digest/count
before reservation. Future IDs are generated once. Matching keys replay original
identities/profile/policy; changed intent conflicts even after completion.
Canonical fingerprint v1 uses strict UTF-8 and explicit big-endian framing;
native JSON, generated IDs and current deployed images do not determine it.

Claims use 120 seconds of database time, a random token/server instance and
increasing generation, with eight explicit claims at most. Stale writers cannot
record evidence or publish. Reclaimed ready work records a newly verified stage,
retaining the original eligible validation attempt rather than inventing a run.

Defensive versioned evidence retains scope/digest/profile, status, confirmation,
nullable validity, counts and bounded derived diagnostics. Database-normalized
evidence overflow is an explicit policy rejection with no invented validator
result. Eligibility is not inferred from diagnostic counts. Only exact matched
confirmed valid/decoded-invalid results can produce revisions.

## Local provisioning

New Compose databases provision three separate logins: migrator, reader and
ingestion. Copy the updated `.env.example` and set four private passwords.
Bootstrap reads passwords inside `psql`; arguments contain no password. The
owner-only migration tool applies the database schema.
Compose readiness checks the final TCP listener, so its temporary initialization
server cannot satisfy `up --wait` before role provisioning finishes.

Supply the passwords in the private environment file, start PostgreSQL and
apply the schema:

```sh
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml up -d --wait
dotnet run --configuration Release --project dotnet/tools/Braid.DatabaseTool -- migrate
```

The tool uses `BRAID_MIGRATION_CONNECTION_STRING`. Migrations use a no-CREATEROLE
owner, and ordinary web startup does not configure ingestion or a default actor.

Actual PostgreSQL gates cover fresh/upgrade/concurrent/restart migrations,
privilege/policy/guard tampering, immutable ancestry, scoped retries, lease
fencing, head ordering, original-actor authority and pooled-state reset.
