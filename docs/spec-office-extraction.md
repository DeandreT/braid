# Bounded Office extraction unit (not activated)

This change adds Domain/Application DOCX and XLSX extraction and in-memory ZIP/XML tests. It does not enable Office uploads or run Office parsing in the API. It uses framework ZIP/XML parsers with no new dependencies.

## Supported scope and honest outcomes

- DOCX: standard transitional WordprocessingML main-body paragraphs, runs, text, tabs, text breaks and direct table/row/cell paragraphs. Citations carry the exact package part and one-based structural indices. Body paragraph indices count direct body paragraphs. Table/row/cell indices count XML siblings, not merged/rendered grid positions; paragraph indices inside a cell count its direct paragraphs. No page or original-byte locator is fabricated.
- XLSX: workbook-to-sheet relationships, unique sheet names and cell addresses, raw numeric/boolean/error/date/string values, inline/rich strings, indexed shared strings, empty cells and empty sheets. Formulas remain literal text. Stored formula caches are explicitly distinguished from absent caches. No formula is evaluated, recalculated, expanded or fetched. Dates/numbers are stored lexical values, not formatted displays; merged cells and hidden cells are not rendered.
- The admitted date lexical profile is a four-digit ISO calendar date, optionally `T` hours/minutes/seconds with one to seven fractional digits, optionally `Z` or an offset through ±14:00. Calendar/time values must validate; original text is preserved. Admitted error values are exactly `#NULL!`, `#DIV/0!`, `#VALUE!`, `#REF!`, `#NAME?`, `#NUM!`, `#N/A`, and `#GETTING_DATA`. Invalid/unadmitted values, including formula caches, are refused rather than labeled as valid dates/errors. Column addresses are limited to three letters and XFD, avoiding integer overflow aliases.
- Accepted Office output is always `Partial`: `office-body-text-only` or `office-stored-cells-only`. This is a narrow structural view, not complete customer-document coverage. Layout/styles/numbering/rendering and other unexamined metadata are not represented. Unsupported body/run/table/cell features, rich-string extensions, shared/array/data-table formulas and unsupported relationships are atomic `Unsupported`, with no earlier chunks retained. Invalid packages, XML, references and typed values are atomic `Malformed`. Budget refusals are atomic `LimitExceeded`.
- OLE compound/legacy-or-encrypted Office, encrypted ZIP, macro-enabled content types, VBA/ActiveX/macro parts, binary parts, unsupported relationships/compression and ZIP64/multi-disk archives are refused. External relationships are refused before resolution; no network or filesystem extraction occurs. DTDs/entities are prohibited and the XML resolver is null.
- Package names and relationship targets use a deliberately narrow ASCII canonical relative-path profile. Absolute targets, `..`, percent-encoded paths, backslashes, directory entries and aliases are refused. This excludes some valid producer variants; it must not be presented as arbitrary Office compatibility. Normal Word/Excel package layouts are covered by actual in-memory fixtures, not a full producer conformance suite.

## Bounds and identity

`SpecOfficeExtractionLimits` pins every limit in its digest: source bytes, entry count, per-entry bytes, aggregate expanded bytes, compression ratio, XML depth/node count, chunks and actual serialized evidence bytes. Hard ceilings are 10 MiB input/evidence, 256 entries, 4 MiB per entry, 16 MiB expanded, ratio 100, depth 64, 100,000 XML nodes/attributes and 10,000 chunks. The default serialized evidence ceiling is 2 MiB.

Classic ZIP central/local headers, names, overlap, encryption flags and counts are checked before `ZipArchive` construction. Expanded entry, aggregate and ratio budgets are enforced during reads with at most one detection byte beyond the effective budget. Declared lengths and CRCs must match the extracted data; dishonest lengths can also be stopped by the framework. XML is budget-checked before constructing bounded trees. Cancellation is checked during entry reads, XML scanning and content extraction. These checks do **not** replace child-process memory/CPU/wall-time isolation.

Without descriptor bit 3, the local CRC and compressed/uncompressed sizes must exactly match the central directory. With bit 3, local fields must be zero placeholders or matching final values. Both signed and unsigned classic descriptors are supported: descriptor CRC and sizes must match, the complete descriptor must precede the central directory, and its bytes are included in the entry's nonoverlap range. Conflicting, truncated or overlapping descriptors are refused.

V1 text/JSON behavior and canonical serialization remain unchanged, including the current atomic physical serialization cap. The default extractor refuses Office with `office-worker-required`. Office results use extractor version **3**, an independent limits digest, exact captured source digest and canonical evidence digest. Serialized evidence is capped during writes; escaping/framing/locator overhead counts, and an overflow replaces all chunks with a bounded failure envelope.

## Closed locator and evidence contract

`SpecEvidenceLocator` has explicit ByteRange, WordParagraph, SheetCell and PdfPage variants. `SpecEvidenceLocatorJson.Write` writes canonical v2 tuples:

```text
["byte-range", offset, length, startLine, endLine, jsonPointerOrNull]
["docx-paragraph", part, paragraph, tableOrNull, rowOrNull, cellOrNull]
["xlsx-cell", part, sheetName, cellAddress, valueKind, hasFormulaCachedValue]
["pdf-page", oneBasedPage]
```

The Office envelope is:

```text
["weft.spec-evidence.v2", fixedDiagnosticCode,
  [ordinal, text, locatorTuple, literalFormulaOrNull], ...]
```

