# Native PDF runner and canonical evidence

The native runner executes the Rust PDF parser with process isolation and strict
parent validation. See [document host configuration](spec-document-host.md) for
installation paths and executable/profile pins.

## Evidence contract for consumers

`SpecSourceFormat.Pdf = 6`. `SpecSourceCapture` caps PDF capture at 8 MiB even when the caller uses the larger generic default; over-limit capture consumes at most one detection byte and returns no source. Other capture formats retain their bounds.

`SpecPdfExtractionResult` exposes `Identity`, `Outcome`, `Code`, `Chunks`, `Cite(ordinal)` and `CopyEvidenceBytes()`. Its constructor is private and its `Decode` factory is internal, available to trusted Application code and the MappingBackends friend assembly. It uses the strict `SpecPdfWorkerResponse` decoder to bind bounded stdout to the captured source digest/count and source identity.

The synchronous `SpecSourceExtractor` refuses PDF-labelled sources with `Unsupported / pdf-worker-required` before UTF-8 or JSON decoding. PDF extraction requires the native runner.

Each immutable `SpecPdfEvidenceChunk` exposes `Ordinal` (zero based), `Text`, `Locator` (`SpecEvidenceLocator.PdfPage`, one based), `Formula` (always null), and `MissingText`. Blank pages are retained in order, including blank pages in otherwise usable Partial results. Citations bind the extraction identity, ordinal and exact PDF page. The canonical representation is:

```text
["braid.spec-evidence.v2", code,
 [ordinal,text,["pdf-page",oneBasedPage],null,missingText], ...]
```

The first four chunk fields follow Office's ordinal/text/locator/formula convention; the fifth preserves the PDF missing-text fact. Consumers must support this PDF shape.

The fixed status mapping is:

- `partial` becomes `Partial`, code `pdf-page-text-only`; all page facts remain present.
- `needsOcr` becomes `Unsupported`, code `pdf-needs-ocr`; all blank/whitespace page facts remain present. This is a refusal, not usable Partial evidence.
- `encrypted` becomes `Unsupported`, code `pdf-encrypted`, with no chunks.
- `malformed` becomes `Malformed`, code `pdf-malformed`, with no chunks.
- `limitExceeded` becomes `LimitExceeded`, code `pdf-limit-exceeded`, with no chunks.
- Canonical physical serialization overflow becomes `LimitExceeded`, code `serialized-evidence-bytes`, with no chunks.

The extractor version is **4**. The evidence digest covers the exact canonical bytes, including missing-text flags. Physical serialization is bounded by a checking stream; its default/hard maximum is 8 MiB, independently of the worker's 1 MiB text bound. All successful usable-text extraction is Partial; PDF layout/rendering completeness is not claimed.

`SpecPdfExtractionProfile` requires the independently approved executable SHA-256 and includes it in `Digest`, together with protocol/extractor strings, native sandbox revision marker, resource limits, input/output/text/page limits and the canonical evidence byte limit. Changing executable bytes or evidence limits changes the profile pin and therefore extraction/snapshot identity. Protocol strings alone do not identify the executable's fixes.

## Native composition

The Rust entrypoint shares `engines/ferrule-host/src/sandbox.rs` by module path. PDF compilation must include that source file; a package containing only the PDF directory is insufficient. Linux-only dependencies use the native host's pinned libc/rustix/seccompiler versions.

The native profile is fixed: 10 CPU seconds, 512 MiB address-space ceiling, 64 descriptors, 10-second parent wall deadline, 8 MiB stdin and stdout caps. Native hardening refuses filesystem opens/changes, network, process/thread creation and exec; it sets no-new-privileges, non-dumpability, process/file/core/other limits and three syscall filters. Arguments selecting different resource values fail before stdin. Paths, URLs, passwords and other arguments are refused. The argument-free mode remains the portable wire-test mode; the host composition always selects sandbox mode.

`QualifiedPdfProcessWorker.CreateForQualificationAsync` is internal and composes `ProcessWorkerRunner` with the internal PDF profile, separately from the hosted-.NET Office launcher. It checks the configured expected profile digest and independently approved executable digest before launch, then compares the runner's own executable measurement. The runner checks the ready marker, inherited-plus-three seccomp filters, every soft/hard resource limit, hidden environment and unprivileged identity in procfs before sending source bytes. It clears the environment, creates a private empty working directory and retains its existing output/deadline/cancellation/cleanup failure handling. The PDF wrapper serializes requests and uses the existing strict response decoder. Process failures throw worker-unavailable errors rather than document refusals.

Startup executes the parser with empty input under containment and requires its source-bound malformed response. Deployment must provide an immutable/read-only executable and trusted native loader/libraries for the entire runner lifetime.

The host uses `CreateForQualificationAsync` after validating the installation. Public `QualifiedPdfProcessWorker.Create` returns unavailable: `pdf-worker-platform-unsupported` outside Linux and `pdf-worker-linux-qualification-pending` on Linux.

## Tests

Build the portable Rust executable first:

```text
cargo test --locked --offline --manifest-path engines/spec-pdf-worker/Cargo.toml
```

Set `BRAID_TEST_PDF_EXECUTABLE` to that build's absolute executable path and `DOTNET_HOST_PATH` to the pinned .NET SDK executable, then run:

```text
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --filter "FullyQualifiedName~SpecPdfProcessTests|FullyQualifiedName~SpecDocumentWorkerTests|FullyQualifiedName~SpecOfficeExtractionTests|FullyQualifiedName~SpecPdfWorkerResponseTests|FullyQualifiedName~SpecIntakeTests" --nologo --verbosity minimal -m:1 -p:OpenApiGenerateDocuments=false --no-restore
```
