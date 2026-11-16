# Project-owned authored preview transport

`FerruleInspectionBackend.PreviewAuthoredAsync` executes retained native bytes and
one sample through `braid.ferrule-authored-preview.v1`. The request binds a genuine
project, authoring identity, proposal fingerprint and native/sample digests. The
compiler and preview runner use distinct fixed profiles.

`FerruleMappingCreationPreview` adapts verified output into the creation workflow.
`MappingCreationPreviewService` checks current author/content authority and exact
specification identity before and after execution. It exposes output only when
the entire expected subject matches. Successful evidence includes a closed
format/media-type pair and output digest. Failure contains no output document.

## Wire and resource limits

The request has three big-endian uint32-prefixed frames: a JSON header (16 KiB),
retained native document (4 MiB), and sample (1 MiB), followed by EOF. The success
reply has a header and one output frame (1 MiB); categorical failures have only a
header. Unknown or missing fields, duplicate JSON properties, mismatched request
echoes, engine identity, output digest/count, extra frames and trailing bytes are
refused. The parser preserves empty successful CSV/TSV output as distinct from a
failed invocation.

The closed MIME types are `application/json`, `application/xml`, `text/csv`,
`text/tab-separated-values`, `application/edi-x12` and `application/edifact`, paired
with their corresponding formats. Format selection comes from the native
boundaries, never a sample filename.

The `authored-preview` service profile is disabled by default. Its setting is
`BRAID_FERRULE_SERVICE_AUTHORED_PREVIEW_ENABLED=true`. Service routing rejects this
protocol on other profiles, including duplicate protocol declarations. Its fixed
process/container policy is 512 MiB, 30 CPU seconds and 30 seconds wall time, using
the existing hardened Ferrule process runner.

## Verification

The portable interop fixtures launch the actual pinned Rust authoring and authored
preview protocol executables. Configure `BRAID_AUTHORING_TEST_EXECUTABLE` and
`BRAID_AUTHORED_PREVIEW_TEST_EXECUTABLE` to run them. They compile fresh mappings and
execute JSON samples into JSON, CSV, TSV and XML, checking escaped output and
malformed-input refusals. Application-level coverage generates and previews a
multi-binding proposal through the real adapters.

Transcript tests cover identity/digest/frame/format substitution, omitted and
duplicate fields, cancellation, byte limits and cross-profile refusal. The Rust
execution matrix covers all six formats under the documented schema/operator restrictions.
