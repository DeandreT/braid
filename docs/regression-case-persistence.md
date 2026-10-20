# Saved regression case persistence

The regression case store saves explicit project test fixtures, not production
payload captures. Each case version owns a bounded private envelope with its
exact revision, runtime selection, inputs, parameters and expected outputs.
The envelope format and replay boundary are described in
[Regression cases](regression-cases.md).

## Publication and access

Current publishers and administrators can create a case or append a version.
Version one binds its creator and mapping. Later versions preserve that creator
and mapping and identify the principal who saved that particular version.
Publishing a case makes it an explicit project sample: current runners,
publishers and administrators may read its private content. Viewers may list
case names and revision/version metadata, but cannot read samples. None of
these permissions grants access to production run payloads.

Every operation requires current principal, organization and exact project
membership. The writer locks those facts before case-row contention and keeps
the locks until commit. Private reads recheck authority after decoding the
bounded envelope; metadata lists recheck before returning their page. Returned
data is not a durable execution permission: regression dispatch must check
current authority again.

## Atomic immutable versions

A case advances one positive version at a time. Concurrent attempts at the
same next version produce one winner; a different losing payload conflicts.
An exact original retry returns its original version even after newer versions
exist. It does not revert the current head or create another version.

The private envelope is stored transactionally in PostgreSQL with its length,
SHA-256 and scope. Reads verify the stored reference against the complete
envelope before returning any private data. Metadata and bytes therefore
cannot commit separately or leave an unreferenced object after a failed save.
The PostgreSQL store uses dedicated reader/writer logins with function-only
access; neither can query the payload table directly or modify retained rows.
Each operation verifies both those role privileges and the exact function and
schema contract, including table ownership, columns, constraints, triggers,
policies and table/column grants. Unexpected schema changes refuse the operation rather
than silently adopting a weakened data boundary.

Each save has its own transaction. A conflict, invalid case or denied project
cannot roll back an unrelated successful case. Callers processing multiple
cases must keep those independent failure boundaries rather than wrap them in
a shared all-or-nothing operation. An ambiguous commit remains an exception;
retrying the same case/version and exact envelope resolves it without duplicate
publication.

## Retention and activation

Saved case versions do not inherit temporary preview expiry. The store exposes
no delete operation; immutable versions remain until an explicit, separately
authorized retirement workflow is supplied. That future workflow must preserve
the references needed by retained regression runs and deployment evidence.

The schema is applied only by the migration owner after its declared
prerequisites. Migration `0014_regression_cases.sql` follows preview migrations
0012/0013; migration 0015 adds the durable run ledger. Application hosts must never install them, grant roles or elevate
their own credentials on demand. Source preparation, isolated schema checks,
the complete ordered upgrade, and public API/worker activation are distinct
qualification boundaries.
