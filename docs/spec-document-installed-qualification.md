# braid installed document worker qualification

**Reviewed local Linux qualification passed.** The initial protected-installation selection passed all four cases with zero failures or skips. The separately approved hostile/resource/lifecycle/census/race selection subsequently passed **16/16 on its first execution, zero failures or skips**, including all four witnessed qualified-extraction/default-host shutdown races. This closes the reviewed bounded local scope, not deployment or full hostile-input acceptance.

The installed fact in `SpecDocumentInstalledQualificationTests` uses the same `WEFT_TEST_DOCUMENT_HOST_LINUX=1` opt-in and independently approved `WEFT_SPEC_DOCUMENT_*` settings as [document host startup](spec-document-host.md). That fact skips on Windows or without opt-in. On Linux with opt-in, absent settings, root/capabilities, unprotected mounts, mismatched pins and failed native admission fail the test through the unchanged host startup gate.

## Run against a prepared installation

An installation owner must prepare and independently approve the Linux x64 worker/runtime/launcher/test image first. Follow the existing host installation requirements, including read-only loader/runtime/test assembly trees and distinct private writable work mounts on a separate backing filesystem. Do not derive new expected pins from whichever binaries happen to be present to get a passing result.

Build the test image before making it read-only. From that protected image, with the documented settings already supplied:

```sh
WEFT_TEST_PDF_EXECUTABLE="$WEFT_SPEC_DOCUMENT_PDF_EXECUTABLE" \
WEFT_SPEC_DOCUMENT_WORKERS=optional WEFT_TEST_DOCUMENT_HOST_LINUX=1 \
  dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~SpecDocumentInstalledQualificationTests|FullyQualifiedName=Weft.Tests.SpecDocumentHostLinuxTests.Installed_workers_pass_real_host_startup_and_revoke_admission_on_shutdown'
```

Match the configuration used when building the image (add `-c Release` for a Release image). Test platform logs/results must use an explicitly writable location outside protected installation paths if the test runner requires them. Use dedicated empty Office/PDF work roots and a test process without unrelated children. The collection disables xUnit parallel execution so startup/settlement checks cannot race other test collections using those roots. The driver does not provision an installation or contact PostgreSQL, Azure or a provider.

## What the test actually exercises

- Production `SpecDocumentHostResources` construction/startup, followed by actual `QualifiedOfficeProcessWorker` and `QualifiedPdfProcessWorker` extraction. No injected factory, portable child launcher or synthetic sandbox admission is used.
- Real DOCX/XLSX packages from the existing fixture builders, readable Unicode text, source/profile/evidence binding, and exact Office canonical bytes compared with the parser reference.
- A real three-page PDF, including a blank middle page, ordered page citations and source/profile/evidence binding.
- Atomic malformed Office/PDF refusals, encrypted ZIP directory-marker refusals for both Office formats, a password-protected PDF with an encrypted page stream, and a painted image-only PDF that requires OCR. The ZIP marker case is not a claim of full password-protected Office-format coverage.
- A corrupt Office request digest sent through the actual installed `ProcessCompiledRuntimeRunner`, with non-success transport failure and no admitted output. A successful contained response is then modified to claim `Complete`; the parent decoder must reject it. This last mutation checks protocol outcome validation, not containment of a hostile replacement executable.
- Active native PDF runner shutdown with only EOF withheld through the existing test seam. It uses the real executable, fixed sandbox arguments, procfs verification and default resource limits. Shutdown must return cancellation, no evidence and unavailable runner state. This is not an active **host** shutdown race test.
- Both work roots empty and no direct test-process children after startup and each completed case, then host shutdown/admission revocation and rejection through retained worker references. Child checks cover every `/proc/self/task/*/children` list, including zombies, rather than filtering by executable name.

The embedded 900-byte encrypted PDF is a public synthetic fixture using Standard Security Handler V1/R2, 40-bit RC4, passwords `fixture-owner` and `fixture-reader`, and an encrypted text stream. Its SHA-256 is `40649c72b43425c05b2d3f4b5ced88f8a9cb3106e277d43cd4e9ab1aa4dc3667`. It was assembled with byte-accurate xref offsets using standard-library PDF padding/MD5/RC4 operations; it contains no customer data. The image fixture paints a one-pixel RGB XObject rather than substituting a blank text page.
The two-case `Portable_pdf_fixture_validation_refuses_encrypted_and_image_only_documents` theory previously passed on Windows with the existing pinned native PDF executable. It sends these same fixtures through the existing actual native child harness using `WEFT_TEST_PDF_EXECUTABLE`, decodes with its executable-hash profile, and checks refusals plus reap/I/O settlement. This portable protocol check runs without Linux opt-in and is not containment qualification. The installed-worker fact was skipped in that historical Windows run, as required; its separate Linux execution is recorded below.

