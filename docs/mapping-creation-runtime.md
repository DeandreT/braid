# Authenticated creation runtime

Implementation worktree: `C:/example/checkout`, branch `feat/v1-creation-runtime`. Reviewed codec/dependencies through `0d318905` are integrated in a pending uncommitted merge; runtime changes remain uncommitted for review.

## Renewed review integration

Exact Partial proposal replay canonicalizes model notes through `CreationProposalNotes.Canonicalize`, a narrow Application entry point to the same `CreationEvidenceCoverage.Include` used by generation. Canonicalization precedes retained-note digest comparison. Exact explicit coverage is idempotent; altered or downgraded coverage returns `invalidCall`. Actual Office/PDF replay regressions assert identical fingerprint, one compilation, one preserved critical coverage assumption and no ingestion.

The loader now uses the common internal `CreationEvidenceDocument.RestoreRetained(source, storedIdentity, canonicalEvidence)` boundary. It preserves nondefault stored profile pins and supports admitted Complete legacy plus Partial Office/PDF evidence. Current content authorization, exact selected object identity/digest/size, aggregate limits and final inventory re-observation remain enforced. Tests use actual Office parsing and native PDF output, including blank pages and mandatory critical coverage. No source re-extraction occurs during loading.

Creation-enabled payload workspace policies use policy version 7 with four tool iterations. Production turn admission derives five provider rounds from this policy; other workspace/mapping policies still receive three tool iterations/four rounds. Duration, request/wire/text bytes, output tokens, eight tool calls and event budgets are unchanged. Independent evidence reads can share one tool batch; extra dependent pages and recovery discovery still consume the bounded turn budget and may require another turn. The full actual-provider-wire regression resolves an unfocused project, discovers a selection, reads its exact snapshot/evidence, proposes, then returns final text, asserting retained identity and a trusted citation.

Five-round support also requires the 0046 owner to change `chat_turns_max_rounds_check` and `chat_rounds_ordinal_check` from 4 to 5 and update exact chat schema admission. Runtime Domain/AI/application ordinal ceilings are 5; production configuration assigns this only to creation-enabled workspace policies. Durable activation remains gated until these database dependencies are qualified.

The 0046 owner's current source now declares both discovery signatures and restricted spec-reader grants below; `SpecSchemaAdmission` names both functions. This is source-level alignment, not committed integration or durable qualification.

## Activation and integration ports

`WEFT_CREATION_ENABLED` defaults false. `CreationRuntime.Configured` requires the specification factory, compiler, preview, current authority, proposal store, approval store, ingestion service/reader, `ICreationDiscovery` and `ICreationRuntimeAdmission`. The last port must verify concrete durable creation fencing, Specification disclosure storage and fixed compiler/authored-preview profiles. The composition owner supplies production admission after those contracts are admitted. Enabling the flag alone leaves tools and endpoints unavailable.

Compiler registration is `IMappingCreationCompiler` -> `FerruleMappingCreationCompiler` with its authoring backend. Preview registration is `IMappingCreationPreview` -> `FerruleMappingCreationPreview` with a separate backend using `DockerWorkerProfile.AuthoredPreview` or `FerruleServiceProtocol.AuthoredPreviewProfile`. The normal preview profile is incompatible. Durable store/authority implementations remain the persistence owner's integration.

Required persistence coordination: `ChatPayloadSource.Specification = 5`, with immutable extraction object ID as subject and null version, requires `chat_payload_audit` to accept source kind 5 and matching schema admission. The new workspace citation is kind 12 (`CreationProposal`), carried through the existing workspace citation event, with an additional canonical `proposalFingerprint` property only for that kind. Existing citation canonical encodings remain unchanged.

## Workspace tools

Only the payload-capable manifest offers these tools. Both the model profile's `PayloadsAllowed` and the creator's explicit current project payload grant are required. Selected specification reads check the creator/project grant around every asynchronous read and again before returning content. Actual shared extraction bytes create a durable `ChatPayloadDisclosureFact`; existing orchestration re-authorizes contributing projects before subsequent provider rounds. Payload-manifest history is excluded from provider history.

