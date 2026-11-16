# Authenticated creation runtime

This is the authenticated runtime contract for creating a new mapping from
retained specifications.

`BRAID_CREATION_ENABLED` defaults false. See [server composition](creation-host-composition.md)
for configuration requirements.

Users review exact evidence and assumptions, choose **Run sample**, and explicitly
choose **Approve and save this mapping**. Approval is proposal-specific; generation
and feedback never imply consent. The private mapping editor has a separate
**Save** action. After an unconfirmed creation save, **Check save status** recovers
the original approved operation rather than creating a replacement proposal/save.

## Evidence and review

Exact Partial proposal replay canonicalizes model notes through `CreationProposalNotes.Canonicalize`, a narrow Application entry point to the same `CreationEvidenceCoverage.Include` used by generation. Canonicalization precedes retained-note digest comparison. Exact explicit coverage is idempotent; altered or downgraded coverage returns `invalidCall`. Actual Office/PDF replay regressions assert identical fingerprint, one compilation, one preserved critical coverage assumption and no ingestion.

The loader uses the common internal `CreationEvidenceDocument.RestoreRetained(source, storedIdentity, canonicalEvidence)` boundary. It preserves nondefault stored profile pins and supports Complete byte and Partial Office/PDF evidence. Current content authorization, exact selected object identity/digest/size, aggregate limits and final inventory re-observation remain enforced. No source re-extraction occurs during loading.

Creation-enabled payload workspace policies use policy version 8 with four tool iterations. Production turn admission derives five provider rounds from this policy; other workspace/mapping policies still receive three tool iterations/four rounds. Duration, request/wire/text bytes, output tokens, eight tool calls and event budgets are unchanged. Independent evidence reads can share one tool batch; extra dependent pages and recovery discovery still consume the bounded turn budget and may require another turn. The full actual-provider-wire regression resolves an unfocused project, discovers a selection, reads its exact snapshot/evidence, proposes, then returns final text, asserting retained identity and a trusted citation.

Migration `0046_document_evidence.sql` supplies five-round support through
`chat_turns_max_rounds_check` and `chat_rounds_ordinal_check`, together with exact
chat schema admission. Runtime Domain/AI/application ordinal ceilings are 5;
production configuration assigns this only to creation-enabled workspace policies.
`SpecSchemaAdmission` checks both discovery signatures and restricted spec-reader
grants below.

## Activation and integration ports

`BRAID_CREATION_ENABLED` defaults false. `CreationRuntime.Configured` requires the specification factory, compiler, preview, current authority, proposal store, approval store, ingestion service/reader, `ICreationDiscovery` and `ICreationRuntimeAdmission`. Concrete server wiring is described in [Creation server composition](creation-host-composition.md). Shared admission verifies durable contracts; compiler/preview runners separately verify their actual service profile on every execution channel before sending frames, preserving authorized retained review during worker/source-store unavailability.

Compiler registration is `IMappingCreationCompiler` -> `FerruleMappingCreationCompiler` with its authoring backend. Preview registration is `IMappingCreationPreview` -> `FerruleMappingCreationPreview` with a separate backend using `DockerWorkerProfile.AuthoredPreview` or `FerruleServiceProtocol.AuthoredPreviewProfile`. The normal preview profile is incompatible. Production `CreationHostConfiguration` registers separate Ferrule service authoring/authored-preview runners, `PostgresMappingCreationStore` for proposal/approval/authority ports, `PostgresCreationDiscovery` and `CreationHostAdmission`. Protected reader, writer and approver connections have distinct restricted roles and passwords and share the admitted database placement with chat/specification dependencies. Every execution channel verifies its exact worker capability before sending frames.

The persistence contract includes `ChatPayloadSource.Specification = 5`, with immutable extraction object ID as subject and null version; `chat_payload_audit` and matching schema admission accept source kind 5. Workspace citation kind 12 (`CreationProposal`) uses the existing workspace citation event, with an additional canonical `proposalFingerprint` property only for that kind. Existing citation canonical encodings remain unchanged.

## Workspace tools

Only the payload-capable manifest offers these tools. Both the model profile's `PayloadsAllowed` and the creator's explicit current project payload grant are required. Selected specification reads check the creator/project grant around every asynchronous read and again before returning content. Actual shared extraction bytes create a durable `ChatPayloadDisclosureFact`; existing orchestration re-authorizes contributing projects before subsequent provider rounds. Payload-manifest history is excluded from provider history.

The complete creation tool manifest fits the 24-tool ceiling.

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

