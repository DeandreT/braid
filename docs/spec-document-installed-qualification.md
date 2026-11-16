# Installed document worker checks

`SpecDocumentInstalledQualificationTests` uses `BRAID_TEST_DOCUMENT_HOST_LINUX=1`
and the `BRAID_SPEC_DOCUMENT_*` settings described in
[document host startup](spec-document-host.md). Tests skip on Windows or without
the opt-in. On Linux with the opt-in, missing settings, root/capabilities,
unprotected mounts, mismatched pins and failed native admission fail the test.

## Run against a prepared installation

Prepare the Linux x64 worker/runtime/launcher/test image using the host installation
requirements. Keep loader, runtime and test assembly trees read-only, with distinct
private writable work mounts on a separate backing filesystem. Configure the
expected build identities from the installation's trusted build record.

Build the test image before making it read-only. With the host settings supplied:

```sh
BRAID_TEST_PDF_EXECUTABLE="$BRAID_SPEC_DOCUMENT_PDF_EXECUTABLE" \
BRAID_SPEC_DOCUMENT_WORKERS=optional BRAID_TEST_DOCUMENT_HOST_LINUX=1 \
  dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~SpecDocumentInstalledQualificationTests|FullyQualifiedName=Braid.Tests.SpecDocumentHostLinuxTests.Installed_workers_pass_real_host_startup_and_revoke_admission_on_shutdown'
```

Match the image's build configuration; add `-c Release` for a Release image. Put
test logs/results outside protected installation paths. Use empty Office/PDF work
roots and a test process without unrelated children. The collection disables xUnit
parallel execution so checks cannot race other collections using those roots.

## Checks

- Construct and start `SpecDocumentHostResources`, then extract through
  `QualifiedOfficeProcessWorker` and `QualifiedPdfProcessWorker`.
- Parse DOCX/XLSX fixtures with Unicode text; compare exact Office canonical bytes
  with the parser reference and verify source/profile/evidence binding.
- Parse a three-page PDF with a blank middle page; verify ordered page citations
  and source/profile/evidence binding.
- Refuse malformed Office/PDF documents, encrypted ZIP directory markers,
  password-protected PDF content and painted image-only PDFs that require OCR.
- Send a corrupt Office digest through the installed `ProcessCompiledRuntimeRunner`;
  require transport failure with no output. Change a successful response to claim
  `Complete` and require the parent decoder to reject it.
- Withhold PDF stdin EOF to keep the native runner active during shutdown. Require
  cancellation, no evidence and unavailable runner state.
- Require empty work roots and no direct children after startup and each completed
  case. Inspect every `/proc/self/task/*/children` list, including zombies. After
  host shutdown, retained worker references must reject extraction.

For hostile-input, resource and host-shutdown checks, see
[installed hostile qualification](spec-document-installed-hostile-qualification.md).
