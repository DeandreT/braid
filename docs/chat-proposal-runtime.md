# Exact chat edit approval runtime

This document owns the runtime approval and recovery contract for #437. Read it
with [PostgreSQL and host composition](chat-edit-approval-composition.md) and
[browser proposal review](chat-proposal-review-ui.md). Those are separate
implementation boundaries, not missing pieces of the integrated source. Source
presence alone does not establish a complete production rollout.

## Model boundary

The default edit manifest offers `propose_mapping_edit`, not `save_mapping_edit`.
It accepts only mapping ID, exact base revision ID, and a typed `ferrule.edit.v1`
script. It calls `MappingDirectEditService.ProposeAsync`, then retains the result
through `ChatMappingEditApprovalService.CreateAsync`. It never prepares ingestion
or calls `ApplyAsync`. The durable turn listing recovers a proposal whose tool
result or SSE delivery was lost.

`WEFT_CHAT_LEGACY_DIRECT_SAVE_ENABLED=true` is the explicit compatibility escape
hatch for the old tool. Its default is false. Both manifest and dispatch enforce
the capability; catalog edit permission alone cannot enable it. This switch is
server configuration, never a turn flag or model argument. Grounding policy v5
does not reuse historical v4 manifests.

Migration 0043 revokes the writer's legacy prepare grant and rejects pending
legacy writes. This runtime flag cannot bypass that database gate; it preserves
the older runtime path only where the database still explicitly admits it.
Quiesce old execution/recovery at migration cutover. Legacy pending work needs a
fresh proposal and exact approval after 0043.

## HTTP contract

All paths below are beneath
`/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}`:

- `GET /mapping-edit-proposals`: at most eight authorized proposal summaries.
- `GET /mapping-edit-proposals/{proposalId}`: one current review/status record.
- `GET /mapping-edit-proposals/{proposalId}/operations?expectedFingerprint=...`:
  the complete canonical script, digest, count, proposal ID and fingerprint.
- `POST /mapping-edit-proposals/{proposalId}/approve`: exact approval.
- `POST /mapping-edit-proposals/{proposalId}/recover`: resume-only recovery.

Both POSTs require only `{"expectedFingerprint":"<64 lowercase hex characters>"}`.
Unknown/duplicate fields are rejected and the request body is limited to 256
bytes. Creator and organization come from the authenticated request boundary.
Cookie requests use the existing Origin/antiforgery rules. All responses are
`no-store`. A completed originating turn is allowed. Fingerprint mismatches
return 409 before any preparation or ingestion.

Review summaries contain at most 64 changes and retain their truncation flag.
They are not a full review. The separately authorized operations endpoint returns
the complete stored canonical script (at most 1 MiB UTF-8 before JSON string
escaping). It never returns native checkpoint bytes or the private storage codec.
Approval always uses the stored content and stable request key, not client bytes.

Reads never approve or reserve. Recovery calls only `ResumeAsync` for an existing
reservation. An approved proposal without a reservation, or a reservation needing
source, requires explicit repeat approval of that same fingerprint. General
`/mapping-writes/recover` is also resume-only; it no longer resupplies journal
bytes. Lost acknowledgements are resolved by authorized reads, not a new model
turn. A committed save remains saved after a later head change.

## Persistence integration requirements

The runtime uses the persistence owner's agreed addition:

```csharp
Task<IReadOnlyList<ChatMappingEditState>> ListAsync(
    ActorContext actor, ChatConversationKey conversation, Guid turnId,
    CancellationToken ct);
```

`ChatProposalHostConfiguration` registers `PostgresChatEditProposalStore` and
`ChatMappingEditApprovalService` in the integrated host. It requires separate
reader, writer and approver credentials. Compose `IChatMappingEditProposalStore`,
`IProjectReader`, and `IRevisionIngestionService` before enabling proposals.
Missing ports disable the
default tool and make proposal HTTP operations unavailable; there is no direct
save fallback. `CatalogMappingEditOptions.Enabled` must also allow approval and
recovery writes.

The completed persistence slice supplies
`PostgresChatEditProposalStore(reader, writer, approver)`, with host-owned
`PostgresChatDatabase` pools of profiles `Reader`, `Writer`, and `Approver`.
The approver must use its separately credentialed `weft_chat_approver` login;
never reuse the model writer credential or route approval through the writer
pool. The host must own/dispose that third pool and register the routed store as
`IChatMappingEditProposalStore`. This branch intentionally cannot instantiate
that class before the persistence slice is integrated and provisioned.

Integrate the persistence owner's verified-generation admission together with
its store. `CreateAsync` and `RestoreFromTrustedStorage` are not proof of worker
authenticity. Only freshly verified `ProposeAsync` output may admit new rows;
restoration verifies independently retained integrity facts and cannot mint a
generation capability. The implemented capability is
`MappingEditProposal.VerifiedGeneration` (`VerifiedMappingEditGeneration`,
internal constructor/setter), minted by the persistence slice's strengthened
`MappingDirectEditService.ProposeAsync`. The runtime passes that original content
instance directly into `ChatMappingEditProposal`; it never reconstructs it from
the storage codec or DTOs. `PostgresChatStore.CreateAsync` checks its actor,
confirmed provenance, scope and native digest before opening a connection.
The store must independently enforce creator, retention,
project authority, immutable identity, exact fingerprint/head, atomic approval
plus journal preparation, and prior-commit replay. These are database integration
requirements, not guarantees established by the recording runtime test doubles.

The future Angular UI must bind approval to the displayed proposal fingerprint,
fetch exact operations for full review, refresh durable listings after reconnect,
and use new proposal identity/approval after an out-of-date result. Existing
`mapping.write` event codecs remain readable; new proposals are discovered through
the dedicated read API rather than mislabelled as legacy write events.

Overlapping tracked files to reconcile when combining the two slices:
`ChatMappingEditProposal.cs` (identical `ListAsync` addition),
`ChatMappingEditApprovalTests.cs` (listing fake/method additions), and
`ChatMappingWriteTests.cs` (generation tests versus runtime/legacy-tool tests).
Retain the persistence slice's `MappingEditProposal.cs` and
`MappingDirectEditService.cs` changes; this runtime slice does not duplicate them.
Actual PostgreSQL qualification, migration ordering with 0041/0042, and Angular
review remain separate integration gates.
