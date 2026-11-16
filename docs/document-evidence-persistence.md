# Document evidence persistence

## Registry and approval pins

The registry admits source formats DOCX=4, XLSX=5 and PDF=6. Partial=5 extraction
outcomes are restricted to Office extractor version 3 for formats 4/5, or PDF
extractor version 4 for format 6. Registry objects carry their source format
under a composite foreign key to the exact source version.

Creation source rows retain extraction outcome as well as original/extraction
object IDs, format, source identity/digest/count, extractor/profile and evidence
digest/count. Selection and both approval and
ingestion publication fences compare the pinned outcome; later state, digest,
profile or authority changes deny execution. Restoration of canonical structural
content is supplied by the separate codec implementation.

Specification disclosure uses `chat_payload_audit.source_kind=5`, admitted by
the actual table constraint and the exact chat schema contract.

## Durable discovery interface

`creation_proposals_discover_v1(actor uuid,org uuid,project uuid,after_proposal uuid,"limit" integer)`
returns `(proposal_id uuid,fingerprint text)`. It requires current content
authority, limits results to the requesting author and exact project, uses
exclusive ascending proposal-ID keyset pagination, and accepts 1–20 rows.
It includes approved/terminal proposals and preserves discoverability after
source retirement. Only committed retention is visible to another connection.

`spec_selection_discover_v1(actor uuid,org uuid,project uuid,store uuid,
after_bundle uuid,after_object uuid,"limit" integer)` returns
`(bundle_id uuid,extraction_id uuid)`. It requires current content authority,
an active store, live bundle/source, and published matching originals and
extractions in that exact store. Complete and correctly typed Partial evidence
are eligible. Ordering is exclusive ascending PostgreSQL `(bundle_id,extraction_id)`.
Cursor components must be both null or both populated; limits are 1–20.

Both functions reject zero identifiers and grant execution only to the spec
reader role, matching `PostgresCreationDiscovery` and its store admission. Returned
IDs and canonical fingerprints allow exact authorized reads after cancellation
or lost events. Discovery is independent of a surviving chat turn. Pages are
current MVCC observations, not a retained pagination snapshot.

## Chat contract

The chat contract admits five provider rounds and workspace citation kind 12 with exactly
the canonical proposal ID and lowercase SHA-256 `proposalFingerprint` shape.
Exact chat admission checks both constraints and the effective metadata validator body.
