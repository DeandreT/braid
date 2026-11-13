# Document intake routes and UI admission

Review unit based on `93a2a53d`, in `feat/v1-document-intake`.

## HTTP and service contract

`GET /api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/capabilities` uses the existing authenticated content boundary and fresh bundle authorization. It returns a no-store version-1 envelope with `maximumTextRequestBytes` (1 MiB) and admitted `formats`. Each format includes its explicit upload value, maximum source bytes, extractor version and evidence format. This is a capability report, not an authority token; uploads recheck admission.

Legacy formats are `text`, `json`, `openapi-json` with the existing 10 MiB capture bound, extractor 2 and v1 evidence. Structural formats are `docx`, `xlsx` (Office profile cap, at most 10 MiB; extractor 3) and `pdf` (8 MiB; extractor 4), with v2 evidence. The existing upload metadata, source version, original/extraction object IDs, attempt keys, `If-Match` and extraction CAS version are preserved.

`SpecBundleService.IntakeAsync` dispatches structural formats through the new partial service implementation. It checks current authority and trusted document admission before consuming source bytes or reserving the original. Office uses the reviewed worker/publication method; PDF verifies the retained original's owner/count/digest, runs the configured worker, checks the exact source/format/profile/extractor identity and publishes its exact canonical evidence bytes. Schema/worker admission is checked again before execution and publication. Existing physical storage and registry callbacks continue to enforce authority and CAS.

Unavailable document admission returns **503 `DocumentWorkerUnavailable`**. Worker execution failure also returns 503 and cannot become a document outcome. Original retention may already have completed when execution or publication fails; clients retain their exact retry intent and reconcile inventory. Valid document refusals are published honestly: structural success is Partial, PDF `needsOcr` and `encrypted` are Unsupported. The existing evidence endpoint returns the precise refusal code and retained page facts. OCR refusal retains missing-text pages; encryption refusal has no chunks.

The OpenAPI document and generated TypeScript now include capabilities, structural upload descriptions and v2 evidence shapes. The receipt remains the existing `SpecIntakeV1` contract, with Partial admitted alongside its existing refusal outcomes.

## Production composition

`SpecDocumentIntakeAdmission` has no public enabling constructor or factory. Production `SpecHostConfiguration` now binds each actor-bound service to host-owned Office/PDF workers. The default remains disabled. See [document host configuration](spec-document-host.md) for independently pinned installation settings and the real startup gates.

The internal admission constructor is for trusted assembly composition and controlled route fixtures. Production uses the same actor-bound PostgreSQL session for fresh schema-46 and active-store validation; availability reflects the owned worker pair after installation validation and real worker probes. A request/configuration boolean cannot assert qualification.

Deployment activation requires:

1. The persistence owner's actual ordered migration 46 and schema-admission result, tied to the same actor-bound database service.
2. A reviewed Linux installation composition for Office and PDF, independently approved executable/profile pins, immutable runtime/native installation and successful live CPU/memory/network/process/cleanup qualification.
3. Successful trusted host startup joining those verified facts and owning worker lifetime. PDF's public convenience factory stays closed; the internal qualification entrypoint is called by the host after installation validation.
4. The separately owned canonical restoration and exact inventory/extraction identities for creation consumers.

Disabled or unavailable workers advertise only legacy formats and structural upload attempts fail closed. Portable fixture results establish source behavior, not deployment qualification.

## UI behavior

The specification page fetches capability version 1 after current inventory/access and rechecks it before each incomplete upload attempt. Format options and source byte limits come from that response. File extensions suggest only formats that the server advertised; the visible format selector remains explicit. Source bytes are uploaded unchanged. The browser parses retained evidence, not DOCX/XLSX/PDF documents.

Missing, unavailable, timed-out, unknown-version or malformed capability responses disable new intake while preserving freshly authorized inventory and extraction reads. The status explains that refreshing retries capabilities. Authentication/content denials still clear private state. Capability state is fenced by the existing context/generation checks and cleared on route/identity/access invalidation. A lost acknowledgement keeps the original IDs, keys and bytes; inventory recovery can confirm a completed upload even if worker availability later changes.

The PDF display accepts the canonical five-field v2 tuple `[ordinal,text,["pdf-page",oneBasedPage],null,missingText]`. It verifies authorized inventory digest/count, page ordering, status/code consistency and text/page/byte bounds. Blank pages remain visible, and the missing-text fact uses the trusted decoder's Unicode whitespace definition. Structural Complete receipts and legacy Partial receipts are rejected.

## Verification

- **203 focused .NET tests passed**, zero failed. Seven existing Linux browser-session qualification cases were skipped on Windows. All fourteen new actual-route/service fixture cases passed.
- The route fixtures exercise actual ASP.NET routes and the real service over controlled storage/worker ports. Their internal transport-admission seam supplies a trusted actor without claiming identity-infrastructure qualification; public production registration always uses `CatalogRequestBoundary`.
- **60 UI/parser tests passed**, zero failed/skipped, including capability-based Word/Excel/PDF uploads, explicit format selection for unknown extensions, Partial receipts, OCR/encryption refusals, capability revocation, exact evidence rendering, existing text/JSON flows, retry/authority fencing and accessibility checks. Review regressions verify inventory/read availability during capability 503 and invalid responses, recovery on refresh, and private-state clearing for capability 401/403 responses.
- Pinned .NET 10.0.112; cached Node 22.23.3/npm 12.1.0. Angular development build and contract generation/checks passed.
- Actual PDF process regressions use the existing worker-tree executable at `C:/example/checkout/engines/spec-pdf-worker/target/debug/weft-spec-pdf-worker.exe`, SHA-256 `378ed6314442771937a02966346e58971a028a1ddb00e59f458ad2512ff1db50`; it was not rebuilt by this unit.

Focused .NET filter: `SpecDocumentIntakeHttpTests|SpecHttpTests|SpecPdfProcessTests|SpecDocumentWorkerTests|SpecOfficeExtractionTests|SpecPdfWorkerResponseTests|SpecIntakeTests` (each qualified with `FullyQualifiedName~`). Set `DOTNET_HOST_PATH` and `WEFT_TEST_PDF_EXECUTABLE` to the pinned absolute executable paths. UI: `playwright test --config apps/web/playwright.spec-intake.config.ts` from the repository root, or the equivalent config-relative invocation from `apps/web`.
