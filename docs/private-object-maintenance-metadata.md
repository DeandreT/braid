# Private object maintenance metadata

Maintenance targets exact owned orphan identities rather than sweeping a storage
directory. A final object and one temporary publication of that object are
different targets. Neither target needs an invented content digest or byte count
when a write was never acknowledged.

Issue #60 adds the metadata and publication-registration boundary. Storage fencing
and tracked temporary-file recovery follow in #61; the actual bounded command
follows in #62. This foundation performs no filesystem deletion and exposes no
maintenance HTTP route or background job.

## Retention and eligibility

Every committed native revision remains protected, including historical revisions
after head and metadata changes. Every resumable `reserved`, `staged` or `ready`
ingestion remains protected. Expired leases and actor revocation do not abandon
an upload. Every usable derived result remains protected, whether selected or
retained; this policy adds no successful-report expiry.

Terminal rejected, head-conflict and attempt-exhausted native stages can become
candidates after a 24-hour database-clock grace and only without an exact native
revision reference. Failed or expired unfinished derived candidates require the
same grace and no usable result reference or live owning claim. Unknown legacy
files have no invented ownership and remain retained.

A positively registered abandoned temporary publication can be a separate target
while its final native object stays protected for resume. The later storage
adapter must also prove that its actual writer is absent; database lease expiry
alone does not establish that a process released a file.

## Permanent retirement and short claims

Retirement is irreversible for its exact full-scope target. A separate retained
attempt journal records database-issued claim tokens, instance IDs, generations,
leases and categorical completion. Reclaiming an expired attempt never revives
the target or rewrites old evidence.

Retirement and every applicable reference-attachment path use the same immutable
ingestion-header or derivation-key lock. Separate post-wait queries under required
Read Committed isolation observe current references and retirement decisions.
Pinned supplementary triggers cover restricted direct writes as well as the
ordinary application ports. Existing historical transition rules remain in force.

One claim call examines at most 100 materialized candidates and returns at most
16 claims. Its project-bound cursor covers due retirements, terminal native
stages, derived candidates and tracked temporary publications. These counts do
not claim to measure every physical PostgreSQL tuple or index access; transaction
and statement timeouts also bound execution. Each claim lasts 60 seconds without
renewal. Failed or expired work waits another 30 seconds before retry; generation
eight exhausts the target without clearing its fence.

Acknowledgement checks the complete original claim and its live lease. Deleted
and missing are observations supplied by the later qualified storage adapter;
metadata tests authoring those dispositions do not prove physical deletion.
Cancellation and transport loss leave truthful retained attempts. Exact retained
generation lookup can resolve an uncertain acknowledgement after the original
operation settles, without substituting a newer claim.

## Publication ownership

The publication sidecar records an exact existing ingestion or report owner, the
root and temporary IDs, a prior-absence commitment and database reservation time.
A separate immutable binding records actual file and directory identities before
the later storage adapter consumes or writes payload. The caller-known natural
key resolves a lost reservation acknowledgement. No caller is active in #60's
ordinary storage path; #61 integrates the explicit owned capability at the two
existing service publication sites.

Binding does not manufacture content facts or confer permission by itself.
Registry writes recheck the original actor, exact current claim, full object and
retirement state. A retained observation can report expired ownership without
granting further writes. A temporary retirement never retires its final object.

## Publication store kinds

Each registration records the explicit kind of the store that registered it in
`storage_kind`, and the publication guard stamps the immutable `format` label from that
kind. Ordinary logins may insert the kind but can never write the label.

| Store kind | Format label |
| --- | --- |
| `filesystem` | `weft.private-publication.linux.v2` |
| `azure-blob` | `weft.private-publication.azure-blob.v1` |

A missing or unknown kind is refused, and the format check admits only these exact
pairs. Reservation and natural-key recovery carry the registering store's kind: a
retained record of another kind is a conflict and is never returned as that store's
registration. Maintenance claims restore the kind and label together and acknowledge only
the exact retained pair, and each store refuses to retire a tracked temporary whose
publication carries another kind.

Migration 0023 adds `storage_kind` and replaces only the format check and the
publication guard body. Registrations made before it were all written by the filesystem
store; they are recorded as `filesystem` and keep `weft.private-publication.linux.v2`.

## Roles and deployment

The maintenance pool requires its separate `weft_maintenance` credential and an
explicit organization/project scope. It never falls back to owner, ingestion,
derivation or catalog credentials. This is scoped invocation, not a claim that
one configurable operator credential is permanently limited to one project.

Migration 0007 appends the publication, retirement and cleanup-attempt tables,
their exact routines, indexes, guards and grants. Migrations 0001–0006 remain
unchanged. Ordinary ingestion and derivation roles gain only the accepted new
publication columns and retirement-identity reads; they cannot write retirements
or invoke maintenance routines. All affected pool admissions pin the exact new
schema and retain their checks for historical guards and privileges.

For a fresh local database, use the compose configuration and separate maintenance
password in `deploy/local/.env.example`. For an existing six-migration database,
stop its application processes, explicitly run
`deploy/local/postgres-add-maintenance-role.sh` with the database administrator
and separate password environment, then run the current DatabaseTool migration
before starting the current application. The one-shot helper grants only CONNECT
and refuses an existing role without rotating or adopting it. The migration
assigns the actual narrow schema authority atomically. This metadata upgrade
does not change the private-storage format; #61 has its own activation procedure.

Implementation and qualification are separate. Actual PostgreSQL tests cover
metadata authority, migration and races; authored leases, inode identities and
deletion dispositions remain explicitly synthetic. Filesystem, process-death and
integrated cleanup qualification belong to the dependent increments.
