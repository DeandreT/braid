# Project-owned mapping creation foundation

The format-authoring follow-up is documented in [Typed mapping creation plan v2](mapping-creation-formats.md).
It preserves the v1 canonical plan and adds a separate closed structured model for all six required formats.
The flat-JSON restrictions below describe the original v1 foundation.

Creation foundation in `C:/example/checkout`, initially based on `014f2f79` and fast-forwarded to the authorized integrated `075cbb36` after preserving the uncommitted unit. The integration includes the reviewed native compiler. The current unit adds Domain/Application code, its real MappingBackends compiler adapter, and tests. Production registration and durable storage are subsequent integration work.

## Ownership and immutable content

`CreationSpecification` retains actual immutable captured sources and extracted evidence alongside the existing `SpecBundleSnapshot`. Every selected extraction must match one captured source's identity, version, format and byte digest; aggregate source and evidence budgets are each 20 MiB, with the existing 20-source cap. Incomplete extractions cannot support generation. Citations identify existing chunks of the selected extractions. Source text remains untrusted evidence.

`MappingCreationPlan` is a closed typed model-output boundary, not a native JSON slot. Its strict parser rejects missing, unknown and duplicate properties, unsupported versions, incompatible/duplicate bindings and missing required target bindings. The application subset is flat JSON records with explicit ordered fields and direct compatible bindings: Text, Number (compiler float), Boolean. Both the application plan and emitted compiler plan are bounded to 64 KiB and 256 fields/bindings. Unsupported EDI, XML, nested schemas, expressions and inference are not silently guessed or advertised.

`CopyBytes()` is the canonical `weft.creation-plan.v1` application document. `CopyCompilerPlanBytes()` deterministically emits the actual `weft.authoring.v1` grammar with explicit Input/Output JSON schemas and `ferrule.edit.v1` bind operations. Each source expression receives its own stable invariant-culture ID (binding index plus one), including separate expressions reading the same source. Reusing ID 1 for every bind fails pinned Ferrule's occupied-node check on the second operation. Both plan digests are pinned. These are distinct contracts and must not be substituted at the transport boundary.

`MappingCreationProposal` has an author, project, proposal/authoring ID and stable ingestion request key. It has no mapping or revision identity. Its digest binds those identities, spec snapshot, both plan digests, notes, exact native bytes, engine/worker provenance, validation/projection evidence, name, description, backend and filename. Native and report bytes are defensively copied. Creation compilation and proposal constructors are internal; public deserialization cannot mint native generation evidence or an approvable proposal.

## Compiler adapter integration contract

Application depends on Domain/Contracts, never MappingBackends. `IMappingCreationCompiler.CompileAsync(project, authoringId, plan, snapshot, ct)` returns exactly checkpoint or `MappingEditFailure`. `FerruleMappingCreationCompiler` now implements it using the actual `FerruleInspectionBackend`; integrated assembly friendship permits the trusted adapter to construct internal generation evidence.

The adapter:

1. Calls `FerruleInspectionBackend.AuthorAsync(project, authoringId, plan.CopyCompilerPlanBytes(), ct)` through the actual authoring boundary.
2. Accepts only its verified `MappingAuthoringCheckpoint`. It preserves categorical failure and the exclusive result/failure shape.
3. Checks exact project, authoring ID, `PlanDigest == plan.CompilerDigest`, native digest/count, and retains `EngineRevision`, `WorkerBuild`, and bounded immutable serializations of the actual Validation and Projection objects when constructing `CreationCompilation`.
4. Binds the private application snapshot digest to that invocation. The compiler does not receive or authenticate the spec snapshot: this is application-owned provenance, not a claimed compiler echo or evidence of requirements correctness.

Generation rechecks current authority after compiler awaits and verifies checkpoint project/authoring/plan/snapshot identity. A checkpoint's existence is not validation success, requirement coverage, or approval. Actual ingestion still performs its existing native admission. The typed compiler translation and real adapter are qualified against the portable Rust compiler harness; production sandbox execution remains a separate gate.

## Approval, assumptions and recovery