## Completed local Linux checks — 2026-11-12

The runs used source base `930ba1f5` plus uncommitted qualification changes.
Production runtime code and the original public admission gates were unchanged.
The protected image was built with SDK `10.0.112` and runtime `10.0.12`, using
locked offline dependencies and no build network:

`sha256:a9cfc1028e157c3391c58d7036ccfb7bbb771077f5712bf714309a96980c4f6c`

The document-worker settings candidate was separately approved and bound to that exact
image. Expected identities were not recalculated from arbitrary installed files
to bypass admission. The protected-installation selection passed **four cases,
zero failures or skips**:

- Both rows of `Portable_pdf_fixture_validation_refuses_encrypted_and_image_only_documents`.
- `SpecDocumentInstalledQualificationTests.Installed_workers_parse_documents_refuse_atomic_failures_and_settle_real_children`.
- `SpecDocumentHostLinuxTests.Installed_workers_pass_real_host_startup_and_revoke_admission_on_shutdown`.

The host/actual PostgreSQL/Office/PDF regression selection also passed **124 tests,
zero failures or skips**. It included real `SpecDocumentHostPostgresTests`
schema-46, actor and store-admission checks. Those database results are separate
from the installed-worker drivers' true schema callback fixture; the callback
itself is not proof of database admission. These overlapping selections must not
be summed or treated as whole-project acceptance.

Execution used the WSL kernel with `AppArmor=N`. Built-in seccomp and native
Landlock controls remained unchanged; no privileged/unconfined mode, capability
addition or policy override was used. This is local Linux evidence, not AppArmor
enforcement or production-environment equivalence.

The CI prerequisite follow-up now builds and exports the actual PDF executable
and required portable authoring example before the existing exhaustive shards.
Both Rust prerequisite builds passed locally offline, and 15 CI launcher unit
tests passed. No hosted CI result is claimed.

## Completed hostile/lifecycle follow-up

The separately built and independently measured/approved image was:

`sha256:f0b5a168c705a6eb4073bfdc67a83efd55efa7f00164ad8720064fbb06fd3f0c`

Its exact selection passed nine main hostile/lifecycle cases, one shared Office
denial-profile probe, two separate Office resource probes and four genuine default
host Stop/Dispose races with pending qualified Office/PDF extraction. Each race
recorded a hardened live-child witness and interrupted overlap on attempt 1.
Gate and driver exits were zero; Gate wait/reap/disposal and owned-container
absence were confirmed. Office/PDF roots were empty, UID/GID 1000, mode `0700`.
Genuine password-encrypted DOCX/XLSX refusals were exercised through the installed
workers. See [the full bounded proof and evidence](spec-document-installed-hostile-qualification.md).

The four matching-image Docker-equivalence cases also passed 4/4 after a local
staging directory-permission correction, using:

- `sha256:a2829f58ff84a790b365ebad3232ec5c41be53e6647d5b592a1797b59d6ed5b1`
- `sha256:c7f3ddeb8abd177c556ac3043c59c3484d6872a8408a82175321a69c2703db3a`

Office CPU evidence combines shared native CPU-termination tests with installed
admission of the exact 30-second CPU/wall limits; exact-parser SIGXCPU was not
observed. PDF controls combine shared Ferrule sandbox tests with installed fixed
profile admission and real parsing/lifecycle results, not exact-PDF-payload
exhaustion or signal attribution. The separately pinned Office memory/overflow
probes are not parser resource-consumption evidence.

## Remaining acceptance boundaries

The bounded cases above have passed on the approved local protected image. A
different or final deployment installation must retain its own approved
build/profile/runtime identities and executed results; the local image result
does not transfer automatically. Windows compilation or a skipped test cannot
establish containment. Actual database admission was exercised in the separate
PostgreSQL selection, not by the installed driver's true schema callback.

The initial four-case driver checks direct children and normal/active-runner settlement. The subsequent 16-case driver additionally checks every visible PID/start-time identity in its externally verified dedicated namespace, including reparented survivors and zombies. This is representative qualification, not exhaustive grammar/fuzz coverage, every cleanup-failure branch, native RSS/OOM qualification or observation of another namespace. The public PDF factory's `pdf-worker-linux-qualification-pending` refusal and the filesystem spec-store unavailability gate remain unchanged.

AppArmor, production-environment equivalence, live-provider and real Azure
acceptance are not established by these runs. Live-provider and Azure acceptance
were explicitly deferred.
