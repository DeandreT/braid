# Native PDF runner and canonical evidence (initial implementation record)

This unit, initially recorded as review pending, composes the reviewed Rust PDF parser with the existing native service self-hardening implementation and parent runner. Public PDF admission remains closed; the historical Linux qualification refusal is unchanged.

Later [installed-worker qualification](spec-document-installed-qualification.md) and the [hostile/lifecycle follow-up](spec-document-installed-hostile-qualification.md) passed four initial cases and a separate 16-case follow-up, with zero failures or skips, closing the reviewed bounded local Linux scope. Exact-PDF parsing/lifecycle results and shared-control evidence retain their distinct limits; AppArmor enforcement and production-environment equivalence are not established. Different/final installations need their own approved identities and executed results. The public PDF factory and filesystem spec-store refusal gates remain unchanged; these records do not activate either path.

## Evidence contract for consumers

`SpecSourceFormat.Pdf = 6`. `SpecSourceCapture` caps PDF capture at 8 MiB even when the caller uses the larger generic default; over-limit capture consumes at most one detection byte and returns no source. Other capture formats retain their bounds.

`SpecPdfExtractionResult` exposes `Identity`, `Outcome`, `Code`, `Chunks`, `Cite(ordinal)` and `CopyEvidenceBytes()`. Its constructor is private and its `Decode` factory is internal, available to trusted Application code and the existing MappingBackends friend assembly. It uses the existing strict `SpecPdfWorkerResponse` decoder to bind successful bounded stdout to the captured source digest/count and source identity. There is no public canonical-result factory. The public low-level transport decoder is not proof of qualified execution.

The legacy synchronous `SpecSourceExtractor` refuses PDF-labelled sources with `Unsupported / pdf-worker-required` before UTF-8 or JSON decoding. Genuine PDF bytes, JSON bytes labelled PDF and invalid UTF-8 all take this refusal path; PDF cannot fall through to legacy JSON extraction.

Each immutable `SpecPdfEvidenceChunk` exposes `Ordinal` (zero based), `Text`, `Locator` (`SpecEvidenceLocator.PdfPage`, one based), `Formula` (always null), and `MissingText`. Blank pages are retained in order, including blank pages in otherwise usable Partial results. Citations bind the extraction identity, ordinal and exact PDF page. The canonical representation is:

```text
["weft.spec-evidence.v2", code,
 [ordinal,text,["pdf-page",oneBasedPage],null,missingText], ...]
```

The first four chunk fields follow Office's ordinal/text/locator/formula convention; the fifth preserves the PDF missing-text fact. Consumers must explicitly support this PDF shape. Existing v1 and Office evidence formats are unchanged.

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

The Rust entrypoint now shares `engines/ferrule-host/src/sandbox.rs` by module path. The shared file remains unchanged. PDF compilation must include that source file; a package containing only the PDF directory is insufficient. Linux-only dependencies use the same pinned libc/rustix/seccompiler versions as the reviewed native host.

The native profile is fixed: 10 CPU seconds, 512 MiB address-space ceiling, 64 descriptors, 10-second parent wall deadline, 8 MiB stdin and stdout caps. Native hardening refuses filesystem opens/changes, network, process/thread creation and exec; it sets no-new-privileges, non-dumpability, process/file/core/other limits and three syscall filters. Arguments selecting different resource values fail before stdin. Paths, URLs, passwords and other arguments are refused. The argument-free mode remains the portable wire-test mode; the host composition always selects sandbox mode.

`QualifiedPdfProcessWorker.CreateForQualificationAsync` is internal and composes `ProcessWorkerRunner` with the internal PDF profile, separately from the hosted-.NET Office launcher. It checks the configured expected profile digest and independently approved executable digest before launch, then compares the runner's own executable measurement. The runner checks the ready marker, inherited-plus-three seccomp filters, every soft/hard resource limit, hidden environment and unprivileged identity in procfs before sending source bytes. It clears the environment, creates a private empty working directory and retains its existing output/deadline/cancellation/cleanup failure handling. The PDF wrapper serializes requests and uses the existing strict response decoder. Process failures throw worker-unavailable errors rather than document refusals.

Qualification admission also executes the actual parser with empty nonprivate input under containment and requires its source-bound malformed response. A probe is only one check, not full host qualification. Deployment must provide an immutable/read-only executable and trusted native loader/libraries for the entire runner lifetime; a startup measurement is not an atomic replacement-proof execution mechanism.

Public `QualifiedPdfProcessWorker.Create` returns unavailable on every platform: `pdf-worker-platform-unsupported` outside Linux and `pdf-worker-linux-qualification-pending` on Linux. There is no configuration boolean to open this gate. A separate reviewed activation change is still required; the later local qualification does not change this refusal.

## Historical Windows verification and initial qualification boundary

Windows verification: **182 focused .NET tests passed, zero failed/skipped** (the prior 171 plus eleven PDF tests). Actual native child tests cover real multipage PDFs, blank-page retention, OCR refusal, malformed refusal, source tampering, invalid page order, nonzero exits, cancellation/deadlines, stdout caps, physical evidence caps, citations, snapshot stability and build/profile pins. Review regressions verify legacy dispatch refusal and the absence of a public canonical-result constructor/factory. Rust has **14 passing tests**: eleven existing PDF/core/process cases, one shared startup-category test, two native-profile/platform tests. Portable tests use a test-only unrestricted process helper and do not qualify Linux containment.

Build the portable Rust executable first:

```text
cargo test --locked --offline --manifest-path engines/spec-pdf-worker/Cargo.toml
```

Set `WEFT_TEST_PDF_EXECUTABLE` to that build's absolute executable path and `DOTNET_HOST_PATH` to the pinned .NET SDK executable, then run:

```text
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj --filter "FullyQualifiedName~SpecPdfProcessTests|FullyQualifiedName~SpecDocumentWorkerTests|FullyQualifiedName~SpecOfficeExtractionTests|FullyQualifiedName~SpecPdfWorkerResponseTests|FullyQualifiedName~SpecIntakeTests" --nologo --verbosity minimal -m:1 -p:OpenApiGenerateDocuments=false --no-restore
```

At the initial Windows boundary, Linux x86-64 qualification was outstanding: the exact locked build and independent digest pin, immutable deployment/loader, actual readable/encrypted/image-only/malformed PDFs, pre-input procfs admission, network/file/process denial, memory/CPU exhaustion, wall deadlines, output overflow, cancellation, reap/pipe/directory cleanup and shutdown races were identified for review. That Windows run had no Linux execution evidence. The later linked records supply the bounded local results, distinguishing shared native resource/denial evidence from exact-PDF workload/lifecycle checks; they do not claim exact-PDF exhaustion or signal attribution. This historical checklist does not expand that closed scope. Public admission remains a separate reviewed gate.

Initial migration coordination: the then-future ordered `0046` was required to admit PDF format 6, Partial outcome 5 and extractor/profile identities through the existing registry and source-format checks. Keep immutable `0044` at Git blob `5363b15807cfc9b23e6b8cfb4eddf45018f6c1c7`. The initial boundary allowed the creation wrapper to consume this result contract after review, with storage decoding and activation requiring separately coordinated integration. Later [host composition](spec-document-host.md) and qualification records describe actual schema-46 admission separately; storage and public activation retain their own gates.
