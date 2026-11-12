# Retained feedback reauthorization qualification

## Source and scope

Windows qualification on 2026-11-12, using .NET SDK 10.0.112. Source is the
follow-up commit containing this receipt, directly based on `3cd43af4` with
provider-contract ancestor `374ea6a5`. Tests used the actual composed workspace
parser, handler, and orchestrator. The build used the repository sources without
temporary source substitutions.

The fix carries the trusted retained-proposal identity/fingerprint in active-turn
state independently of citation candidates. Every subsequent provider/tool
boundary reads current exact retained-content authority before dispatch. The
Specification disclosure audit remains ahead of provider delivery.

## Results

- Red regression on `3cd43af4` plus the new retained-content-denial case: 4 passed,
  1 failed. Revoking only retained-content access after DataShared acknowledgement
  reproduced the second-provider-request disclosure.
- Fixed feedback and payload reauthorization selection: 27 passed, 0 failed,
  0 skipped. Includes denial and fingerprint substitution after disclosure
  acknowledgement and after a previously authorized provider round.
- Creation composition selection: 101 passed, 0 failed, 5 skipped (106 total).
  Selection: `CreationFeedbackToolTests`, `CreationCapabilitiesTests`,
  `CreationRuntimeToolTests`, `CreationProviderContractTests`,
  `WorkspacePayloadReauthorizationTests`, `CreationRuntimeHttpTests`.
- Orchestration regression selection: 145 passed, 0 failed, 0 skipped.
  Selection: `ChatOrchestrationBoundTests`, `ChatOrchestrationEventTests`,
  `ChatOrchestrationLifetimeTests`, `ChatOrchestrationAdmissionTests`,
  `ChatOrchestrationToolTests`, `MappingToolOrchestrationTests`,
  `WorkspaceChatOrchestrationTests`, `WorkspacePayloadToolTests`.
- Native format/preview selection: 57 passed, 0 failed, 0 skipped.
  Selection: `MappingCreationFormatsTests`, `MappingAuthoredPreviewInteropTests`,
  `MappingAuthoringBackendTests`.
- Generated TypeScript contract check, Markdown formatting, and diff whitespace
  check passed.

Each selection used an OR of `FullyQualifiedName~<class>` filters. The native
composition selection executed the real compiler through all 36 provider boundary
pairs and the singular XML root/frame case. The separate native format selection
executed all 36 typed format pairs, XML namespace/attribute/text/iteration cases,
and actual compiler-to-preview interoperability. Feedback SSE tests use recording
backend ports and a loopback provider; native protocol tests qualify portable
execution rather than sandbox isolation.

The only five skips require the Linux browser-session key profile:

- `CreationCapabilitiesTests.Missing_runtime_returns_false_to_current_metadata_reader`
- `CreationCapabilitiesTests.Authenticated_metadata_capability_reflects_runtime_without_payload_or_source_reads`
- `CreationRuntimeHttpTests.Session_csrf_and_critical_questions_refuse_ingestion`
- `CreationRuntimeHttpTests.Authenticated_exact_approval_recovery_preview_and_revocation`
- `CreationRuntimeHttpTests.Stale_snapshot_preserves_authorized_terminal_receipt_but_content_revocation_hides_it`

## Native executable pins

The environment used these executables; SHA-256 hashes matched before and after
qualification:

- `WEFT_AUTHORING_TEST_EXECUTABLE`: `C:/example/checkout/engines/ferrule-host/target/debug/examples/authoring_protocol.exe`
  - `C168E1DB21CFC2E632DE7D666F2FD4F66B558B4A7BE324EDFDB523161DF82409`
- `WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE`: `C:/example/checkout/engines/ferrule-host/target/debug/examples/authored_preview_protocol.exe`
  - `3449078204D77A51D446BC8B3CE619A8BF56F050E2107C964BF0570A8929A6D9`
- `WEFT_TEST_PDF_EXECUTABLE`: `C:/example/checkout/engines/spec-pdf-worker/target/debug/weft-spec-pdf-worker.exe`
  - `378ED6314442771937A02966346E58971A028A1DDB00E59F458AD2512FF1DB50`

## Test receipts

TRX files were captured in `%LOCALAPPDATA%/Temp/opencode/weft-feedback-qualification`.
Their hashes identify the exact test runs summarized above:

- `late-content-before-fix.trx`: `6FB37A555E55A4895A307F1D43E0DA4FC118A4661DE464B7C986057391A65B94`
- `late-content-after-fix.trx`: `EE976517E22F887B319CCCA30192E6DDFCEA45F20F9E8F7D1053B6125C2778EB`
- `native-creation-composition.trx`: `3DD495FC3EBBF362887559D94392931AF2F0046FB9C030AF277FA2F1015F0BA7`
- `orchestration-regressions.trx`: `B2D428B7BA1863040F5051D4D3176A007BB9256EF1AD9361E83F69A2FABD810F`
- `native-formats-preview.trx`: `8A107412F8C17A342C771ED9C3DCAD3B626B9854F5ADB063B3FF527C779AD768`