It is serialized by `Utf8JsonWriter` in that order; the digest covers those exact bytes. `SpecOfficeEvidenceCitation` binds an existing ordinal and typed locator to the exact extraction identity. Office result types intentionally do not expose fake byte/line properties.

For the coordinator's PDF adapter, `new SpecEvidenceLocator.PdfPage(page)` validates a positive one-based page; the shared writer already supports `["pdf-page", page]`. The PDF adapter still needs its own source format/status and evidence envelope integration. A page locator conveys neither OCR success nor text coverage. This change contains no PDF parsing or worker implementation.

`SpecPdfWorkerResponse.Decode(stdout, exitCode, sourceIdentity, expectedSourceDigest, expectedSourceByteCount)` is a strict, bounded decoder for the independently implemented Rust worker. It admits exactly these fields and no duplicates/unknowns:

```text
{
  "format": "weft.spec-pdf-extraction.v1",
  "extractor": "ferrule-pdf-dddc4dce-text.v1",
  "sourceSha256": "<exact lowercase captured digest>",
  "sourceByteCount": <exact captured byte count>,
  "status": "partial|needsOcr|encrypted|malformed|limitExceeded",
  "pages": [{"page": 1, "text": "...", "missingText": false}, ...]
}
```

It requires exit code zero, at most 8 MiB captured input/stdout, 256 sequential one-based pages and 1 MiB aggregate UTF-8 text. Blank pages are retained; missingText must agree with whitespace-only text. Partial needs at least one text-bearing page; needsOcr needs one or more all-blank pages. Encrypted/malformed/limitExceeded must be page-free. Invalid UTF-8, surrogate escapes, unknown versions/statuses, source mismatch, gaps/duplicates, inconsistent states and malformed transport raise a fixed `SpecPdfWorkerProtocolException`, not a fabricated document outcome. The result retains source identity and exact stdout digest, which is explicitly not an intake canonical-evidence digest. Tests here qualify the decoder with controlled protocol bytes; actual PDF/process qualification remains with the separate Rust worker.

## Activation gaps

`ISpecOfficeExtractionWorker` is the explicit host boundary; `UnavailableSpecOfficeExtractionWorker` is the fail-closed implementation. `SpecOfficeParser` is internal, exposed to the existing test friend assembly only, with no DI registration or API call path. A later worker project needs a deliberate accessibility/integration change; do not make the parser public and invoke it on the API thread as a shortcut.

Production activation requires a separately qualified, killable child process with credential/filesystem/network isolation, hard CPU/memory/wall-time/output limits, bounded IPC and strict parent validation of source/result identity, budgets and locators. For the PDF bridge, send the exact captured PDF bytes to a pinned executable on stdin, close stdin, cap stdout while reading, discard bounded stderr, require successful exit and decode stdout using the exact captured source facts. No caller-controlled executable/path/layout/password/URL is accepted. Kill and settle the whole child tree on cancellation, timeout or output overflow before admitting any result. Neither interface flags nor a successful decoder result qualify a runner. No runner, API activation or process-isolation qualification is supplied here.

Reported contract gaps: API upload admission currently permits only text/JSON/OpenAPI JSON; registry/database enum/check constraints, HTTP/OpenAPI contracts, generated clients and UI status/evidence parsers need explicit support for Docx/Xlsx, Partial and v2 structural locators. Keep v1 readers/version routing intact. No SQL, API, UI or generated-contract edits are included. Office runtime admission must remain unavailable until the ordered schema/backend and qualified worker are admitted.

The follow-up schema migration must preserve immutable `0044`; use ordered `0046` for Office/PDF admission if `0045` is reserved for creation changes. It must cover persisted format/outcome constraints and the new evidence/extractor identities alongside explicit PDF format/status decisions. The API/OpenAPI, regenerated types and UI must admit those exact formats/outcomes and distinguish v1 byte citations from v2 structural citations before enabling uploads. Migration presence alone is not worker qualification.

The PDF runner must independently pin and verify a configured approved executable **build digest** before execution. `weft.spec-pdf-extraction.v1` and `ferrule-pdf-dddc4dce-text.v1` remain wire checks, not proof that the executable includes later page-tree/alias fixes. This decoder cannot establish executable provenance; admission remains disabled until the runner provides that verification and resource containment.

## Verification command

Use the repository's pinned .NET SDK 10.0.112:

```text
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj --filter "FullyQualifiedName~SpecOfficeExtractionTests|FullyQualifiedName~SpecIntakeTests|FullyQualifiedName~SpecPdfWorkerResponseTests" -m:1 -p:OpenApiGenerateDocuments=false
```

The tests create actual Office ZIP/XML bytes in memory, including multibyte paragraphs/table cells, sheet relationships, shared/rich/inline strings, blanks, formula caches, hostile archives, DTDs/external relationships and configured limits. They also exercise the existing text/JSON extraction and serialization regressions. They do not qualify API/storage activation or the future Office child-process host.

Latest review-fix result: 141 tests passed, zero failed/skipped, on pinned SDK 10.0.112. This includes the original 104 cases plus address-overflow, date/error/cache lexical validation, conflicting local ZIP facts, signed/unsigned descriptors and descriptor-overlap regressions. Twenty cases exercise PDF transport decoding; no PDF parser/process test is claimed by this branch. The initial unrestricted parallel build timed out; the serial build/test command above completed successfully.
