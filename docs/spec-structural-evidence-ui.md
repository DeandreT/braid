# Structural evidence display (activation pending)

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

Local display unions use the reviewed Domain values: `Unsupported`, `Text`,
`Json`, `OpenApiJson`, `Docx`, `Xlsx`; outcomes add `Partial` to the existing four.
The API upload options and receipt validation remain text/JSON/OpenAPI JSON.

## Exact activation gaps

- Office publication requires the ordered database constraint/admission changes,
  backend orchestration, and a qualified isolated worker. Current upload admission
  and migration 0044 do not support Office. Browser Office fixtures simulate
  future retained evidence, not successful Office uploads.
- The shared PDF page tuple is parsed and has a presentation branch, but the
  Domain has no PDF source-format value or intake OCR/encryption outcomes. The
  inventory reader therefore rejects `Pdf` and worker-only statuses. Current
  source-format binding prevents PDF tuples from being presented as another
  source format. PDF activation needs a canonical intake envelope and durable
  format/status contract in addition to the qualified runner.
- `missingText` exists only in the separate Rust worker transport. It is not a
  field of a v2 locator/chunk, and extra fields are rejected. The page template
  can describe an empty/whitespace text chunk without inferring scanned content,
  OCR success, missing pages, or complete coverage. The worker JSON is not intake
  evidence and is rejected by this reader.

## Verification

Use pinned Node 22.23.3 / npm 12.1.0 and locked dependencies. Run `npm run check`,
`npm run build`, and `npm run test:spec-intake-browser` with
`WEFT_CHROMIUM_EXECUTABLE` pointing to the installed Chromium when needed.
The focused lane includes parser tests and actual Chromium tests with synthetic
API responses: both evidence versions, hostile locators/fields, integer overflow,
exact multibyte UTF-8/digest/count checks, literal formulas and cache distinctions,
inert markup, mobile accessibility, and revoked-scope late responses. PDF tuple
coverage is parser-level until its intake source contract is available.

Focused verification on pinned Node/npm with installed Chromium 1254: 48 tests
passed, zero skipped (32 parser cases and 16 actual browser cases). Dependencies
were installed from the local cache with `npm ci --offline --ignore-scripts`.
The complete `npm run check` passed, including contract checks, browser TypeScript,
Angular development compilation and the browser-session probe build.
Production compilation passed via `npm.cmd --workspace @weft/web run build --
--output-path dist/structural-evidence-production`; the initial bundle emitted a
671.87 kB / 500 kB budget warning. PowerShell's `npm.ps1` wrapper dropped the
argument separator on the first two output-path attempts; `npm.cmd` preserved it.
