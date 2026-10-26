# MFD interchange

## Application boundary

`IInterchangeBackend` is independent of transport and mapping-engine implementation.
It receives an immutable `InterchangePackage`, not paths to host files. A package
contains an exact design entry and at most 128 files, each at most 4 MiB, with at
most 16 MiB of file content altogether. UTF-8 relative names are at most 256 bytes;
absolute paths, empty/dot/parent segments, backslashes, colons and controls are
rejected. Neither package admission nor decoding accesses the filesystem/network.

The canonical package encoding uses four-byte, unsigned-range big-endian lengths
for each field: the UTF-8 string `weft.interchange-package.v1`, the design path,
the file count, and each file's path and original bytes. Files sort by **UTF-8 byte
order**, including supplementary Unicode characters; .NET UTF-16 ordinal sorting
is not equivalent. The SHA-256 digest covers that complete framing. Decoding
rejects noncanonical ordering, duplicate/missing entries and trailing bytes.

Conversion returns the exact registered backend/engine/build/adapter identity,
the input package digest, native bytes and a complete report. Native output is
bounded by the existing 4 MiB native admission ceiling. Reports allow 256 warnings
(128-byte code, 2048-byte message) and an 8192-byte summary. Warning overflow fails
conversion rather than producing an incomplete review. Transport adapters must
validate these facts before constructing an output; an echoed digest is not proof
of sandbox isolation.

## Review and confirmation

`InterchangeImportService` first authorizes the Publisher, performs conversion,
then rechecks authority before saving a server-owned snapshot. Review is not
revision publication. The snapshot binds its original actor, project, destination
metadata or append target/expected head, original package, converted native bytes,
complete report, exact engine identity and expiry. The review digest covers all
of those facts. Times are normalized to milliseconds before hashing.

Confirmation accepts only the review identity/digest and explicit warning
acknowledgement, never replacement native bytes. The idempotency key must equal
the review ID, so one review cannot create multiple revisions under different
keys. Append always carries an explicit `EqualHead` selected for review. Both
confirmation and ingestion recheck current authorization. The final ingestion
transaction checks the expected head. Confirmation does not run conversion again;
native validation uses the exact reserved engine profile, not a new deployment's
current profile.

Reviews expire for new work after one hour. Once ingestion is reserved, an exact
confirmation replay can use its retained server-owned bytes after expiry; the
existing receipt/resume workflow also recovers by operation ID.
Committed replay, lease fencing, source recovery, and head conflict behavior are
the same as native ingestion.

## Durable ownership and publication

`PostgresRevisionIngestionStore` also implements the private `IImportReviewStore`.
Review snapshots are stored as bounded private binary envelopes (32 MiB maximum)
behind the ingestion role, organization RLS and fresh original-actor authorization.
They are not public API payloads. These tables deliberately retain data rather
than implementing an implicit expiry cleanup policy.

Original provenance is a separate immutable `revision_import_provenance` row
(24 MiB maximum encoded envelope) inserted in the **same reservation transaction**
as its ingestion intent. The intent fingerprint binds its complete digest, which
includes original package bytes, native digest, reviewed report/review digests and
conversion identity. Receipt restoration checks that binding. No provenance is
attached after a revision is published.

The existing `mapping_revisions.ingestion_operation_id` foreign key publishes the
source relationship in the **same transaction** as the native revision, mapping
head and completed receipt. Original bytes are already durable before that
transaction, just as staged native bytes are. The source row cannot be updated or
deleted, and the ingestion role has no deletion/truncation grants. Source storage
does not introduce a private-object maintenance candidate that could be collected
between review and publication. Native storage/maintenance behavior is unchanged.

Receipt restoration reads facts, attempts and original provenance in one SQL
statement snapshot. Only the first ordered attempt row carries the provenance
bytes (including when no attempt exists); later rows carry only attempt data.
This prevents package-sized transfer from multiplying by the attempt count.
The restored intent fingerprint still requires the original provenance, so a
missing or changed source snapshot cannot become a native-only receipt.

The ingestion schema admission guard verifies the review/provenance tables' exact columns,
constraints, RLS, grants and transition functions. Native-only intent fingerprints
remain byte-for-byte compatible with version 1.

## Worker transport

Package conversion uses the separate `interchange` Docker/service profile:
512 MiB memory, 30-second CPU/wall limit, and the existing no-filesystem/no-network
sandbox. The `weft.ferrule-mfd.v1` request contains a length-prefixed JSON header
(at most 16 KiB) and one length-prefixed binary payload, then EOF. Its header binds
request ID, operation (`import` or `export`), input SHA-256, design path and exact
engine revision. Import payloads use the canonical package encoding; export
payloads use the unchanged 4 MiB native-document bound.

The response is a JSON header (at most 1 MiB), followed by exactly one binary frame
on success. The complete response is bounded to 18 MiB. The adapter verifies the
request identity, engine, operation, input digest, design path, payload count and
payload SHA-256 before admitting native bytes or a package. Duplicate/unknown JSON
members, trailing frames and incomplete warnings are refused. No base64 package
is embedded in inspection headers. The smaller inspection profile is unchanged.

The pinned Ferrule `MfdPackage` importer/exporter receives only in-memory entries.
Missing, unsafe or unsupported package resources fail closed without filesystem,
catalog, manifest or network fallback. Import uses Ferrule's versioned native
codec. Export includes every emitted asset, lossy warning and compatibility issue;
warning overflow refuses the conversion rather than truncating the review.

## HTTP and browser workflow

