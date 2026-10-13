# Scoped ingestion metadata

P3c1 supplies the trusted server metadata boundary for immutable revision
ingestion. Implementation and qualification are tracked in
[#23](https://github.com/DeandreT/weft/issues/23). The
[ingestion plan](catalog-ingestion-plan.md) separates this PostgreSQL foundation
from P3c2's actual storage/native workflow. This library adds no HTTP identity,
client evidence input, or native execution.

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
belongs to P3c2 and never runs with a metadata transaction open.

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
confirmed valid/decoded-invalid results can produce revisions. P3c1 tests use
explicitly synthetic admission evidence; P3c2 proves real object/native admission.

## Local provisioning and upgrade

New Compose databases provision three separate logins: migrator, reader and
ingestion. Copy the updated `.env.example` and set four private passwords.
Bootstrap reads passwords inside `psql`; arguments contain no password. The
owner-only migration tool preserves `0001` and its exact checksum.
Compose readiness checks the final TCP listener, so its temporary initialization
server cannot satisfy `up --wait` before role provisioning finishes.

For an existing ownership-only database, add the ingestion password to the
private environment file, supply it to the service, then explicitly provision
the login before applying the new migration:

```sh
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml up -d --wait
docker compose --env-file deploy/local/.env -f deploy/local/postgres.compose.yml exec -T postgres bash < deploy/local/postgres-add-ingest.sh
dotnet run --configuration Release --project dotnet/tools/Weft.DatabaseTool -- migrate
```

The tool uses `WEFT_MIGRATION_CONNECTION_STRING`. The one-shot script creates
only the new login/CONNECT grant and never changes existing passwords/roles.
Do not rerun it when the login already exists. Migrations use a no-CREATEROLE
owner, and ordinary web startup does not configure ingestion or a default actor.

Actual PostgreSQL gates cover fresh/upgrade/concurrent/restart migrations,
privilege/policy/guard tampering, immutable ancestry, scoped retries, lease
fencing, head ordering, original-actor authority and pooled-state reset.
Exact-source local/clean evidence is linked in the scoped PR before merge.