All standard features plus creation fit the existing 24-tool ceiling. Adding legacy direct-save to that complete manifest exceeds the ceiling and refuses configuration; keep legacy direct-save off for that composition.

Evidence reads and proposals require `projectId`, `bundleId`, and `extractionIds` (1–20 distinct UUIDs). Organization and actor come from the authenticated conversation. Selection is sorted by extraction object ID, verifies each published original/extraction pair, checks original/evidence bytes and digests, restores the exact retained evidence, then re-observes the selected inventory records. Original and evidence aggregate limits are each 20 MiB. Unrelated bundle-version changes do not invalidate selected content. `SpecInventoryItem.Extraction` carries stored pins; PostgreSQL obtains these through existing `spec_object_gate` columns. The common restorer preserves the exact stored identity even when another profile could produce equal evidence bytes.

`read_specification` supports `mode: "selections" | "proposals" | "evidence"` (default `evidence`). Discovery takes only `projectId`, optional cursors and `limit` (1–20). Selections return bundle/extraction/source IDs, stored profile/version, format/outcome and evidence digest/size; the next cursor is `{afterBundle,afterObject}`. Proposals return exact IDs/fingerprints/review links plus trusted citation candidates; the next cursor is `{afterProposal}`. Full pages return a cursor; a following empty page completes iteration. Cursor fields cannot be mixed with evidence arguments. Discovery is independent of turns, events and retained model history.

Evidence mode additionally accepts `sourceIndex` (0–19, default 0), `offset` and `limit` (1–20). It returns one selected source's bounded chunk page, its source/extraction digests and identity, the ordered selected-source index, and aggregate `snapshotDigest`. This keeps each disclosure fact tied to the exact extraction shared. An oversized first chunk returns `resourceLimit`, never an available page with an unchanged cursor. Chunk text is evidence, never authority.

`propose_mapping_creation` requires `snapshotDigest`, `name`, `plan`, and `notes`. `plan` is parsed by the actual closed `MappingCreationPlan.Parse` contract; `notes` is a strict object with:

```json
{"evidence":[{"sourceId":"<UUID>","sourceVersion":1,"chunkOrdinal":0}],"assumptions":[{"id":"a1","text":"Review this assumption","critical":true}],"questions":[]}
```

Question objects are `{text,critical}`. The complete call is bounded to 16 KiB. The generation service compiles through the registered real adapter and verifies all citations against selected sources. Proposal and ingestion identities are deterministic from trusted conversation/turn/round/call context. Retention preserves exact fingerprint and native evidence. An exact repeated call reuses its retained proposal; substituted input conflicts. The model manifest contains neither approval nor recovery.

The result includes `projectId`, `proposalId`, `fingerprint`, `snapshotDigest`, `planDigest`, `nativeDigest`, `requiresApproval` and server-generated `reviewPath`. A trusted `workspace.citation` candidate is also returned and freshly verified against the creator-owned stored proposal. Its public shape is `kind: "creationProposal"`, organization/project IDs, `subjectId` = proposal ID, and `proposalFingerprint`; other citation slots are null. The UI route is:

`/catalog/organizations/{org}/projects/{project}/creation/{proposal}?fingerprint={fingerprint}`

`ICreationDiscovery` and `PostgresCreationDiscovery` implement cross-turn lookup against the pending SQL contract, with `CreationDiscoveryService` re-reading every result through authorized inventory/proposal ports. Current project grants are rechecked around workspace discovery reads and before returning identifiers. Trusted policy version 7 instructs the model to discover retained proposals after uncertain retention rather than create replacements, treats specification/examples as untrusted evidence, and distinguishes retention from authenticated approval.

## 0046 SQL owner handoff

The implemented PostgreSQL adapter uses the restricted `PostgresSpecBundleDatabase` Reader profile and validates store admission. It requires these owner functions:

