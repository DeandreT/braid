# Exact chat edit approval runtime

Read this with [PostgreSQL and host composition](chat-edit-approval-composition.md)
and [browser proposal review](chat-proposal-review-ui.md).

## Model boundary

The edit manifest offers `propose_mapping_edit`. It accepts a mapping ID, exact
base revision ID and typed `ferrule.edit.v1` script. It calls
`MappingDirectEditService.ProposeAsync`, then retains the result through
`ChatMappingEditApprovalService.CreateAsync`. It does not prepare ingestion or
call `ApplyAsync`. The durable turn listing recovers proposals after lost tool
results or SSE delivery.

## HTTP contract

All paths below are beneath
`/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}`:

- `GET /mapping-edit-proposals`: at most eight authorized proposal summaries.
- `GET /mapping-edit-proposals/{proposalId}`: one current review/status record.
- `GET /mapping-edit-proposals/{proposalId}/operations?expectedFingerprint=...`:
  complete canonical script, digest, count, proposal ID and fingerprint.
- `POST /mapping-edit-proposals/{proposalId}/approve`: exact approval.
- `POST /mapping-edit-proposals/{proposalId}/recover`: resume-only recovery.

Both POSTs require only `{"expectedFingerprint":"<64 lowercase hex characters>"}`.
Unknown/duplicate fields are rejected; the request body is limited to 256 bytes.
Creator and organization come from the authenticated request boundary. Cookie
requests use Origin/antiforgery checks. Responses are `no-store`. A completed
originating turn is allowed. Fingerprint mismatches return 409 before preparation
or ingestion.

Summaries contain at most 64 changes and a truncation flag. The operations
endpoint returns the complete canonical script, at most 1 MiB UTF-8 before JSON
escaping, without native checkpoint bytes or the storage codec. Approval uses
stored content and the stable request key, never client-supplied native bytes.

Reads never approve or reserve. Recovery calls `ResumeAsync` for an existing
reservation. An approved proposal without a reservation, or one needing source,
requires explicit repeat approval of the same fingerprint. Lost acknowledgements
are resolved by authorized reads. A committed save remains saved after a head change.

## Persistence composition

`ChatProposalHostConfiguration` registers `PostgresChatEditProposalStore` and
`ChatMappingEditApprovalService`. It requires separately credentialed Reader,
Writer and Approver pools, plus `IProjectReader` and `IRevisionIngestionService`.
The host owns and disposes all pools. Approval uses `braid_chat_approver`, never
the model writer credential. Missing ports disable the tool and make its HTTP
operations unavailable. `CatalogMappingEditOptions.Enabled` must allow approval
and recovery writes.

Only freshly verified `ProposeAsync` output admits new rows. Storage restoration
checks independently retained integrity facts and cannot mint
`VerifiedMappingEditGeneration`. Pass the original content instance directly into
`ChatMappingEditProposal`; do not reconstruct it from storage envelopes or DTOs.
The store checks actor, confirmed provenance, scope and native digest before
opening a connection, and enforces creator, retention, project authority,
immutable identity, exact fingerprint/head, atomic preparation and prior-commit replay.

The browser binds approval to the displayed fingerprint, fetches exact operations
for review, refreshes durable listings after reconnect, and requests a new proposal
and approval when the base is out of date.
