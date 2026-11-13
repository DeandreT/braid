# Flat CSV documents

`weft.csv.flat-document.v1` identifies Weft's primary flat CSV document capability.
The direct Ferrule preview protocol is `weft.ferrule-csv-preview.v1`; its C# adapter
uses `PreviewExecutionProfile.CsvDocument` and the separate
`weft.ferrule-csv-preview-adapter.v1` identity. Source generation uses
`weft.ferrule-csharp-csv.v1`. These are separate admission contracts, not aliases
for legacy JSON or JSON/EDI document execution.

## Boundaries and settings

Both primary boundaries must explicitly save `tabular_kind: "csv"`. A saved tab
delimiter gives the boundary TSV identity and `text/tab-separated-values` media;
other admitted delimiters give CSV identity and `text/csv`. Design-time paths,
uploaded extensions and projection recognition do not select a codec.

Admission uses Ferrule's `CsvDocumentBoundaryPolicy::from_project` and
`validate_csv_document_boundary`. The accepted schema is a closed, non-repeating
row group with scalar columns and the metadata supported by that policy. The root
mapping produces a repeated row collection. Named inputs/outputs, mixed-format
plans, dynamic documents, unresolved text repair, conflicting options and other
unsupported programs are refused before input parsing.

Saved delimiter, quote, disabled quoting, header, UTF-8 BOM and empty-string
settings are retained. Preview output includes resolved `csvDialect` metadata
with schema `weft.csv-dialect.v1`: delimiter, nullable quote, quote-disabled flag,
header flag, BOM flag and empty-string flag. Media must agree with that dialect.
The metadata is presentation information, not permission to reinterpret input or
alter returned bytes.

Input uses strict UTF-8, with an optional UTF-8 BOM. Headers are skipped by
position, not matched by name. Missing trailing cells are null. Present empty
text cells are null unless `csv_preserve_empty_strings` is selected; numeric and
boolean empty cells remain null. An explicit zero-byte frame is an empty document,
not a missing input. With headers disabled, zero output rows can produce an
explicit zero-byte output frame with the digest of the empty byte string.

## Execution and limits

Only the CSV preview profile selects `PayloadRunOptions::with_bounded_documents`.
Legacy JSON/EDI execution retains its established route, including JSON schema
provenance acceptance. No C ABI or secondary mapping implementation is involved.
CSV parsing and serialization use the pinned native codecs; Weft also validates
output against the bounded CSV reader before publication.

Native ceilings are 8 MiB per document, 1 MiB per cell, 100,000 physical records
(including a skipped header), 256 columns and 1,000,000 materialized nodes. Weft's
requested aggregate input/output, artifact and diagnostic ceilings remain in force
and may be smaller. The worker process still owns CPU, memory and deadline limits.
Failures publish no partial output and do not trigger mutation retries.

Generation invokes `emit_with_csv_document_adapters` with the admitted policy.
The generated entry point is `ExecuteCsvDocumentBytes(byte[])`, with a separate
host-context overload. The compiler source contract admits exactly
`GeneratedMapping.Csv.cs` and `GeneratedMapping.CsvDocument.cs` as CSV companions,
not arbitrary similarly named files.

### Browser table display

The workbench uses recorded `weft.csv-dialect.v1` metadata to offer **Table** and
**Raw text** views. It does not infer a dialect from a filename or payload. Header
rows follow the saved setting; displayed cell strings are not trimmed, coerced
to types or padded to a common row width.

Browser parsing has separate, smaller ceilings: 1 MiB per document, 64 KiB of
encoded content per cell, 10,000 records including a declared header, 256 columns
and 100,000 nodes (one per record and per cell). It checks the full document but
shows at most 200 data rows. These ceilings do not replace the native execution
limits above. Missing or invalid dialect metadata, invalid UTF-8/CSV, media/dialect
disagreement or a display-limit refusal leaves a bounded raw text or hex view
with the reason shown. Raw text is limited to the first 64 KiB; hex fallback to
the first 1 KiB. Display never rewrites the full bytes used for downloads or
regression comparison.

## Persisted previews and regression cases

