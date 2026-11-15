# Page-cited PDF extraction worker

The separately scoped native host/evidence unit is described in [spec-pdf-native-runner.md](spec-pdf-native-runner.md); its current changes await independent review and Linux qualification.

`engines/spec-pdf-worker` uses Ferrule's exact `238369bf` public PDF layout API.
The fixed layout captures visual text once per page, including empty pages. No
caller-supplied path, URL, layout, password or executable expression is accepted.
The standalone protocol accepts a PDF on stdin and returns one bounded JSON
document on stdout. Its source SHA-256 and byte count bind the complete accepted
input. Oversized/failed stdin returns exit code 2 without a misleading partial
source digest; output failures return exit code 3.

The input ceiling is 8 MiB. A separate complete-page-tree check refuses invalid
children, duplicate references, cycles, mismatched parent/count facts and trees
over 64 levels or 1,024 nodes before assigning citations. Extraction limits are
256 pages, one million collection events, one MiB decoded text and bounded output
instance depth/node counts. These are not parser-wide memory/time ceilings:
document loading, decompression and font decoding can allocate before collection
limits apply. External process containment remains mandatory. JSON output is
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
discard stderr rather than publishing parser diagnostics. Host process failures,
timeouts and representative customer files must be qualified before advertising
PDF extraction. This does not enable the currently unavailable filesystem object store.

## Checks

Use the pinned Rust toolchain and `cargo test --locked` from the worker directory.
Tests construct actual PDF page objects/streams/xref offsets and verify blank-page
conservation, exact source identity, malformed/oversized refusals and serialized
expansion. Actual encrypted and image-only PDF fixtures exercise refusal and
missing-text outcomes. Eleven tests and all-target Clippy with warnings denied
pass on the pinned Windows Rust toolchain. These checks do not establish sandbox
or live intake qualification.
