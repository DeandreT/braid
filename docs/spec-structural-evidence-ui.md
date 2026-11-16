# Structural evidence display (activation pending)

The display supports retained text/JSON, Office and PDF evidence. “Activation
pending” in this retained heading refers to each installation's admission, not
missing browser format support. Upload choices come from current server
capabilities; see [intake](spec-intake-ui.md) and [document admission](spec-document-intake.md).

The specification reader dispatches `weft.spec-evidence.v1` and
`weft.spec-evidence.v2`. V1 byte/line/JSON-pointer citations retain their meaning.
V2 accepts the exact closed tuples written by `SpecEvidenceLocatorJson` in the
Office extraction unit:

```text
["weft.spec-evidence.v2", diagnosticCode,
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
`Partial`; structural `Complete` and legacy text/JSON `Partial` receipts are
rejected. A successful extraction is not mapping validation or generation.

## Exact activation gaps

- Ordered migrations 0046/0047 and current schema admission supply the durable
  structural contract. They exist in source; the target database still needs
  the admitted schema and restricted roles. Browser fixtures are not proof that
  a deployment has them.
- Office/PDF uploads need optional trusted host composition, independently pinned
  installations and profiles, successful worker probes and owned shutdown.
  Capability discovery reports current admission, not a promise of future worker
  availability. The public PDF convenience factory remains closed; no request or
  configuration boolean asserts installation qualification.
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
- Local installed-worker evidence has a bounded scope. Filesystem specification
  publication refusal, production-environment acceptance and the deferred
  provider/Azure gates are not lifted by this display support.

## Verification

Use pinned Node 22.23.3 / npm 12.1.0 and locked dependencies. Run `npm run check`,
`npm run build`, and `npm run test:spec-intake-browser` with
`WEFT_CHROMIUM_EXECUTABLE` pointing to the installed Chromium when needed.
The focused lane includes parser tests and actual Chromium tests with synthetic
API responses: both evidence versions, hostile locators/fields, integer overflow,
exact multibyte UTF-8/digest/count checks, literal formulas and cache distinctions,
inert markup, mobile accessibility, and revoked-scope late responses. PDF coverage
includes the canonical intake envelope and capability/refusal behavior; see
[document intake verification](spec-document-intake.md#verification). These
fixture checks do not establish installed-worker or hosted acceptance.

### Historical structural-display checkpoint

The following is the original focused display verification record, before
capability-controlled document intake was integrated. It is not current activation
guidance; later evidence and its limits are in [v1 integration](v1-integration.md).

Focused verification on pinned Node/npm with installed Chromium 1254: 48 tests
passed, zero skipped (32 parser cases and 16 actual browser cases). Dependencies
were installed from the local cache with `npm ci --offline --ignore-scripts`.
The complete `npm run check` passed, including contract checks, browser TypeScript,
Angular development compilation and the browser-session probe build.
Production compilation passed via `npm.cmd --workspace @weft/web run build --
--output-path dist/structural-evidence-production`; the initial bundle emitted a
671.87 kB / 500 kB budget warning. PowerShell's `npm.ps1` wrapper dropped the
argument separator on the first two output-path attempts; `npm.cmd` preserved it.
