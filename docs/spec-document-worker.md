# Document worker executable and host foundation

This initial implementation record introduced the real .NET child executable at `dotnet/apps/Weft.DocumentWorker`, a bounded request/response protocol, a strict parent decoder and an explicit publication method. It is built by `dotnet/Weft.slnx`. At that boundary, text/JSON intake remained the default and Office activation awaited the admitted schema, API/UI contracts and live host qualification.

Later [installed-worker qualification](spec-document-installed-qualification.md) and the [hostile/lifecycle follow-up](spec-document-installed-hostile-qualification.md) passed four initial cases and a separate 16-case follow-up, with zero failures or skips, closing the reviewed bounded local Linux scope. Exact-parser, shared-control and separately pinned probe evidence retain their stated limits; AppArmor enforcement and production-environment equivalence are not established. Different/final installations require their own approved identities and executed results. Public PDF factory and filesystem spec-store refusal gates remain unchanged; qualification does not activate them.

## Child protocol

`Weft.DocumentWorker` accepts no command-line arguments. Stdin is a bounded binary frame, not a caller path, URL, password, layout or executable selection. It reads through EOF with a fixed cap before parsing. The 84-byte header is:

- Bytes 0–7: ASCII `WEFTOFC1`.
- Eleven little-endian Int32 fields starting at byte 8: source format (Docx=4 or Xlsx=5), exact source byte count, maximum source bytes, ZIP entries, entry bytes, aggregate expanded bytes, compression ratio, XML depth, XML nodes, chunks, serialized evidence bytes.
- Bytes 52–83: SHA-256 of the complete source bytes.
- Byte 84 onward: exactly the captured DOCX/XLSX bytes, at most 10 MiB.

Every profile field is validated against the extraction hard ceilings. The child recomputes the source digest and rejects mismatches, truncated/trailing bytes, unknown formats and invalid limits. Parent project/bundle/source IDs never leave the parent; a fixed inert child-local identity is replaced with the parent's exact captured-source identity only after validation.

Successful stdout is one closed JSON object:

```text
{
  "format": "weft.office-worker-response.v1",
  "extractor": 3,
  "sourceSha256": "<captured source SHA-256>",
  "sourceFormat": "Docx|Xlsx",
  "sourceByteCount": <exact source count>,
  "profileSha256": "<all extraction limits digest>",
  "outcome": "Partial|Unsupported|Malformed|LimitExceeded",
  "evidenceSha256": "<canonical evidence SHA-256>",
  "evidence": ["weft.spec-evidence.v2", fixedCode, [ordinal,text,typedLocator,formulaOrNull], ...]
}
```

The maximum request is 10 MiB + 84 bytes; stdout is 10 MiB + 2 KiB. Evidence itself is already physically capped during canonical serialization, and the envelope contains only fixed-size hashes and closed literals. The parent rejects unknown/duplicate fields, unknown outcomes/codes, source/profile mismatches, invalid/duplicate structural coordinates, inconsistent formula-cache states, noncanonical evidence encodings and invalid evidence digests. It independently enforces text/chunk/evidence/source limits. Refusals contain no chunks. Transport failures produce exit 1 and one fixed stderr line; they never masquerade as document outcomes. The executable has a 25-second cooperative deadline; hard resource/deadline containment belongs to its launcher.

## Linux host composition

`QualifiedOfficeProcessWorker.CreateAsync` composes the existing `ProcessCompiledRuntimeRunner` with a fixed Office operation. That runner uses the already-existing `compile-sandbox --profile=runtime --runtime-assembly=...` hosted-.NET path, rather than launching an arbitrary process with a kill timer. Its deployment-provided paths are installation facts, never document request fields.

Admission requires an independently configured expected assembly SHA-256 and expected full runtime identity. The latter measures the installed entrypoint/dependencies, .NET host/runtime, native launcher, runner assembly, ELF interpreter and admitted native libraries, with an Office-specific operation/protocol/extractor marker. The installation must be immutable/read-only after measurement. A matching protocol string is not a build pin.

The reused launcher provides its read-only .NET runtime Landlock/seccomp profile, prevents process creation/exec and network access, and establishes fixed CPU/address-space/file/descriptor limits and non-dumpability. The parent verifies non-root identity, no-new-privileges, filter count, exact soft/hard limits and hidden process environment before sending its go byte or source data. The resources remain those of the compiled runtime: 30 CPU seconds, 30-second parent wall deadline, 3 GiB address-space ceiling, 128 file descriptors, 1 MiB file ceiling and a 512 MiB managed-heap setting. The heap setting is not a separate OS-native RSS limit. Parent environment clearing and explicit runtime grants are reused. One Office request runs at a time per host instance.

