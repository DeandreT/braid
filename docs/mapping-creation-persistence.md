# Durable mapping creation

## Trust and retained content

V1 uses the proposal/authoring ID as its immutable session identity. Each session
has one immutable snapshot and proposal. `creation_proposals` holds the bounded
binary restoration envelope and separately queryable exact ingestion intent;
`creation_sources` pins selected original and extraction object IDs;
`creation_approvals` holds the separate deliberate decision. A new generation
uses a new proposal identity and request key. Exact retention replay is allowed;
replacement conflicts.

The internal Application codec is available to the trusted PostgreSQL assembly.
It uses the plan's `CopyBytes`, `Parse` and compiler digest rather than depending
on its field layout. It preserves captured bytes and canonical extraction bytes,
notes, native bytes and reports. Restoration checks component/aggregate budgets,
canonical encoding, source/evidence identities, citations and the recomputed
proposal fingerprint. It never calls the specification physical-object gate.
The 64 MiB aggregate ceiling is binary, not a base64-expanded envelope.

Complete byte evidence and Partial Office/PDF evidence are admitted. The writer selects exact published
objects from the trusted registry and pins their IDs. It verifies source version,
format, digest/count, extractor/profile, evidence digest/count and source digest.
Snapshot validity follows selected content; unrelated bundle additions do not
invalidate it. Publisher/content authority is separate from permission to send
content to an AI provider; no AI-sharing authority is introduced here.

The database writer is a trusted generation-retention capability. Digests detect
substitution; they do not cryptographically prove compiler execution. Model/API
input has no public restoration constructor or approval-store capability.

## Transactions and failure boundaries

Approval acquisition first commits the exact decision using the approver pool.
It then opens a new READ COMMITTED transaction, rechecks the same decision and
snapshot and holds SHARE authority/store/bundle locks until lease disposal.
Separate reader connections see the committed decision and restore retained
content with MVCC; no proposal UPDATE or specification object gate is involved.
The reader permits retained review after source staleness but checks current
content authority. Terminal ingestion receipts are reconciled by the service
before attempting a fresh snapshot fence.

Authority locks are principal, organization membership, project membership,
payload grant, project, followed by stores in sorted order and the bundle.
Nonblocking authority/snapshot lock acquisition prevents waits behind a queued
revoker while a calling lease holds shared fences. Contention is a categorical
failure, never permission to proceed. The lease holds no ingestion or catalog
row locks.

The ingestion trigger is the independent failure backstop: creation-owned create
keys require exact approved native/metadata intent at reservation and current
authority/snapshot fences in the transaction publishing the committed receipt.
Those locks survive through the ingestion transaction's own commit even if the
outer connection dies. Failure rolls back mapping, revision and receipt changes
together. A shared key advisory lock closes concurrent retain-versus-reserve
races, including direct ingestion of a key before proposal retention.

A decision committed before a crash remains approved but unreserved. Recovery
only resumes a discovered existing operation; a subsequent explicit approval can
reserve the original key. Lost acknowledgements use the actual receipt reader.
NeedsSource recovery never replaces native bytes or allocates another key.

## Roles and deployment

Provision CONNECT-only `braid_creation_reader`, `braid_creation_writer` and
`braid_creation_approver` before migration. Local/Azure provisioning exposes
optional separate credentials; absent credentials leave unusable password logins.
Reader, writer and approver pools admit only exact function grants. No creation
profile gets direct table DML or ingestion capabilities.

MigrationRunner verifies installed migration checksums. Runtime admission checks the catalog shape,
function bodies/signatures/ACLs, constraints, indexes, RLS policies and ingestion
guard. Ingestion admission also verifies the creation guard dependency shape.

## Qualification

Actual PostgreSQL tests use the existing disposable `PostgresFixture` native
route. Application orchestration tests use the real RevisionIngestionService and
PostgreSQL metadata with synthetic validator/in-memory object ports.
`MappingCreationPersistenceTests` covers disabled internal FK triggers,
terminated-lease publication rollback, independent reads, lost acknowledgements
and exact registry selection.
