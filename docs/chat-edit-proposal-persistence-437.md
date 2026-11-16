# Exact chat proposal persistence

`PostgresChatEditProposalStore` implements `IChatMappingEditProposalStore` through
three host-owned `PostgresChatDatabase` pools:

- Reader: authorized reads and bounded per-turn discovery.
- Writer: verified proposal generation admission.
- Approver (`braid_chat_approver`): explicit approval and atomic journal preparation.

See [host composition](chat-edit-approval-composition.md) for configuration and
[approval runtime](chat-proposal-runtime.md) for HTTP operations.

## Discovery

```csharp
Task<IReadOnlyList<ChatMappingEditState>> ListAsync(
    ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct);
```

The query returns at most eight authorized retained proposals in creation order,
with proposal ID as a stable tie-breaker. Discovery works after lost SSE delivery
and after a turn ends. These private application objects contain native bytes
and complete operation scripts; API responses use separate DTOs.

## Authenticity and immutable content

`MappingDirectEditService.ProposeAsync` mints `VerifiedMappingEditGeneration`
only after trusted backend replay confirms matching scope, digest and provenance.
The capability binds the actor, backend provenance and generation ID. It has no
public constructor; `MappingEditProposal.VerifiedGeneration` has no public setter.
Storage restoration cannot recreate it. `CreateAsync` refuses missing or
mismatched admission before opening a connection. Pass the original proposal
content from `ProposeAsync` into `ChatMappingEditProposal`.

The database retains generation/provenance and immutable identity, digest and
count facts independently of the private encoding. Every read checks canonical
encoding against those facts. SQL checks native bytes, encoded digest,
journal/base bindings, provenance scope/digest, both call-derived key domains,
the retained call event, authority and retention. An identical retry uses its
original admission record; regeneration cannot overwrite it.

Keep the writer credential private to the trusted generation path. JSON assertions,
hashes and `Confirmed=true` do not authenticate a worker. The raw writer credential
can assert generation facts, but has no approval-function grant. Authenticated
backend transport and host isolation protect that boundary.

## Approval, receipts and retention

Approval serializes with conversation erasure and other approvals. It checks
creator, organization, turn, fingerprint, retention and current read authority,
then reads any committed receipt before checking a later head or publish grant.
Fresh approval checks exact base revision/sequence and writes approval plus journal
intent/content in one transaction. Generation requires a matching live
`propose_mapping_edit` call; approval can follow a terminal turn.

Finalization does not acquire the conversation/proposal locks. Before returning
a publish-permission or exact-head refusal, approval rereads through
`chat_edit_read` using a new READ COMMITTED statement snapshot. A commit visible
then returns `Saved`; withdrawn read authority or expired retention returns SQL
NULL. This avoids reverse lock ordering with finalization. Retention is rechecked
after waits, including while the conversation lock is held.

Reads and listing never prepare or reserve ingestion. `Approved` means prepared
but unreserved. `Pending` requires an existing ingestion reservation. Explicit
approval can reserve; recovery uses `ResumeAsync` on an existing reservation.

Private proposal/journal content cascades with conversation erasure. No-content
provenance, approval and commit audits remain immutable. Conversation retention
sets proposal expiry. Journal summaries are bounded; complete scripts and proposal
summaries remain in private proposal content. Known proposal keys cannot commit
without matching approval and bindings.

## Database checks

`PostgresChatEditProposal` tests cover discovery after lost delivery, terminal-turn
approval, exact replay, receipt-first recovery, immutable facts, expiry, erasure
and unapproved-key refusal.

The concurrency regression places an advisory gate after the fixture approval
function's initial `Pending` read, finalizes ingestion on another connection, then
releases approval after head movement, publish demotion or read revocation. Head
movement and demotion must return the committed receipt; read revocation must
return NULL. Instrument only the disposable fixture and restore its original
function in cleanup; production exact-schema admission rejects instrumentation.

Focused test filter:

```text
FullyQualifiedName~ChatMappingEdit|FullyQualifiedName~ChatMappingWriteTests|FullyQualifiedName~PostgresChatEditProposal
```
