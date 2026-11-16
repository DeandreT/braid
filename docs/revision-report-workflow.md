# Persistent revision-report workflow

`RevisionReportService` implements the Application reader and deriver ports over
the scoped metadata reader/store, a pure profile registry and a lazy private-store
registration. The native revision remains authoritative. See the
[persisted-report API](persisted-report-api.md) for HTTP routes.

## Reads and cache identity

Current reads select a key from the authorized native source, trusted current
backend profile, report format and effective policy. Retained reads use the exact
requested derivation and its original key; they do not upgrade its profile or
resolve a live backend. Supported historical reports still undergo pure format
checks and the intersection of immutable and current read limits.

A usable hit requires the selected object, verified bytes, exact digest/count,
strict envelope, provenance and complete metadata binding. Pending, failed and
not-derived metadata need no object access. Reads never claim, renew, repair or
invoke Ferrule. False validation is a usable report of findings; explanation
coverage remains independent of validity.

Strict Ferrule normalization preserves every partial-coverage gap and document
diagnostic. Its navigation links must resolve to the same uniquely emitted
native origin. A schema subproperty pointing at an enclosing field retains its
native evidence without claiming an exact row link.

Optional validation links use the explicitly requested, independently verified
retained projection. Both reports must describe the same native source and
backend profile. Only unique native origins receive links; the stored validation
report remains unlinked, and the linked response owns its data.

## Derivation and repair

Ensure first observes durable state. A verified cached result needs no work slot;
a live claim or retry delay returns the corresponding state. Accepted new work
rereads state before claiming, checks trusted native limits before resolving
storage, verifies the unchanged native bytes and dispatches the selected backend.

A usable backend result is normalized, encoded and published create-only at the
new database-allocated candidate object ID. Unexpected `AlreadyPresent` is
refused, including matching bytes. Exact verified readback and the codec's void
`VerifyCandidate` proof precede fenced metadata completion. That proof compares
the whole expected payload; it does not manufacture committed metadata. Only an
acknowledged or exactly recovered immutable completion can become a report hit.

Repair requires the requested successful derivation to equal the current
selection before every shortcut. A healthy exact selection follows the read
path. Only proven selected-object absence or integrity failure admits repair
work, followed by another selection check and the full metadata compare-and-swap.
A replacement uses a new attempt, generation and object ID. Old success facts,
objects and unpublished candidates remain retained; the workflow does not delete
them or adopt an orphan from an earlier process.

## Authority, capacity and recovery

Composition shares one service instance through both ports. Its fixed gates admit
one work operation and two reads without waiting. An optional projection lookup
shares its caller's admitted slot. Owned work delivers its acknowledged report
under its existing work slot, independently of the public read capacity.
Fixed read/work deadlines are 30 seconds; metadata
cleanup and ambiguous-completion resolution have a separate five-second budget.
The database lease is 60 seconds without renewal, and retry delay uses its
30-second database clock policy. Existing worker limits remain unchanged.

Fresh source and present authority checks precede each object operation,
dispatch, publication/readback and final delivery. Metadata transactions finish
before external I/O. These observations do not lock membership through I/O.

Cancellation settles the owned worker before metadata cleanup and preserves
actual isolation failure and runner quarantine. Ambiguous completion resolves
the original retained attempt and compares its complete key, generation, lease,
repair lineage, observation and object reference. Another current selection
cannot stand in for that attempt. Caller cancellation remains observable even
when an immutable completion was committed. Unresolved ownership, expired
leases or lost authority leave durable facts for later explicit recovery.

The test-only workflow probe exercises process death before completion and after
acknowledgment, plus independent read-only reopening. Test checkpoints and fault
decorators stay outside the production service.
