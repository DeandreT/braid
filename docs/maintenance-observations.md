# Maintenance observations

Issue #72 adds three bounded read-only ports for the later maintenance command.
They report retained metadata, physical directory entries and agreement between
those observations. They confer no claim, retirement, publication or deletion
authority. This increment adds no command, HTTP route, schema or grant.

Implementation and qualification are recorded separately in
[issue #72](https://github.com/DeandreT/weft/issues/72) and its linked implementing
pull request. This document describes the contract; it does not claim a test run.

## Retained metadata status

`IPrivateObjectMaintenanceStatusReader.GetStatusesAsync` takes one exact
`ProjectScope` and an optional project-bound retirement cursor. Each page carries
one database observation time and at most 100 flat retirement headers, ordered by
PostgreSQL UUID order. A page below 100 is complete for that selected statement;
100 conservatively returns a continuation, even when the next page is empty.
A continuation does not make earlier pages a project-wide snapshot.

Headers expose the current generation, lease times and categorical completion,
without lease tokens, instance IDs or original workflow claims. State is computed
at the page's database time: unattempted, leased, retry waiting, retry due,
completed or exhausted. A live unfinished eighth attempt remains leased; it is
exhausted after expiry or an unsuccessful finish. Deleted or Missing completion
must agree with the exact current completed-attempt pointer. Retry times retain
the existing 60-second lease, 30-second delay and eight-attempt policy.

The existing dedicated maintenance pool retains its full role/schema admission,
organization/project binding and transaction limits. Observation queries use only
the three already-readable maintenance tables. Flat headers and witnesses do not
restore ingestion or derivation claims through broader source-table reads.

## Physical entries

`IPrivateObjectStorageObserver.ObserveAsync` takes one project on an already-open
strict v2 store. It traverses only that project's native and derived data
namespaces, native first, through mapping, revision and payload levels. Every
consumed non-dot entry spends the shared 100-entry budget, including hierarchy
directories, hidden or unknown names, symlinks and vanished entries. It stops at
100 without reading a 101st entry. Unknown or unsafe subtrees remain untraversed.

Each entry retains the actual held parent identity independently of optional
child facts. Raw names remain private owned bytes, available only through the
cancellation-aware copying method `CopyRawName`; ordinary entry serialization and
its fixed `ToString` expose no raw-name property. No replacement decoding merges
different native byte names.

The key for detecting a repeated entry is its held parent identity plus exact raw
name bytes. Different names for a hardlinked inode remain distinct entries. The
first repeated key refuses the whole invocation with
`PrivateStorageObservationChangedException`, including a repeat at the 100th
consumed entry. No partial page or positive ownership result survives that failure.

Canonical safe mapping/revision directories are structural observations. Positive
payload slots additionally require exact canonical spelling and actual regular
file, owner, mode, single-link, parent and full storage-incarnation identities.
A temporary name supplies a TempId, not an invented object ID. Unsafe entries
retain categorical unproved facts. Observation reads no payload bytes and creates,
repairs, follows, renames or deletes no storage entry.

`TraversalComplete` describes only this bounded traversal: both visited
namespaces reached EOF, no subtree was skipped and the limit was not reached.
It does not establish an atomic, current or eventually complete project census;
native-first traversal may repeatedly consume the available budget.

## Matching observed registrations

After physical observation has settled and its descriptors are closed,
`IPrivateObjectPublicationObservationReader.MatchAsync` takes an owned request of
at most 100 slots. No database transaction spans physical observation. Results
preserve input order and ordinal and carry a later database observation time,
which is not the filesystem observation time.

Each slot returns NoMatch, ExactlyOne or Ambiguous, with exactly zero, one or two
distinct flat witnesses. Matching requires the complete scope, object class,
root/control/boot/mount namespace, effective user and file/parent identities, plus
the final object ID or temporary TempId. Witnesses contain the retained natural
publication key and immutable origin, without credentials or reconstructed claims.
Bounded results do not promise a fixed number of database tuple visits.

ExactlyOne means that the earlier physical facts agreed with one bound retained
registration at the later query. It proves neither content, current permission,
continued physical presence nor cleanup eligibility. The consuming command must
await each port to settle and keep metadata, entry, slot, match and unproved
counts separate. Safe hierarchy directories are not unproved payloads. Aggregate
counts are per-invocation observations, never a total orphan count.
