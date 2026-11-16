# Document intake routes and UI admission

## HTTP and service contract

`GET /api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/capabilities` uses the existing authenticated content boundary and fresh bundle authorization. It returns a no-store version-1 envelope with `maximumTextRequestBytes` (1 MiB) and admitted `formats`. Each format includes its explicit upload value, maximum source bytes, extractor version and evidence format. This is a capability report, not an authority token; uploads recheck admission.

Text formats are `text`, `json`, `openapi-json` with a 10 MiB capture bound, extractor 2 and v1 evidence. Structural formats are `docx`, `xlsx` (Office profile cap, at most 10 MiB; extractor 3) and `pdf` (8 MiB; extractor 4), with v2 evidence. Upload metadata includes the source version, original/extraction object IDs, attempt keys, `If-Match` and extraction CAS version.

`SpecBundleService.IntakeAsync` checks current authority and document admission before consuming source bytes or reserving the original. Office uses the document worker; PDF verifies the retained original's owner/count/digest, runs the configured worker, checks the exact source/format/profile/extractor identity and publishes canonical evidence bytes. Schema/worker admission is checked again before execution and publication. Physical storage and registry callbacks enforce authority and CAS.

Unavailable document admission returns **503 `DocumentWorkerUnavailable`**. Worker execution failure also returns 503 and cannot become a document outcome. Original retention may already have completed when execution or publication fails; clients retain their exact retry intent and reconcile inventory. Valid document refusals are published honestly: structural success is Partial, PDF `needsOcr` and `encrypted` are Unsupported. The existing evidence endpoint returns the precise refusal code and retained page facts. OCR refusal retains missing-text pages; encryption refusal has no chunks.

The OpenAPI document and generated TypeScript now include capabilities, structural upload descriptions and v2 evidence shapes. The receipt remains the existing `SpecIntakeV1` contract, with Partial admitted alongside its existing refusal outcomes.

## Production composition

`SpecDocumentIntakeAdmission` has no public enabling constructor or factory. Production `SpecHostConfiguration` now binds each actor-bound service to host-owned Office/PDF workers. The default remains disabled. See [document host configuration](spec-document-host.md) for independently pinned installation settings and the real startup gates.

The internal admission constructor is for trusted assembly composition and route fixtures. Production uses the same actor-bound PostgreSQL session for schema and active-store validation. Availability reflects the owned worker pair after installation validation and worker probes. Disabled or unavailable workers advertise only text formats and reject structural uploads.

## UI behavior

The specification page fetches capability version 1 after current inventory/access and rechecks it before each incomplete upload attempt. Format options and source byte limits come from that response. File extensions suggest only formats that the server advertised; the visible format selector remains explicit. Source bytes are uploaded unchanged. The browser parses retained evidence, not DOCX/XLSX/PDF documents.

Missing, unavailable, timed-out, unknown-version or malformed capability responses disable new intake while preserving freshly authorized inventory and extraction reads. The status explains that refreshing retries capabilities. Authentication/content denials still clear private state. Capability state is fenced by the existing context/generation checks and cleared on route/identity/access invalidation. A lost acknowledgement keeps the original IDs, keys and bytes; inventory recovery can confirm a completed upload even if worker availability later changes.

The PDF display accepts the canonical five-field v2 tuple `[ordinal,text,["pdf-page",oneBasedPage],null,missingText]`. It verifies authorized inventory digest/count, page ordering, status/code consistency and text/page/byte bounds. Blank pages remain visible, and the missing-text fact uses the trusted decoder's Unicode whitespace definition. Structural Complete receipts and text-format Partial receipts are rejected.

## Verification

Focused .NET filter: `SpecDocumentIntakeHttpTests|SpecHttpTests|SpecPdfProcessTests|SpecDocumentWorkerTests|SpecOfficeExtractionTests|SpecPdfWorkerResponseTests|SpecIntakeTests` (each qualified with `FullyQualifiedName~`). Set `DOTNET_HOST_PATH` and `BRAID_TEST_PDF_EXECUTABLE` to the pinned absolute executable paths. UI: `playwright test --config apps/web/playwright.spec-intake.config.ts` from the repository root, or the equivalent config-relative invocation from `apps/web`.
