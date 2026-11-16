# Project-owned mapping creation

Creation retains immutable specification evidence, typed plans and compiler results
under an author and project. Its contracts are described in:

- [Typed creation formats](mapping-creation-formats.md): plan grammar, canonical
  bytes, independent digests and format-specific limits.
- [Creation evidence](creation-evidence-generation.md) and
  [evidence storage](creation-evidence-storage.md): selected Office/PDF evidence
  and coverage acknowledgements.
- [Durable creation](mapping-creation-persistence.md): proposals, approval
  decisions, transactional fences and ingestion-key reconciliation.
- [Creation runtime](mapping-creation-runtime.md) and
  [host composition](creation-host-composition.md): generation, preview, approval,
  recovery and compiler/preview runners.
- [Exact review](creation-review-ui.md) and
  [proposal library](creation-proposal-library.md): review, discovery and feedback.

## Ownership and immutable content

`CreationSpecification` retains captured sources and extracted evidence alongside
`SpecBundleSnapshot`. Each extraction matches a selected source's identity,
version, format and digest. Source and evidence budgets are each 20 MiB, with at
most 20 sources. Citations identify selected extraction chunks. Source text is
untrusted evidence.

`MappingCreationPlan` is a closed typed model-output boundary. Parsing rejects
missing, unknown and duplicate properties, unsupported versions, incompatible or
duplicate bindings and missing required targets. Its application bytes and compiler
bytes are distinct canonical contracts, each with an independent digest.

`MappingCreationProposal` binds author, project, proposal/authoring ID, stable
ingestion key, specification snapshot, both plan digests, notes, exact native
bytes, engine/worker provenance, validation/projection evidence and destination
metadata. It has no mapping or revision identity until saved. Native and report
bytes are copied. Internal constructors keep public deserialization from minting
native generation evidence.

## Compiler adapter

Application depends on Domain/Contracts. The MappingBackends adapter implements
`IMappingCreationCompiler.CompileAsync(project, authoringId, plan, snapshot, ct)`:

1. Call `FerruleInspectionBackend.AuthorAsync` with `plan.CopyCompilerPlanBytes()`.
2. Accept its verified `MappingAuthoringCheckpoint` or categorical
   `MappingEditFailure`, preserving the exclusive result/failure shape.
3. Check project, authoring ID, compiler plan digest, native digest/count and
   retain engine revision, worker build and bounded validation/projection bytes.
4. Bind the application-owned snapshot digest to that invocation.

Generation rechecks authority after compiler awaits and verifies the complete
checkpoint subject. Ingestion performs native admission when the proposal is saved.

## Approval, assumptions and recovery

Generation, retention, discovery and ephemeral preview leave the catalog unchanged.
Preview uses a verified runner with project/authoring identity, proposal fingerprint,
native digest and sample digest. It checks the complete result subject and current
authority without inventing a saved revision or queued preview reservation.

`CreationGenerationNotes` retains citations, named assumptions and unresolved
questions. Critical questions block approval and require a new proposal to resolve.
Critical assumptions require the exact complete set of IDs in a
`CreationApprovalDecision` bound to the fingerprint. Missing, extra, duplicate or
foreign acknowledgements fail.

The authenticated approval service and separate durable approval store bind the
decision to actor, project, proposal, fingerprint and stable key. Current publisher
and content authority, specification versions/tombstones and lifecycle fencing
remain held through dispatch/commit. Approval calls
`IRevisionIngestionService.CreateAsync` with retained bytes and metadata.

Lost acknowledgements are reconciled through
`IIngestionWorkflowReader.FindOwnReceiptAsync`. Compare the full actor/project/key,
native and metadata intent before exposing or resuming a receipt. Recovery requires
an approved existing operation and calls only `ResumeAsync`. Approved-but-unreserved
work needs another explicit approval; NeedsSource recovery does not replace bytes
or allocate a key. Read terminal receipts before staleness refusals, but hide content
when its authority is revoked.
