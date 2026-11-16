# Authenticated creation runtime

This is the authenticated runtime contract for creating a new mapping from
retained specifications. Concrete server composition, discovery and ordered
migrations through 0047 exist in source. They do not enable creation on an
unconfigured host or establish real-provider/cloud acceptance.

`WEFT_CREATION_ENABLED` defaults false. See [server composition](creation-host-composition.md)
for activation requirements and the [delivery plan](../ROADMAP.md#remaining-acceptance-gates)
for the user-deferred provider and Azure gates. Branch-era implementation and
verification checkpoints remain in [the integration record](v1-integration.md)
and the [original runtime record](https://github.com/DeandreT/braid/blob/25e7a6c9924291081960494fa9eed707be1aa58b/docs/mapping-creation-runtime.md).

Users review exact evidence and assumptions, choose **Run sample**, and explicitly
choose **Approve and save this mapping**. Approval is proposal-specific; generation
and feedback never imply consent. The private mapping editor has a separate
**Save** action. After an unconfirmed creation save, **Check save status** recovers
the original approved operation rather than creating a replacement proposal/save.

## Renewed review integration

Exact Partial proposal replay canonicalizes model notes through `CreationProposalNotes.Canonicalize`, a narrow Application entry point to the same `CreationEvidenceCoverage.Include` used by generation. Canonicalization precedes retained-note digest comparison. Exact explicit coverage is idempotent; altered or downgraded coverage returns `invalidCall`. Actual Office/PDF replay regressions assert identical fingerprint, one compilation, one preserved critical coverage assumption and no ingestion.

The loader now uses the common internal `CreationEvidenceDocument.RestoreRetained(source, storedIdentity, canonicalEvidence)` boundary. It preserves nondefault stored profile pins and supports admitted Complete legacy plus Partial Office/PDF evidence. Current content authorization, exact selected object identity/digest/size, aggregate limits and final inventory re-observation remain enforced. Tests use actual Office parsing and native PDF output, including blank pages and mandatory critical coverage. No source re-extraction occurs during loading.

Creation-enabled payload workspace policies use policy version 8 with four tool iterations. Production turn admission derives five provider rounds from this policy; other workspace/mapping policies still receive three tool iterations/four rounds. Duration, request/wire/text bytes, output tokens, eight tool calls and event budgets are unchanged. Independent evidence reads can share one tool batch; extra dependent pages and recovery discovery still consume the bounded turn budget and may require another turn. The full actual-provider-wire regression resolves an unfocused project, discovers a selection, reads its exact snapshot/evidence, proposes, then returns final text, asserting retained identity and a trusted citation.

Migration `0046_document_evidence.sql` supplies five-round support through
`chat_turns_max_rounds_check` and `chat_rounds_ordinal_check`, together with exact
chat schema admission. Runtime Domain/AI/application ordinal ceilings are 5;
production configuration assigns this only to creation-enabled workspace policies.
The target database must pass current contract admission; a source migration is
not proof that an installation has applied it.

Migration 0046 declares both discovery signatures and restricted spec-reader
grants below; `SpecSchemaAdmission` checks both functions. Later ordered migration
0047 restores the exact document JSON helper contract. Keep all applied migrations
intact; the [integration record](v1-integration.md#migration-0046-integration-verification)
describes the bounded database verification rather than a pending owner handoff.

## Activation and integration ports

`WEFT_CREATION_ENABLED` defaults false. `CreationRuntime.Configured` requires the specification factory, compiler, preview, current authority, proposal store, approval store, ingestion service/reader, `ICreationDiscovery` and `ICreationRuntimeAdmission`. Concrete server wiring is described in [Creation server composition](creation-host-composition.md). Shared admission verifies durable contracts; compiler/preview runners separately verify their actual service profile on every execution channel before sending frames, preserving authorized retained review during worker/source-store unavailability.

Compiler registration is `IMappingCreationCompiler` -> `FerruleMappingCreationCompiler` with its authoring backend. Preview registration is `IMappingCreationPreview` -> `FerruleMappingCreationPreview` with a separate backend using `DockerWorkerProfile.AuthoredPreview` or `FerruleServiceProtocol.AuthoredPreviewProfile`. The normal preview profile is incompatible. Production `CreationHostConfiguration` registers separate Ferrule service authoring/authored-preview runners, `PostgresMappingCreationStore` for proposal/approval/authority ports, `PostgresCreationDiscovery` and `CreationHostAdmission`. Protected reader, writer and approver connections have distinct restricted roles and passwords and share the admitted database placement with chat/specification dependencies. Every execution channel verifies its exact worker capability before sending frames.

The persistence contract includes `ChatPayloadSource.Specification = 5`, with immutable extraction object ID as subject and null version; `chat_payload_audit` and matching schema admission accept source kind 5. Workspace citation kind 12 (`CreationProposal`) uses the existing workspace citation event, with an additional canonical `proposalFingerprint` property only for that kind. Existing citation canonical encodings remain unchanged.

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

`ICreationDiscovery` and `PostgresCreationDiscovery` implement cross-turn lookup against the SQL contract, with `CreationDiscoveryService` re-reading every result through authorized inventory/proposal ports. Current project grants are rechecked around workspace discovery reads and before returning identifiers. Trusted policy version 8 instructs the model to discover retained proposals after uncertain retention rather than create replacements, treats specification/examples as untrusted evidence, and distinguishes retention from authenticated approval. The [provider contract](creation-provider-contract.md) defines the closed six-format v2 grammar, bounded calls and the separately implemented feedback handoff.

## 0046 SQL owner handoff

This retained heading names the original handoff. The functions are now part of
the ordered schema and exact admission contract, not instructions to invent a
new successor migration.

The implemented PostgreSQL adapter uses the restricted `PostgresSpecBundleDatabase` Reader profile and exact schema admission. Selection discovery validates physical store admission. Proposal discovery relies on current actor/project content authorization and creator filtering in the owner function, so retained review remains discoverable after source-store deactivation or relocation. It requires these owner functions:

```sql
weft.spec_selection_discover_v1(actor uuid, org uuid, project uuid, store uuid,
    after_bundle uuid, after_object uuid, limit integer)
    -- RETURNS TABLE(bundle_id uuid, extraction_id uuid)
weft.creation_proposals_discover_v1(actor uuid, org uuid, project uuid,
    after_proposal uuid, limit integer)
    -- RETURNS TABLE(proposal_id uuid, fingerprint text)
```

Both require current actor/project content authority, bounded limits 1–20, nonzero UUIDs, and exclusive ascending PostgreSQL UUID keyset ordering. Selection cursors are both null or both populated; selections must reference published extractions and published originals in the bound store with live source/bundle. Proposal rows must belong to the authenticated creator in the exact organization/project, include terminal proposals and survive source retirement/staleness. Retention must commit before its identity becomes discoverable; discovery must not depend on a chat event, acknowledged response or surviving turn. Return at most the requested limit with canonical lowercase SHA-256 fingerprints. The adapter rejects duplicates, malformed rows and nonprogressing cursors.

Migration 0046 grants only the required reader execution permissions and retains
owner-function current-access fencing. `SpecSchemaAdmission` verifies exact
definitions, signatures and permissions; missing functions or stale admission
fail closed. Specification audit kind 5, Partial-format durability and five-round
chat checks are integrated schema requirements. Actual host activation still
requires the target database, content store and execution profiles to pass their
own admission checks.

## HTTP contracts

DTOs: `dotnet/src/Weft.Contracts/Creation/MappingCreationV1.cs`. Base:

`/api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals/{proposalId}`

- `GET ?expectedFingerprint=<lowercase SHA256>` returns `CreationProposalV1`.
- Collection `GET /api/v1/organizations/{organizationId}/projects/{projectId}/creation-proposals?afterProposal=<UUID>&limit=20` returns `CreationProposalPageV1`: `{proposals:[{proposalId,fingerprint,reviewPath}],afterProposal}`. Both query parameters are optional; the cursor is null after a short page.
- `GET /api/v1/organizations/{organizationId}/projects/{projectId}/creation-capabilities` reports current project creation capability; it does not approve a proposal or replace checks on subsequent actions.
- `POST /approve` takes `{fingerprint,acceptedCriticalAssumptions}`. All exact critical assumption IDs are required; critical questions block ingestion.
- `POST /recover` takes `{fingerprint}`. The application resumes only an existing approved ingestion operation.
- `POST /preview` takes `{fingerprint,sampleBase64,sampleDigest}`; sample bytes are at most 1 MiB and must match their digest. `CreationPreviewV1` binds proposal/native/sample and verified output format/media/digest; failed output fields are null.

Session/bearer admission uses the existing `CatalogRequestBoundary`, including session CSRF protection. Development identity is refused. Creator, project, fingerprint and current content authority are checked before and after operations. Reads and terminal receipt discovery survive selected-source staleness when content access remains authorized; preview and nonterminal approval/recovery still require a current exact snapshot. Responses are no-store, categorical failures contain no private exception text, and native bytes are never exposed by these DTOs.

## Verification

Current source includes portable contract tests, actual-adapter PostgreSQL
discovery tests, authenticated session HTTP tests and native compiler/preview
checks. Their prerequisites and evidence are separate: synthetic ports do not
qualify durable storage, controlled browser responses do not qualify a provider,
and local worker checks do not qualify another installation. The later
[integration verification](v1-integration.md#migration-0046-integration-verification),
[server-composition record](creation-host-composition.md) and
[sandbox qualification](creation-sandbox-qualification.md) document their actual
scope. Real-provider and Azure acceptance remain deferred.

### Historical pre-0046 runtime checkpoint

The paragraphs below retain the original focused runtime verification record.
Its “pending”, “skipped” and “still need qualification” statements describe that
earlier branch checkpoint, not the current migration inventory. Do not turn
these overlapping selections into a whole-project result or deployment claim.

`PostgresCreationDiscoveryTests` adds two actual-adapter cases (inactive and relocated source store): retained proposals remain discoverable/readable, selections refuse, and subsequent content-grant revocation blocks discovery/readback. The theory explicitly skips until reviewed 0046 is embedded, then requires the real PostgreSQL fixture. It uses real restricted connections and owner functions, with synthetic source/native fixtures and registry publication callbacks. The focused follow-up run passed two portable discovery tests and skipped this pending-0046 theory; it does not qualify the new database cases.

Portable tests cover tool manifests, profile/grant boundaries, revocation between reads/rounds, exact retained evidence, multi-source selection, duplicate selection refusal, stable proposal replay, actual Rust compiler adapter invocation, substituted compiler/snapshot input, no ingestion during proposal creation, trusted citation encoding and revalidation, and HTTP OpenAPI/development-identity refusal.

`CreationRuntimeHttpTests` includes three Linux session-profile tests for authenticated read/approval/recovery/preview, CSRF, critical decisions, wrong actors, revoked content and committed receipt discovery after source staleness. Those require the existing Linux session-key fixture; they are not executed by the Windows lane. Portable recording ports are not durable PostgreSQL or runtime sandbox qualification.

Latest post-codec focused selection: 171 passed, three Linux session tests skipped. Covers creation runtime, orchestration/AI turn limits, workspace history/payload reauthorization/OpenAPI, the full four-batch wire path, actual Office/native-PDF loader restoration and Partial proposal replay. Actual native compiler and preview adapters were enabled. The PostgreSQL inventory regression passed against local PostgreSQL 16.15 with a stored maximumChunks=100 profile. Durable discovery, five-round database persistence and citation kind 12/fingerprint storage still need qualification against the owner's 0046 functions/checks. Existing `ChatHostConfigurationTests` require Unix file modes and need the Linux lane along with the three authenticated HTTP cases.