Interchange is disabled by default. Set `WEFT_MFD_INTERCHANGE_ENABLED=true` on the
server to enable its API with the configured real Docker or Ferrule-service
runner. Revision ingestion and revision report reads must also be enabled, with
their existing private storage and database configurations. The inspection-only
remote worker does not register an interchange backend. The capability endpoint
reports availability; the browser never substitutes a fake conversion or native
upload. The Ferrule service exposes its bounded `interchange` profile in its
capability document and uses a fresh sandboxed child for each request.

All resource routes use the existing scoped identity boundary. Browser sessions
require Origin and the cookie-bound request token for mutations; delegated bearer
clients use the same current project authority without cookies or CSRF headers,
and mutations pass the existing durable audit boundary. Import routes require current upload authority before
reading a package. Export routes require current revision read authority before
storage, conversion and download. Review snapshots remain original-actor private.

| Operation | Route below `/api/v1/organizations/{organizationId}/projects/{projectId}` |
| --- | --- |
| Review new mapping import | `POST /mfd-imports/review?name=…&backendId=…` |
| Review append | `POST /mappings/{mappingId}/mfd-imports/review?expectedHeadRevisionId=…` |
| Restore own import review | `GET /mfd-imports/{reviewId}` |
| Confirm import | `POST /mfd-imports/{reviewId}/confirm` |
| Review export | `POST /mappings/{mappingId}/revisions/{revisionId}/mfd-exports/review` |
| Download reviewed export | `POST /mappings/{mappingId}/revisions/{revisionId}/mfd-exports/{reviewId}/download` |

`GET /api/v1/catalog/interchange` returns availability and package limits. Import
review bodies use `application/vnd.weft.mfd-package.v1` and canonical framing.
Their route-specific Kestrel limits permit the bounded 16 MiB-content package
without changing global or native-upload limits. Unknown-length bodies are read
with a one-byte overflow probe. Compression and media parameters are refused.
Each admitted interchange operation has a 60-second work/body-read deadline and
shares a two-operation gate; expiration never implies that an in-flight native
ingestion was undone, so confirmation recovery uses the same review/key.

Confirmation/download bodies use `application/json` with exactly `reviewDigest`
and `acknowledgeWarnings`; they are limited to 1024 bytes. Duplicate, unknown or
incorrectly typed properties are refused. Import confirmation additionally
requires `Idempotency-Key` equal to the review ID. Failed review conditions return
412; disabled/unavailable feature routes return 404. Responses are `no-store`.

Export conversion runs once when creating the review. Download never reconverts:
it checks the original actor, complete review digest, warning acknowledgement,
expiry and fresh revision authority. One design downloads as `mapping.mfd`;
packages with assets download as a deterministic uncompressed `mapping.zip` with
safe relative entry names and fixed timestamps. `X-Content-Sha256` identifies the
exact bounded download bytes. The browser verifies it before saving the file.

Upload/append pages show the separate MFD review workflow only when capability is
enabled. They retain actor/scope-bound review IDs, digests and retry-state flags in session
storage, never original/native bytes or request tokens. Reload restores the
server-owned review and requires warning acknowledgement again.
Draft files and metadata are cleared when the actor/scope changes; metadata-only
drafts receive the same navigation protection as unsent files.
Same-owner access and window-focus checks preserve the mounted draft while
temporarily disabling dispatch. An unavailable session check hides the panel but
retains the unsent package in page memory, never browser storage. Fresh admission
restores it only for the same owner and resource; changing either, leaving the page,
or completing sign-out clears that retained draft. The selected-package list shows
retained files even when a file-picker control has been recreated.
A protected API refusal is classified through the
original request's session generation and identity, clearing private presentation
on a real boundary without allowing a late refusal to invalidate a newer session.
Uncertain confirmation is retried with the same review/key, not a new conversion.
It is retained as that original review, never restored as fresh input files. A
failed server-review read blocks new conversion until the original review is
recovered or its recovery is explicitly forgotten with acknowledgement.
Forgetting recovery after a confirmation attempt requires an explicit acknowledgement that
it cannot cancel or undo a saved revision. The
revision view exposes export warning review before download when enabled.

## Command-line workflow

The authenticated `weft mfd` commands use the same reviews and confirmation routes.
An import command only prepares a review; it never publishes implicitly:

```sh
weft mfd import --project PROJECT --name Orders orders.mfd \
  --design-path maps/orders.mfd --asset maps/orders-source.xsd=orders-source.xsd
weft mfd review --project PROJECT REVIEW_ID
weft mfd confirm --project PROJECT REVIEW_ID --review-digest REVIEW_DIGEST \
  --acknowledge-warnings --wait
```

For an append, replace `--name` with `--mapping MAPPING --expected-head REVISION`.
Only the design can come from stdin (`-`, with an explicit `--design-path`).
Every referenced file is explicitly selected; no directory scan or filesystem
fallback supplies omitted assets. The package uses the same file/path/total bounds
and canonical encoding as the HTTP and browser workflows.

Confirmation re-reads the original actor's review, requires the exact reviewed
digest and explicit warning consent, then uses the review ID as its only
idempotency key. `--wait` retries only that confirmation while work is pending;
without it, repeat the same confirm command to recover an uncertain operation.

```sh
weft mfd export --project PROJECT --mapping MAPPING --revision REVISION
weft mfd download --project PROJECT --mapping MAPPING --revision REVISION \
  REVIEW_ID --review-digest REVIEW_DIGEST --acknowledge-warnings --output mapping.zip
```

Export prints the complete review before a separate download. Download verifies
the bounded body and its SHA-256 before emitting stdout or atomically publishing
a new output file; it never overwrites an existing destination. `--output -`
writes only verified binary bytes to stdout. `weft mfd capabilities` reports
server availability and limits; `--json` on review commands preserves their
structured response.
