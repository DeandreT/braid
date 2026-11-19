# Project specification intake UI

Open **Open specification intake** from project metadata. The Angular routes are `/catalog/organizations/:organizationId/projects/:projectId/specs` and the same project path with `/specs/:bundleId`. The first creates a client-generated bundle identity in the URL; the second lets the user create that exact bundle through the API. No manually supplied UUID is needed. A bookmark or reload retains the bundle identity and reloads authoritative inventory.

The client uses generated `Spec*V1` contracts from `contracts/braid-v1.json`. It checks current catalog/session access before operations, binds results to the owner and route generation, checks organization scope, sends the current antiforgery token, and hides results whose context is no longer current. Metadata visibility is not content permission. The server decides content grants and backend admission; unavailable and denied responses are shown explicitly.

## Intake and recovery

- Refresh inventory and capabilities before choosing an upload format. The server may advertise **Plain text**, **JSON**, **OpenAPI JSON (structural extraction)**, **Word DOCX (partial text extraction)**, **Excel XLSX (stored cells only)** and **PDF (page text; OCR unavailable)**. Only advertised formats are offered. Extensions suggest a format; the selector remains explicit and bytes are uploaded unchanged.
- Use the advertised source limit: text/JSON formats are at most 10 MiB, Office formats at most 10 MiB under their worker profile, and PDF at most 8 MiB. Pasted text must fit the 1 MiB JSON request limit. The browser reads retained evidence; it does not parse Office or PDF files itself.
- The original and extraction have separate stable publication IDs/keys and CAS versions. Pending requests freeze the in-tab input. Retry reads inventory first, avoids a second upload when both publications are already confirmed, and only refreshes the extraction expected version while no extraction reservation exists. A stale original CAS is not silently rebased; the user may inspect inventory and explicitly discard a local attempt before starting another.
- Creation uses the bundle UUID as its stable creation key. Deletion retains its exact idempotency key and original expected version across uncertain outcomes. A missing bundle does not prove deletion or failed creation. A successful deletion receipt acknowledges logical deletion, not physical retirement.
- Original publication state and extraction outcome are separate. `Partial`, `LimitExceeded`, `Malformed`, `Unsupported` and pending states are explicit; no incomplete extraction is presented as successful generation. Office extraction covers body text or stored cells, not all document content. PDF retains page facts, reports OCR/encryption refusal and never claims OCR support. Evidence is rendered only as text, including JSON pointers and URLs. Citations identify the source/version, extraction ID/digest, ordinal and format-specific locator; see [structural evidence](spec-structural-evidence-ui.md).
- Work, selected files and pending request content remain only in component memory. Unsent-work integration, route guards and before-unload warn before losing it. After a confirmed reload, inventory remains recoverable but private bytes and exact in-tab retry keys are gone; the UI does not fabricate a retry for a pre-existing pending reservation. Re-selecting a file is a new operation only after explicit abandonment. Cross-owner admission clears private draft/recovery state; refused authentication hides it.

## Capabilities

The UI reads `GET /api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}/capabilities` after fresh inventory/access checks and before each incomplete upload attempt. A missing, unavailable, malformed or unknown-version response disables new intake, not freshly authorized inventory or evidence reads. Refresh retries capability discovery. Authentication or content denial still clears private state.

Specification intake is opt-in (`BRAID_SPEC_INTAKE_ENABLED`, false by default). Office/PDF additionally require the trusted host's installed worker, schema, profile and containment checks. Disabled or unavailable document workers advertise only text/JSON formats; attempted structural uploads fail closed. A `503 DocumentWorkerUnavailable` is not a valid document outcome. An original may already have been retained before worker failure, so reconcile the exact upload with inventory before retrying or abandoning it. A pre-send file refusal does not mean an earlier upload was undone.

Bundle URLs are the intake navigation mechanism. Continue through the
[proposal library](creation-proposal-library.md) and [review UI](creation-review-ui.md)
to generate and review a mapping. For server setup, see
[document admission](spec-document-intake.md) and [host configuration](spec-document-host.md).

## Verification

The maintained browser lane is `bun run test:spec-intake-browser`. It covers
capability discovery, structural uploads, Partial/refusal evidence, recovery and
access fencing using controlled API responses. It also checks page navigation,
pasted text, citation rendering, reload, deletion, CAS replay, lost-response
inventory reconciliation, identity changes, auth failures, mobile overflow and
accessibility. Run browser checks through the isolated-display procedure in
[development](development.md).

Use Bun 1.4.5 and the [frozen install commands](development.md#manual-hosting-and-frontend-development). Run `bun run test:spec-intake-browser` from the repository root. Set `BRAID_CHROMIUM_EXECUTABLE` when using a separately installed Chromium.