The host enables CSV previews explicitly with `WEFT_PREVIEWS_CSV_ENABLED=true`
alongside `WEFT_PREVIEWS_ENABLED=true`. Catalog submission then selects CSV with
profile `weft.ferrule-csv-preview.v1`; the CLI equivalent is `previews create --csv`.
The first and only input must be present, but its base64 content may be empty.
The server reads the pinned revision's native options, not a caller-supplied
dialect. Primary and all-target selection are supported for the primary-only plan.
Legacy JSON and JSON/EDI profiles retain their existing nonempty-input admission.

Migration `0041_csv_documents.sql` adds the private storage contract after the
unchanged mapping draft and chat write migrations. `weft.csv-preview-request.v1`
records explicit presence and the resolved input dialect. The retained output
descriptor includes the exact output dialect, media, byte count and SHA-256.
CSV and TSV downloads return the recorded media and unchanged bytes.

CSV preview submission targets a saved revision. Save an editor draft as a revision
before selecting the CSV profile; existing JSON and document draft previews retain
their checkpoint subjects. The CSV suffix preserves those private checkpoint rows
and uses subject-aware authority for input and output metadata reads.

In **Try sample**, the offered admission option is **Explicit CSV / TSV (saved
native dialect)**. An empty text input or zero-byte file is an explicit primary
input. For a successful current submission, publishers and administrators with
CSV regression publication available can choose **Save preview as test case**
after naming the case and explicitly accepting the exact output as its baseline.
The save retains the verified output bytes and dialect, original input,
parameters, evaluation time and runtime. It selects exact-byte comparison; it
does not automatically approve a baseline or a build.

CSV regression cases use `weft.csv-regression-case.v1`; completed observations
use `weft.csv-regression-run-result.v1`. Both input and output dialects are pinned
to the same native revision. `weft.regression.csv-rows.v1` compares logical records
and cell strings in order, including headers and whitespace. Quoting and record
terminator syntax are not themselves differences; embedded cell line breaks are.
A missing trailing cell differs from an empty cell. UTF-8 is strict, an initial
BOM is optional, and diagnostics identify a one-based row and column without
including cell values. Exact-byte comparison remains available.

A CSV case requires one primary expected-output document with its native dialect.
That document may contain zero bytes and still be a real baseline. An absent
expected document is not an empty CSV document and cannot produce a passing
comparison. In the browser, start with a saved preview baseline or edit an
existing CSV case so the expected dialect remains tied to the native revision.

## Host composition and compiled execution

CSV regression publication and execution require `WEFT_REGRESSIONS_CSV_ENABLED=true`
on the API and regression worker respectively. The default is off. Both select the
installed CSV adapter against the same configured preview producer identity.

CSV build controls are explicitly enabled by `WEFT_BUILDS_CSV_ENABLED`; the
configured native/compiler identities and transport remain independently checked.
The compiled protocol is `weft.ferrule-compiled-csv.v1` and invokes the generated
CSV byte entry point. Build boundary descriptors retain `csvDialect`; compiled
document descriptors use `name`, `byteCount`, `sha256`, `mediaType`, `csvDialect`.
Legacy descriptors omit the dialect member and keep their existing wire form.

CSV HTTP native reads require the existing qualified native-metadata and private
storage ports. Reporting/ingestion registrations remain authoritative; build-only
hosts with CSV builds enabled alias their existing read ports without opening storage or transport during
capability reads. Role, revision, source identity and storage-integrity checks
remain required around actual native reads. No additional credentials or fallback
native source are inferred.

CSV preview/regression configuration refuses startup unless both unkeyed
`IPrivateObjectStore` and `IRevisionReportMetadataReader` ports are registered.
Use configured report sources, existing CSV-build source aliases, or explicitly
composed qualified native sources. Ingestion storage alone is not a metadata
source. Builds and reports are never enabled implicitly. These checks inspect
registration metadata only, including Session composition, and do not resolve
source factories, open credential files, or create workers. With only legacy
previews enabled, an explicit CSV submission is rejected before source access;
JSON/EDI admission is unchanged.

Build evidence and qualification still require exact bytes for all declared
targets. CSV row comparison is never accepted as build proof. Canonical SQL binds
both dialects to the build intent and verifies the exact output media, digest and
length. Source implementation and test definitions are not runtime qualification;
the migration, host composition and execution gates must be exercised in a
qualified environment before deployment.

The self-authored `csv-flat.native.json` fixture and CSV document tests cover the
native contract and generated companion selection. The existing
`ServiceEquivalenceFact` gate owns the Docker/service-child byte and dialect parity
test. These definitions do not themselves establish runtime qualification.
