# Structural evidence display

The display supports retained text/JSON, Office and PDF evidence. Upload choices come from current server
capabilities; see [intake](spec-intake-ui.md) and [document admission](spec-document-intake.md).

The specification reader dispatches `braid.spec-evidence.v1` and
`braid.spec-evidence.v2`. V1 byte/line/JSON-pointer citations retain their meaning.
V2 accepts the exact closed tuples written by `SpecEvidenceLocatorJson`:

```text
["braid.spec-evidence.v2", diagnosticCode,
  [ordinal, text, locatorTuple, literalFormulaOrNull], ...]

["byte-range", offset, length, startLine, endLine, jsonPointerOrNull]
["docx-paragraph", part, paragraph, tableOrNull, rowOrNull, cellOrNull]
["xlsx-cell", part, sheetName, cellAddress, valueKind, hasFormulaCachedValue]
["pdf-page", oneBasedPage]
```

Office chunks use the four-field tuple above. Canonical PDF chunks add a fifth
field: `[ordinal,text,["pdf-page",oneBasedPage],null,missingText]`. The boolean is
part of intake evidence, not a browser guess about OCR. It must agree with the
trusted decoder's Unicode whitespace definition.

DOCX indices describe one-based XML structure, including direct body paragraphs
and table/row/cell paragraphs. XLSX values remain stored lexical text. Literal
formulas, absent caches, present empty-string caches and blank stored cells are
displayed distinctly. Text, formulas and sheet names use Angular interpolation;
URLs and markup remain inert. Office `Partial` is explicitly incomplete coverage.

The HTTP reader receives an ArrayBuffer, checks its actual byte count and SHA-256
against the authorized inventory, then decodes strict UTF-8 and validates the
envelope. The 10 MiB evidence ceiling includes the original serialized framing
and escaping. No reserialization is used for the digest. This bounds admission
after the browser HTTP client has buffered the response; it is not streaming
network/memory isolation. Existing current-scope/generation checks still fence
presentation after the asynchronous digest check.

Local display unions use the Domain values `Unsupported`, `Text`, `Json`,
`OpenApiJson`, `Docx`, `Xlsx` and `Pdf`. Outcomes are `Complete`, `Partial`,
`Unsupported`, `Malformed` and `LimitExceeded`. Structural success is always
`Partial`; structural `Complete` and text/JSON `Partial` receipts are rejected.

## Document admission

- Office/PDF uploads need optional trusted host composition, independently pinned
  installations and profiles, successful worker probes and owned shutdown. See
  [host configuration](spec-document-host.md).
- PDF evidence is bound to `Pdf`, v2 and page locators. The reader checks ascending
  one-based pages, at most 256 pages, at most 1 MiB extracted text and at most
  8 MiB serialized PDF evidence. `pdf-page-text-only` is Partial with some text;
  `pdf-needs-ocr` is Unsupported with retained missing-text pages;
  `pdf-encrypted` is Unsupported with no chunks. Malformed/limit refusals retain
  their exact categorical codes. Blank pages stay visible, without claiming
  scanned content, OCR success or complete document coverage.
- Extra fields and worker transport JSON are rejected. The intake envelope, not
  raw worker output, is the reader contract. Locators must match the inventory's
  source format and outcome, so PDF facts cannot be presented as Office evidence.

## Verification

Use Bun 1.4.5 and the [frozen install commands](development.md#manual-hosting-and-frontend-development). Run `bun run check`,
`bun run build`, and `bun run test:spec-intake-browser` with
`BRAID_CHROMIUM_EXECUTABLE` pointing to the installed Chromium when needed.
The focused lane includes parser tests and actual Chromium tests with synthetic
API responses: both evidence versions, hostile locators/fields, integer overflow,
exact multibyte UTF-8/digest/count checks, literal formulas and cache distinctions,
inert markup, mobile accessibility, and revoked-scope late responses. PDF coverage
includes the canonical intake envelope and capability/refusal behavior; see
[document intake verification](spec-document-intake.md#verification).
