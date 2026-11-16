# Bounded Office extraction

DOCX and XLSX extraction uses framework ZIP/XML parsers in the
[document worker](spec-document-worker.md), outside the API process.

## Supported scope and honest outcomes

- DOCX: standard transitional WordprocessingML main-body paragraphs, runs, text, tabs, text breaks and direct table/row/cell paragraphs. Citations carry the exact package part and one-based structural indices. Body paragraph indices count direct body paragraphs. Table/row/cell indices count XML siblings, not merged/rendered grid positions; paragraph indices inside a cell count its direct paragraphs. No page or original-byte locator is fabricated.
- XLSX: workbook-to-sheet relationships, unique sheet names and cell addresses, raw numeric/boolean/error/date/string values, inline/rich strings, indexed shared strings, empty cells and empty sheets. Formulas remain literal text. Stored formula caches are explicitly distinguished from absent caches. No formula is evaluated, recalculated, expanded or fetched. Dates/numbers are stored lexical values, not formatted displays; merged cells and hidden cells are not rendered.
- The admitted date lexical profile is a four-digit ISO calendar date, optionally `T` hours/minutes/seconds with one to seven fractional digits, optionally `Z` or an offset through ±14:00. Calendar/time values must validate; original text is preserved. Admitted error values are exactly `#NULL!`, `#DIV/0!`, `#VALUE!`, `#REF!`, `#NAME?`, `#NUM!`, `#N/A`, and `#GETTING_DATA`. Invalid/unadmitted values, including formula caches, are refused rather than labeled as valid dates/errors. Column addresses are limited to three letters and XFD, avoiding integer overflow aliases.
- Accepted Office output is always `Partial`: `office-body-text-only` or `office-stored-cells-only`. This is a narrow structural view, not complete customer-document coverage. Layout/styles/numbering/rendering and other unexamined metadata are not represented. Unsupported body/run/table/cell features, rich-string extensions, shared/array/data-table formulas and unsupported relationships are atomic `Unsupported`, with no earlier chunks retained. Invalid packages, XML, references and typed values are atomic `Malformed`. Budget refusals are atomic `LimitExceeded`.
- OLE compound/legacy-or-encrypted Office, encrypted ZIP, macro-enabled content types, VBA/ActiveX/macro parts, binary parts, unsupported relationships/compression and ZIP64/multi-disk archives are refused. External relationships are refused before resolution; no network or filesystem extraction occurs. DTDs/entities are prohibited and the XML resolver is null.
- Package names and relationship targets use an ASCII canonical relative-path profile. Absolute targets, `..`, percent-encoded paths, backslashes, directory entries and aliases are refused.

## Bounds and identity

`SpecOfficeExtractionLimits` pins every limit in its digest: source bytes, entry count, per-entry bytes, aggregate expanded bytes, compression ratio, XML depth/node count, chunks and actual serialized evidence bytes. Hard ceilings are 10 MiB input/evidence, 256 entries, 4 MiB per entry, 16 MiB expanded, ratio 100, depth 64, 100,000 XML nodes/attributes and 10,000 chunks. The default serialized evidence ceiling is 2 MiB.

Classic ZIP central/local headers, names, overlap, encryption flags and counts are checked before `ZipArchive` construction. Expanded entry, aggregate and ratio budgets are enforced during reads with at most one detection byte beyond the effective budget. Declared lengths and CRCs must match the extracted data; dishonest lengths can also be stopped by the framework. XML is budget-checked before constructing bounded trees. Cancellation is checked during entry reads, XML scanning and content extraction. These checks do **not** replace child-process memory/CPU/wall-time isolation.

Without descriptor bit 3, the local CRC and compressed/uncompressed sizes must exactly match the central directory. With bit 3, local fields must be zero placeholders or matching final values. Both signed and unsigned classic descriptors are supported: descriptor CRC and sizes must match, the complete descriptor must precede the central directory, and its bytes are included in the entry's nonoverlap range. Conflicting, truncated or overlapping descriptors are refused.

The default extractor refuses Office with `office-worker-required`. Office results use extractor version **3**, an independent limits digest, exact captured source digest and canonical evidence digest. Serialized evidence is capped during writes; escaping/framing/locator overhead counts, and an overflow replaces all chunks with a bounded failure envelope.

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
["braid.spec-evidence.v2", fixedDiagnosticCode,
  [ordinal, text, locatorTuple, literalFormulaOrNull], ...]
```

It is serialized by `Utf8JsonWriter` in that order; the digest covers those exact bytes. `SpecOfficeEvidenceCitation` binds an existing ordinal and typed locator to the exact extraction identity. Office result types intentionally do not expose fake byte/line properties.

`new SpecEvidenceLocator.PdfPage(page)` validates a positive one-based page; the shared writer supports `["pdf-page", page]`. See [PDF evidence](spec-pdf-native-runner.md#evidence-contract-for-consumers) for the PDF envelope and status mapping.

`SpecPdfWorkerResponse.Decode(stdout, exitCode, sourceIdentity, expectedSourceDigest, expectedSourceByteCount)` is a strict, bounded decoder for the independently implemented Rust worker. It admits exactly these fields and no duplicates/unknowns:

```text
{
  "format": "braid.spec-pdf-extraction.v1",
  "extractor": "ferrule-pdf-dddc4dce-text.v1",
  "sourceSha256": "<exact lowercase captured digest>",
  "sourceByteCount": <exact captured byte count>,
  "status": "partial|needsOcr|encrypted|malformed|limitExceeded",
  "pages": [{"page": 1, "text": "...", "missingText": false}, ...]
}
```

It requires exit code zero, at most 8 MiB captured input/stdout, 256 sequential one-based pages and 1 MiB aggregate UTF-8 text. Blank pages are retained; missingText must agree with whitespace-only text. Partial needs at least one text-bearing page; needsOcr needs one or more all-blank pages. Encrypted/malformed/limitExceeded must be page-free. Invalid UTF-8, surrogate escapes, unknown versions/statuses, source mismatch, gaps/duplicates, inconsistent states and malformed transport raise a fixed `SpecPdfWorkerProtocolException`, not a document outcome. The result retains source identity and exact stdout digest, distinct from the intake canonical-evidence digest.

## Worker boundary

`ISpecOfficeExtractionWorker` is the host boundary;
`UnavailableSpecOfficeExtractionWorker` rejects work when unavailable. Office
parsing runs in a killable child process with credential, filesystem and network
isolation, hard CPU/memory/wall-time/output limits, bounded IPC and parent
validation of source/result identity, budgets and locators.

The PDF runner verifies the configured executable build digest before launch.
It sends exact captured bytes on stdin, caps stdout while reading, discards
bounded stderr and decodes the result using the captured source facts. It kills
and settles the whole child tree on cancellation, timeout or output overflow.
See [document host configuration](spec-document-host.md) for installation settings.

## Verification command

Use the repository's pinned .NET SDK 10.0.112:

```text
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --filter "FullyQualifiedName~SpecOfficeExtractionTests|FullyQualifiedName~SpecIntakeTests|FullyQualifiedName~SpecPdfWorkerResponseTests" -m:1 -p:OpenApiGenerateDocuments=false
```

The tests create Office ZIP/XML bytes in memory, including multibyte paragraphs/table cells, sheet relationships, shared/rich/inline strings, blanks, formula caches, hostile archives, DTDs/external relationships and configured limits. They also exercise text/JSON extraction, serialization, address and lexical validation, conflicting ZIP facts, signed/unsigned descriptors and descriptor overlap.