The factory runs the existing empty-frame probe through the installed Office entrypoint, checking its exact fixed refusal. Each subsequent operation is admitted by the same pre-input checks. Cancellation, output overflow, child failure and shutdown reuse the runner's owned process/pipe/reap/cleanup logic; failed cleanup disables availability. Failure to admit execution throws a fixed worker-unavailable error, not a published extraction outcome.

On Windows, factory admission returns `document-worker-platform-unsupported`, with `Available=false`, before file access/process creation. There is no public portable production bypass. The portable child launcher used by tests lives exclusively in the test assembly and qualifies wire behavior, not CPU/memory/network/process containment.

## Publication and snapshot boundary

`SpecBundleService.ExtractOfficeAndPublishAsync` operates on an already-retained original. It checks current write authority, exact source/object ownership, captured byte count and digest before invoking the worker. After execution it verifies the extraction source/format/profile/version pin and re-enters the existing publication path, which rechecks current authority and verifies the exact serialized evidence digest before reservation and physical publication. The registry remains responsible for source-format/CAS/attempt/tombstone fencing at the physical boundary.

Cancellation, source mismatch, unavailable execution, nonzero exit, transport corruption and output overflow cannot create extraction reservations. A valid document refusal can be published as atomic evidence and retained in `SpecBundleSnapshot`; it is not reported as Complete. Retrying an uncertain publication keeps its object/attempt identities and exact canonical bytes. Tests verify the snapshot digest is unchanged after lost-ack replay. The in-memory storage fixture exercises this contract, not a deployed Blob/PostgreSQL backend.

## Initial PDF and qualification boundary

The Rust PDF executable and `SpecPdfWorkerResponse` decoder use a separate native process protocol. This Office .NET launcher composition does not qualify or start that native executable. At this initial boundary, its next bridge required an independently configured executable **build digest**, the native process boundary, bounded stdout reads, exit/source verification and preservation of blank pages/missingText. The later [native PDF runner](spec-pdf-native-runner.md) records that composition. The protocol/extractor strings stayed stable across PDF fixes and cannot substitute for that digest.

At this initial boundary, live Linux qualification was pending for the installed Office worker/runtime/launcher image: ordinary DOCX/XLSX parsing under containment, prohibited network/file/process operations, memory/CPU exhaustion, timeout/cancellation/output overflow, reap/cleanup failures, shutdown races and immutable installation identity were identified for review. The Windows run had no WSL distribution and did not perform those checks. The later linked records close their reviewed bounded local scope and distinguish exact-parser results from shared-control and separately pinned probe evidence. This historical checklist does not expand that closed scope. Portable tests are not evidence that a production Linux deployment has passed qualification.

At that implementation boundary, ordered migration `0046` (with `0045` reserved for creation), API/OpenAPI/generated clients, UI v2 evidence rendering and actual backend admission remained integration gates. `0044` stays immutable. This slice registered no server capability and changed no SQL/API/UI admission. Later [host composition](spec-document-host.md) and the qualification records describe subsequent schema/admission evidence separately from activation.

## Historical Windows verification

Pinned SDK 10.0.112; set `DOTNET_HOST_PATH` to its absolute executable for actual portable child tests:

```text
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj --filter "FullyQualifiedName~SpecDocumentWorkerTests|FullyQualifiedName~SpecOfficeExtractionTests|FullyQualifiedName~SpecPdfWorkerResponseTests|FullyQualifiedName~SpecIntakeTests" -m:1 -p:OpenApiGenerateDocuments=false
```

The process cases launch the actual worker and cover DOCX/XLSX, complete input hashing, profile limits, fixed failures, parent tamper rejection, deadline/cancellation reaping, stdout bounds, authority changes, exact publication replay and snapshot/refusal behavior. The original Office and text/JSON regressions remain included. See the review report for final counts and build/format results.

The initial Windows verification recorded **171 focused tests passed, zero failed/skipped**, including 30 new worker/protocol/publication cases and the prior 141 extraction/decoder cases. Review regressions use canonical, correctly hashed evidence to prove Blank cells cannot carry text/cached values and InlineString cells cannot carry formulas; valid blank and inline-string cases remain admitted. The test command built the new executable and affected projects successfully. Changed C# whitespace checks passed. An additional compiled-protocol/transport/settings/lifecycle selection produced 44 passed, 8 failed and 1 skipped: the eight existing Docker-settings cases reach `MappingRuntimeSelection.cs:44` with `/usr/bin/docker`, which fails Windows absolute-path validation; the certificate-settings case is platform-skipped. Those endpoint/settings files were unchanged in that slice. This is not a passing claim for that broader selection or for live Linux containment.