`ICreationDiscovery` and `PostgresCreationDiscovery` implement cross-turn lookup against the SQL contract, with `CreationDiscoveryService` re-reading every result through authorized inventory/proposal ports. Current project grants are rechecked around workspace discovery reads and before returning identifiers. Trusted policy version 8 instructs the model to discover retained proposals after uncertain retention rather than create replacements, treats specification/examples as untrusted evidence, and distinguishes retention from authenticated approval. The [provider contract](creation-provider-contract.md) defines the closed six-format v2 grammar, bounded calls and the separately implemented feedback handoff.

## Discovery SQL contract

The implemented PostgreSQL adapter uses the restricted `PostgresSpecBundleDatabase` Reader profile and exact schema admission. Selection discovery validates physical store admission. Proposal discovery relies on current actor/project content authorization and creator filtering in the owner function, so retained review remains discoverable after source-store deactivation or relocation. It requires these owner functions:

```sql
braid.spec_selection_discover_v1(actor uuid, org uuid, project uuid, store uuid,
    after_bundle uuid, after_object uuid, limit integer)
    -- RETURNS TABLE(bundle_id uuid, extraction_id uuid)
braid.creation_proposals_discover_v1(actor uuid, org uuid, project uuid,
    after_proposal uuid, limit integer)
    -- RETURNS TABLE(proposal_id uuid, fingerprint text)
```

Both require current actor/project content authority, bounded limits 1–20, nonzero UUIDs, and exclusive ascending PostgreSQL UUID keyset ordering. Selection cursors are both null or both populated; selections must reference published extractions and published originals in the bound store with live source/bundle. Proposal rows must belong to the authenticated creator in the exact organization/project, include terminal proposals and survive source retirement/staleness. Retention must commit before its identity becomes discoverable; discovery must not depend on a chat event, acknowledged response or surviving turn. Return at most the requested limit with canonical lowercase SHA-256 fingerprints. The adapter rejects duplicates, malformed rows and nonprogressing cursors.

Migration 0046 grants only the required reader execution permissions and retains
owner-function current-access fencing. `SpecSchemaAdmission` verifies exact
definitions, signatures and permissions; missing functions or stale admission
fail closed. Specification audit kind 5, Partial-format durability and five-round
chat checks are schema requirements.

## HTTP contracts

DTOs: `dotnet/src/Braid.Contracts/Creation/MappingCreationV1.cs`. Base:

`/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}`

- `GET ?expectedFingerprint=<lowercase SHA256>` returns `CreationProposalV1`.
- Collection `GET /api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals?afterProposal=<UUID>&limit=20` returns `CreationProposalPageV1`: `{proposals:[{proposalId,fingerprint,reviewPath}],afterProposal}`. Both query parameters are optional; the cursor is null after a short page.
- `GET /api/v1/organizations/{organizationId}/projects/{projectId}/creation-capabilities` reports current project creation capability; it does not approve a proposal or replace checks on subsequent actions.
- `POST /approve` takes `{fingerprint,acceptedCriticalAssumptions}`. All exact critical assumption IDs are required; critical questions block ingestion.
- `POST /recover` takes `{fingerprint}`. The application resumes only an existing approved ingestion operation.
- `POST /preview` takes `{fingerprint,sampleBase64,sampleDigest}`; sample bytes are at most 1 MiB and must match their digest. `CreationPreviewV1` binds proposal/native/sample and verified output format/media/digest; failed output fields are null.

Session/bearer admission uses the existing `CatalogRequestBoundary`, including session CSRF protection. Development identity is refused. Creator, project, fingerprint and current content authority are checked before and after operations. Reads and terminal receipt discovery survive selected-source staleness when content access remains authorized; preview and nonterminal approval/recovery still require a current exact snapshot. Responses are no-store, categorical failures contain no private exception text, and native bytes are never exposed by these DTOs.

## Verification

`PostgresCreationDiscoveryTests` uses the real PostgreSQL fixture, restricted
connections and owner functions. It checks inactive and relocated source stores:
retained proposals remain discoverable/readable, selections refuse, and content
grant revocation blocks discovery/readback.

Portable tests cover tool manifests, profile/grant boundaries, revocation between reads/rounds, exact retained evidence, multi-source selection, duplicate selection refusal, stable proposal replay, actual Rust compiler adapter invocation, substituted compiler/snapshot input, no ingestion during proposal creation, trusted citation encoding and revalidation, and HTTP OpenAPI/development-identity refusal.

`CreationRuntimeHttpTests` uses the Linux session-key fixture for authenticated
read/approval/recovery/preview, CSRF, critical decisions, wrong actors, revoked
content and committed receipt discovery after source staleness.