Generation, retention, discovery and ephemeral preview have no catalog-create action. Preview executes synchronously through a separate unimplemented verified-runner port with project/authoring identity, proposal fingerprint, native digest and sample digest, plus bounded sample/output evidence. It never invents a `RevisionScope` or requires a queued `preview_runs` reservation. Its opaque evidence constructor is internal to trusted adapters; results are checked against the entire expected subject and withheld if authority is no longer current. Validation alone cannot mint execution evidence or label a proposal ready. The separate mixed-format runner must be integrated and qualified before this port can execute real native samples; the recording fixture is not execution qualification.

`CreationGenerationNotes` retains bounded immutable citations, named assumptions and unresolved questions. Critical questions block approval. Resolving them requires new generation/proposal content and a new review. Critical assumptions require explicit acknowledgement of the exact complete set of assumption IDs in a `CreationApprovalDecision` bound to the proposal fingerprint. Missing, extra, duplicate or foreign acknowledgements cannot approve. A decision object is intent only; model generation cannot grant authority by constructing one.

Only a dedicated authenticated caller may invoke `MappingCreationApprovalService.ApproveAsync`. The future endpoint must enforce its real actor/session and deliberate-request protections. Its durable approval store is separate from the proposal-retention writer. The store must bind exact decisions to actor/project/proposal/fingerprint/key, handle exact replay, deny substitutions and enforce critical-question rules itself.

The approval lease contract requires current publisher/content authority, exact spec versions/tombstones, and project lifecycle fencing through dispatch/commit. A check-then-act adapter is not sufficient. This contract is not implemented or claimed qualified here. Schema/runtime integration must prove lock ordering and invalidation fencing with actual ingestion; it must not activate the feature with a permissive fake lease.

Approval calls the existing `IRevisionIngestionService.CreateAsync` with retained exact bytes, metadata and the original stable key. It never calls Append. Lost acknowledgement is reconciled through the real `IIngestionWorkflowReader.FindOwnReceiptAsync` create-key lookup. Full receipt actor/project/key/native/metadata intent is compared before exposing or resuming it. No acknowledgement is treated as success without durable evidence.

Recovery requires an existing approved operation and calls only `ResumeAsync`. Approved-but-unreserved state remains unreserved until another explicit approval. NeedsSource recovery does not repair bytes or allocate a new key. Terminal durable receipts are read before staleness refusals; revoked content authority still hides reads. Ingestion retains its own current Publisher checks and admission/commit semantics.

## Verification and next unit

Pinned SDK 10.0.112 locked restore succeeded. The focused selection of `MappingCreationWorkflowTests`, `MappingCreationCompilerTests`, `MappingAuthoringBackendTests`, `RevisionIngestionServiceTests`, `ChatMappingEditApprovalTests` and `SpecSourceExtractorTests` passed **149 tests, zero failures/skips**. The native harness was built in this worktree using `cargo +1.98.1 build --locked --offline -p weft-ferrule-core --example authoring_protocol`, then selected with `WEFT_AUTHORING_TEST_EXECUTABLE` for the .NET run. Both multi-binding tests reproduce the old second-operation failure, compile the corrected typed plans through the real adapter, verify two distinct output rules and actual validation/projection evidence, and repeat generation to check deterministic native bytes. They cover distinct sources and one source feeding two targets. Other coverage includes no-ingestion generation/read/preview, exact retention replay/conflicts, lost acknowledgements, stable bytes/key/metadata, existing-operation-only recovery, foreign/fingerprint/source/compiler substitutions, stale/revoked authority, schema bounds and explicit critical-assumption decisions. Portable harness qualification does not establish production sandbox or durable PostgreSQL fencing.

The requested next unit is durable project-owned authoring sessions, snapshots, proposals and decisions in migration 0045 on the integrated 0044 baseline, followed by authenticated runtime composition with the verified compiler adapter. Separate follow-ups must admit actual Office/PDF Partial structural evidence through a closed evidence union with explicit coverage warnings/critical acknowledgements, and expand the typed plan to all required formats. This unit retains the complete-extraction and flat-JSON restrictions rather than guessing EDI schemas or accepting arbitrary model native bytes. The coordinator owns those follow-ups. This coherent foundation and P1 correction are authorized for a local personal-identity commit after verification.
