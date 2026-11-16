# Typed mixed evidence for creation generation

`CreationSpecification` selects evidence through a closed application-layer
`CreationEvidenceDocument`. It can be created only from a genuine
`SpecExtractionResult`, `SpecOfficeExtractionResult` or `SpecPdfExtractionResult` using the corresponding
`CreationEvidenceDocument.From(...)` overload. Its constructor is private, the
class is sealed, and it has no JSON, identity/chunk, or arbitrary-byte factory.
The wrapper conveys provenance from a trusted extractor API, not document authority
or trust in the document's text.

The constructor accepting `IEnumerable<SpecExtractionResult>` adapts those typed
results. An overload accepts `IEnumerable<CreationEvidenceDocument>` for mixed selections. The exact
source/digest/format/bundle checks, 20-source limit and independent 20 MiB aggregate
captured-source/evidence limits still apply.

## Common application contract

`CreationSpecification.Extractions` is an `IReadOnlyList<CreationEvidenceDocument>`.

Each document exposes:

- `Identity: SpecExtractionIdentity`
- `Outcome: SpecExtractionOutcome` and `Code: string`
- `CopyEvidenceBytes(): byte[]`, a defensive copy of the exact original canonical bytes
- `Chunks: IReadOnlyList<CreationEvidenceChunk>`

Each common chunk exposes `Ordinal`, `Text`, `Locator: SpecEvidenceLocator` and
`Formula: string?`, plus `MissingText: bool`. MissingText is false for byte and
Office chunks, which have no PDF page missing-text flag; it is not a completeness
claim and is not inferred from empty cell text. Byte evidence retains its byte-range locator and has no formula.
Word keeps paragraph/table-cell locators. Workbook cells keep sheet/cell identity,
stored text, literal formula and the locator's cached-value flag. Byte-specific
properties such as `JsonPointer` and `StartLine` are accessed through a
`SpecEvidenceLocator.ByteRange`, not directly on the common chunk. The wrapper
does not reserialize either v1 or structural v2 evidence.

## Generation and mandatory coverage review

Generation admits only:

- `Complete` typed byte evidence for Text, JSON or OpenAPI JSON.
- `Partial` typed Office evidence for DOCX (`office-body-text-only`) or XLSX
  (`office-stored-cells-only`).
- `Partial` typed PDF page evidence (`pdf-page-text-only`).

Refusal results remain selectable in a snapshot but prevent generation before
compiler dispatch. Office is never relabeled `Complete`. All selected partials,
including uncited or empty documents, generate a critical coverage assumption.

The Word statement limits coverage to supported body paragraphs and table-cell
text; it makes no claim to complete document coverage, rendered layout, headers,
footers, drawings or page locations. The workbook statement limits coverage to
stored cells, says formulas are literal and unevaluated, warns that cached values
may be missing or stale, and makes no claim to complete workbook coverage or
calculated results. These are fixed application strings, not model output.

An assumption ID is the full lowercase SHA-256 of the canonical JSON string array
`["braid.creation-coverage.v1", singleExtractionSnapshotDigest]`. The single-source
snapshot pins the bundle, source/version/digest, format, extractor family/version,
profile, outcome and canonical evidence digest. IDs are exactly 64 ASCII alphanumeric
characters and fit the existing notes/approval grammar.

Coverage assumptions form a deterministic suffix ordered by source ID, matching
snapshot ordering. Caller assumptions with the same ID are rejected unless ID,
text and `Critical=true` all exactly match the generated assumption. Exact replay
is normalized into that same suffix. Existing model assumptions retain their
relative order. The combined result must fit the existing 64-assumption budget;
coverage never silently displaces or truncates model notes. The resulting notes
digest is included in the existing proposal fingerprint.

The existing approval service therefore requires an explicit decision for every
exact synthesized critical ID, bound to the proposal fingerprint. Critical
questions still block approval even when every coverage assumption is accepted.
Citations still refer to an existing chunk ordinal of the exact selected source,
whose extraction identity is pinned by the snapshot.

All-Complete selections reuse the original notes object without modification.

## Integration dependencies

The mixed [retained evidence codec](creation-evidence-storage.md) consumes
`.Identity` and `.CopyEvidenceBytes()` and restores through the internal
`CreationEvidenceDocument.RestoreRetained` boundary. It retains exact canonical
bytes, snapshot identity, original profile pins and generated notes.

The PDF overload is
`From(SpecPdfExtractionResult)`, admitting only PDF `Partial` with code
`pdf-page-text-only`. It copies every page chunk, including empty text pages,
with its exact ordinal, PdfPage locator, null formula and MissingText flag. Its
original five-field canonical-v2 chunk bytes are retained unchanged.

The fixed critical coverage statement is: "This PDF extraction contains
only page text. Text or page content may be missing, including pages without
usable text. OCR was not performed. It does not establish complete document
coverage or rendered layout." The existing exact-identity coverage synthesis
applies to every selected PDF Partial. `pdf-needs-ocr` and `pdf-encrypted`
remain Unsupported refusals requiring readable evidence; malformed and limit
outcomes also prevent generation.

The PDF result has a private constructor and internal Decode method;
public callers cannot mint it from worker JSON. The synchronous byte extractor
refuses PDF before JSON parsing. Integration tests execute the actual native
worker on readable/blank pages and an encrypted PDF fixture, then adapt its
source-bound output through the internal decoder.

## Focused qualification

`CreationEvidenceGenerationTests` builds real bounded DOCX/XLSX packages and calls
the actual parsers. It covers mixed generation, immutable bytes/chunks, Word/table
locators, formula/cache facts, complete-v1 compatibility, deterministic replay,
exact-identity changes, collisions, uncited empty partials, approval acknowledgement,
critical questions, refused outcomes, source/digest/citation substitutions and
source/evidence/assumption budgets. No Office extraction is fabricated as Complete.

Build the native worker with `cargo build --locked --offline --manifest-path
engines/spec-pdf-worker/Cargo.toml` and set `BRAID_TEST_PDF_EXECUTABLE` to its absolute
executable path. Run with the pinned .NET SDK 10.0.112 and locked local-cache restore:

```text
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --no-restore -p:OpenApiGenerateDocuments=false --filter "FullyQualifiedName~CreationEvidenceGenerationTests|FullyQualifiedName~MappingCreationWorkflowTests|FullyQualifiedName~SpecOfficeExtractionTests|FullyQualifiedName~SpecIntakeTests|FullyQualifiedName~SpecPdfProcessTests"
```