```sql
weft.spec_selection_discover_v1(actor uuid, org uuid, project uuid, store uuid,
    after_bundle uuid, after_object uuid, limit integer)
    -- RETURNS TABLE(bundle_id uuid, extraction_id uuid)
weft.creation_proposals_discover_v1(actor uuid, org uuid, project uuid,
    after_proposal uuid, limit integer)
    -- RETURNS TABLE(proposal_id uuid, fingerprint text)
```

Both require current actor/project content authority, bounded limits 1–20, nonzero UUIDs, and exclusive ascending PostgreSQL UUID keyset ordering. Selection cursors are both null or both populated; selections must reference published extractions and published originals in the bound store with live source/bundle. Proposal rows must belong to the authenticated creator in the exact organization/project, include terminal proposals and survive source retirement/staleness. Retention must commit before its identity becomes discoverable; discovery must not depend on a chat event, acknowledged response or surviving turn. Return at most the requested limit with canonical lowercase SHA-256 fingerprints. The adapter rejects duplicates, malformed rows and nonprogressing cursors.

0046 owner must grant only the required reader execution permissions, retain owner-function current-access fencing, and update exact `SpecSchemaAdmission` definitions/signatures/permissions. Missing functions or stale admission fail closed. Specification audit kind 5, Partial-format durability and five-round chat checks remain owner 0046 dependencies. This worktree includes qualified 0045 and the common evidence codec through the pending merge.

## HTTP contracts

DTOs: `dotnet/src/Weft.Contracts/Creation/MappingCreationV1.cs`. Base:

`/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}`

- `GET ?expectedFingerprint=<lowercase SHA256>` returns `CreationProposalV1`.
- Collection `GET /api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals?afterProposal=<UUID>&limit=20` returns `CreationProposalPageV1`: `{proposals:[{proposalId,fingerprint,reviewPath}],afterProposal}`. Both query parameters are optional; the cursor is null after a short page.
- `POST /approve` takes `{fingerprint,acceptedCriticalAssumptions}`. All exact critical assumption IDs are required; critical questions block ingestion.
- `POST /recover` takes `{fingerprint}`. The application resumes only an existing approved ingestion operation.
- `POST /preview` takes `{fingerprint,sampleBase64,sampleDigest}`; sample bytes are at most 1 MiB and must match their digest. `CreationPreviewV1` binds proposal/native/sample and verified output format/media/digest; failed output fields are null.

Session/bearer admission uses the existing `CatalogRequestBoundary`, including session CSRF protection. Development identity is refused. Creator, project, fingerprint and current content authority are checked before and after operations. Reads and terminal receipt discovery survive selected-source staleness when content access remains authorized; preview and nonterminal approval/recovery still require a current exact snapshot. Responses are no-store, categorical failures contain no private exception text, and native bytes are never exposed by these DTOs.

## Verification

Portable tests cover tool manifests, profile/grant boundaries, revocation between reads/rounds, exact retained evidence, multi-source selection, duplicate selection refusal, stable proposal replay, actual Rust compiler adapter invocation, substituted compiler/snapshot input, no ingestion during proposal creation, trusted citation encoding and revalidation, and HTTP OpenAPI/development-identity refusal.

`CreationRuntimeHttpTests` includes three Linux session-profile tests for authenticated read/approval/recovery/preview, CSRF, critical decisions, wrong actors, revoked content and committed receipt discovery after source staleness. Those require the existing Linux session-key fixture; they are not executed by the Windows lane. Portable recording ports are not durable PostgreSQL or runtime sandbox qualification.

Latest post-codec focused selection: 171 passed, three Linux session tests skipped. Covers creation runtime, orchestration/AI turn limits, workspace history/payload reauthorization/OpenAPI, the full four-batch wire path, actual Office/native-PDF loader restoration and Partial proposal replay. Actual native compiler and preview adapters were enabled. The PostgreSQL inventory regression passed against local PostgreSQL 16.15 with a stored maximumChunks=100 profile. Durable discovery, five-round database persistence and citation kind 12/fingerprint storage still need qualification against the owner's 0046 functions/checks. Existing `ChatHostConfigurationTests` require Unix file modes and need the Linux lane along with the three authenticated HTTP cases.
