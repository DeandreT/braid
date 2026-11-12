# Document evidence persistence (0046)

Worktree: `C:/example/checkout`, based on qualified `d1105cd0`
and integration checkpoint `996f2cd4`, with reviewed codec changes from `0d318905`
applied before mixed-document qualification. Migration scripts 0001–0045 are immutable.

## Registry and approval pins

0046 admits source formats DOCX=4, XLSX=5 and PDF=6. Partial=5 extraction
outcomes are restricted to Office extractor version 3 for formats 4/5, or PDF
extractor version 4 for format 6. Existing Complete outcomes retain their prior
semantics. Registry objects now carry their source format under a composite
foreign key to the exact source version. Existing objects are backfilled before
the non-null and FK constraints are installed.

Creation source rows retain extraction outcome as well as original/extraction
object IDs, format, source identity/digest/count, extractor/profile and evidence
digest/count. Existing rows receive Complete=1. Selection and both approval and
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

The runtime owner supplies Application discovery ports and PostgreSQL adapter
methods. Spec inventory projection and stored extractor/profile transport remain
under that owner's coordination.

## Qualification

Native PostgreSQL qualification passed across the main run and corrective retry.
The Release main run (`document-regressions.trx`) passed 98/99 cases with zero
skips. Its remaining Specification disclosure fixture was corrected to use a
workspace conversation and atomically update its ledger counter and event.
The final retry (`document-audit-inventory-final.trx`) passed 25/25 with zero
skips, including that audit case and 24 catalog/build/release inventory cases.
Together these receipts cover 123 distinct passing cases; this is not a claim
of a single clean 123-case run. Receipts are in
`C:/example/evidence/document-persistence-checks`.

Qualification used .NET SDK 10.0.112, native PostgreSQL 16.15, and the real
`weft-spec-pdf-worker.exe` child for the mixed-document case. Coverage includes
populated 44→45→46 upgrade, retention/restoration/approval, source retirement,
authority revocation, format foreign keys, admission drift, restricted discovery
ACLs, and UUID keysets.

SQL-only fixture payload/evidence
bytes are explicitly synthetic trusted-writer inputs. A separate mixed-document
test parses actual DOCX/XLSX packages and invokes the real PDF child process,
publishes their exact extraction identities through the restricted registry,
then retains/restores a typed-v2 proposal and approves its Partial coverage.
That test uses synthetic compiler bytes and a synthetic physical-publication
callback; it qualifies real document restoration and database boundaries, not
native mapping execution or physical storage.
## Integrated runtime qualification

Reviewed runtime `c2ba6f56306631766f23b834286c602176a9579f` and discovery
follow-up `c761ed442ddee7a2366b05e42a2089463c07ca42` were applied locally.
The coordinator reported independent static review clear for SQL/admission,
historical migration immutability, structural pins, discovery, five rounds,
kind-12 citations and source-5 audit. The runtime follow-up resolves physical
store admission for retained proposal discovery while preserving current content
authority and selection-store checks.

0046 admits five provider rounds and workspace citation kind 12 with exactly
the canonical proposal ID and lowercase SHA-256 `proposalFingerprint` shape.
Legacy citation shapes retain their closed key sets. Exact chat admission checks
both constraints and the effective metadata validator body.

Release/native receipts in the directory above:

- `document-creation-chat-contract.trx`: 14 passed, zero skips.
- `document-runtime-integrated.trx`: 85 passed, two failures, four skips.
  Failures were the missing native compiler fixture path and the new typed test's
  inconsistent provider/turn budgets. Three HTTP session cases require Linux;
  the fourth skip was the compiler-adapter case subsequently qualified below.
- `document-runtime-discovery-final.trx`: 19 passed, one failed, zero skips.
  Both actual PostgreSQL inactive/relocated-store discovery cases passed,
  including content revocation. The remaining typed test incorrectly changed
  worker instance IDs between rounds and was corrected to preserve one instance.
- `document-native-codec-final.trx`: two passed, zero skips, using the built
  native authoring fixture and real PDF worker for Office/PDF restoration and
  the runtime compiler adapter.
- `document-typed-chat-final.trx`: one passed, zero skips. Actual typed kind-12
  citation and source-5 disclosure append/read/replay passed with five ordinal
  reservations, one audit row, and unauthorized-reader refusal.

These are separate receipts with overlapping cases, not a single clean run.
Production admission and live-provider/Azure acceptance remain deferred.
