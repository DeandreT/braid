# Page-cited PDF extraction worker

`engines/spec-pdf-worker` uses Ferrule's exact `dddc4dce` public PDF layout API.
The fixed layout captures visual text once per page, including empty pages. No
caller-supplied path, URL, layout, password or executable expression is accepted.
The standalone protocol accepts a PDF on stdin and returns one bounded JSON
document on stdout. Its source SHA-256 and byte count bind the complete accepted
input. Oversized/failed stdin returns exit code 2 without a misleading partial
source digest; output failures return exit code 3.

The parser's ceilings are 8 MiB input, 256 pages, one million decoding events,
one MiB decoded text and bounded instance depth/node counts. JSON output is
bounded to 8 MiB while serializing, not after unbounded allocation. A serialization
overflow produces an atomic chunk-free `limitExceeded` outcome.

Text-bearing results are `partial`: tables, graphics, handwriting, accessibility
ordering and the semantic completeness of PDF text are not established. Empty
pages remain visible through `missingText`; all-empty documents report `needsOcr`,
which requests an accessible copy and does not prove that every page is scanned.
No OCR or external links are executed. Encryption is refused. Page numbers are
one-based page-tree order, not printed page labels or source-byte offsets.

## Activation boundary

This executable is not yet wired to intake. Native PDF parsing must run behind a
host-enforced memory/time/process/network boundary, with bounded input/output and
private temporary storage. In-process catches are not resource containment.
The host must verify the complete source identity, closed response shape and
extractor version, translate page locators into the intake evidence format, and
discard stderr rather than publishing parser diagnostics. Process failures,
timeouts and encrypted/scanned fixtures must be qualified before advertising PDF
extraction. This does not enable the currently unavailable filesystem object store.

## Checks

Use the pinned Rust toolchain and `cargo test --locked` from the worker directory.
Tests construct actual PDF page objects/streams/xref offsets and verify blank-page
conservation, exact source identity, malformed/oversized refusals and serialized
expansion. These tests do not establish sandbox or live intake qualification.
