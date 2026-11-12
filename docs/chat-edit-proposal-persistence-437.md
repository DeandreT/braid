# Exact chat proposal persistence (#437)

## Implementation boundary

Migration `0043_chat_edit_proposals.sql` adds immutable proposal identity/generation facts, conversation-retained private content, and an immutable approval audit. It preserves migrations 0001–0040. Slots 0041 (CSV) and 0042 (XML) belong to parallel branches and are absent in this checkout. No placeholder migrations or relaxed inventory admission were added: the consecutive migration runner remains fail-closed until those real migrations are integrated.

`PostgresChatEditProposalStore` implements `IChatMappingEditProposalStore`, routing through three host-owned `PostgresChatDatabase` instances:

- Reader profile: read and bounded per-turn discovery.
- Writer profile: verified proposal generation admission only.
- New Approver profile (`weft_chat_approver`): explicit approval and atomic journal preparation.

The exact discovery signature is:

```csharp
Task<IReadOnlyList<ChatMappingEditState>> ListAsync(
    ActorContext actor, ChatConversationKey conversation, Guid turnId, CancellationToken ct);
```

It returns at most eight authorized retained proposals in creation order, with proposal ID as a stable tie-breaker. Delivery/SSE success and a live turn are not prerequisites for discovery. The port is private application data; API DTOs must not serialize it directly because it contains native bytes and the complete operation script.

## Authenticity and immutable content

`MappingDirectEditService.ProposeAsync` mints a `VerifiedMappingEditGeneration` capability only after the trusted backend's verified replay has matching scope/digest and confirmed provenance. The capability has no public constructor; `MappingEditProposal.VerifiedGeneration` has no public setter. It binds the generating actor and retains the backend provenance and a generation ID. Storage restoration deliberately never recreates this capability. PostgreSQL `CreateAsync` refuses missing/mismatched admission before opening a connection. Pass the original `ProposeAsync` content into `ChatMappingEditProposal`.

The database retains generation ID/provenance and complete immutable identity/digest/count facts independently of the private encoding. The application checks exact canonical encoding against those independent facts on every read. SQL verifies the separate native bytes and encoded-byte digest, journal/base bindings, provenance scope/digest assertion, both call-derived key domains, retained call event, authority, and retention. Retrying identical content uses the original generation admission record; regeneration does not overwrite it.

**Trust invariant:** the verified replay implementation and application generation admission path are trusted. The chat writer database credential must remain private to that application path. Neither a JSON provenance assertion, `Confirmed=true`, storage encoding, nor its hashes cryptographically authenticates a worker. A party holding the raw writer credential can assert generation facts; it still has no approval function grant. This slice does not invent signed worker evidence or accept client storage envelopes. Host/backend isolation and authenticated endpoint routing remain required.

## Approval, receipts, and retention

Approval serializes with conversation erasure and other approvals, checks creator, organization, turn, fingerprint, retention and fresh authority, then reads a prior committed receipt before checking a later head or current publish permission. Read permission is still required. Fresh approval checks exact base revision/sequence and writes approval plus the existing journal intent/content in one transaction. A terminal turn can be approved without a live round fence; generation still requires the matching live `propose_mapping_edit` call.

Concurrent finalization does not acquire those conversation/proposal locks. Before returning either a publish-permission or exact-head refusal, approval therefore rereads through `chat_edit_read` using a new READ COMMITTED statement snapshot. A commit visible at that refreshed read returns `Saved`; withdrawn read authority or expired retention returns SQL NULL, never the previously cached private response. This is an observation boundary, not a promise to see a commit occurring after the refreshed read. No ingestion or mapping row locks were added, so approval introduces no reverse lock order with finalization. Conversation erasure still serializes behind the existing conversation lock; wall-clock retention is rechecked even while that lock is held.

The concurrency regression instruments only the disposable fixture's approval function with an advisory gate immediately after its initial `Pending` read. It verifies the actual blocking edge, finalizes ingestion on another connection while approval retains its locks, and then releases approval after either head movement alone, publish demotion to viewer, or read-membership revocation. The first two must return the committed receipt; revocation must return NULL. The original function definition is restored in cleanup. This test intentionally invokes the real restricted SQL role directly because production exact-schema admission correctly rejects an instrumented function; it does not weaken production admission or modify finalization/audit behavior. Actual PostgreSQL execution of this regression remains pending.

Reads and listing never approve, prepare, reserve, or supply source to ingestion. `Approved` means atomically prepared but unreserved; only a repeated explicit approval action may first-reserve it. `Pending` requires an existing ingestion reservation. The runtime owner is changing generic recovery to `ResumeAsync`, rather than `AppendAsync` source resupply.

Proposal/private journal content cascades with conversation erasure. No-content proposal provenance, approval and commit audits remain immutable. Conversation retention is the proposal expiry boundary in the current application contract. Journal summaries retain the existing bounded representation; the complete canonical operation script and full bounded proposal summaries remain in private proposal content.

The commit audit identifies organization/actor/request key before validating bindings. Known proposal keys cannot commit without approval, and known legacy intent keys cannot fall through as ordinary ingestion. The old prepare function loses its writer grant. Old committed receipts remain readable without inventing retrospective approval. Pending legacy writes therefore require a fresh proposal after cutover.

## Integration and held qualification gates

No API/UI/model executor or production dependency-injection cutover is included. The runtime branch owns those changes and the approval service discovery overloads. Integrate its identical `ListAsync` interface addition once.

Before deployment:

1. Integrate actual migrations 0041 and 0042; run complete migration inventory/checksum checks. Do not renumber or relax the runner.
2. Provision a separately credentialed `weft_chat_approver` LOGIN role with NOINHERIT, NOSUPERUSER, NOBYPASSRLS, NOCREATEDB, NOCREATEROLE and NOREPLICATION, no role memberships/config overrides, and database CONNECT only. Migration 0043 grants schema USAGE, migration-history column reads and the four exact read/list/approve/schema-lock functions. Production local/Azure provisioning and secret configuration are integration work still required; the disposable PostgreSQL test fixture provisions its own isolated role.
3. Quiesce old chat write execution and recovery before applying the migration's legacy write gate. Coordinate the propose-only tool registry and authenticated exact approval routes with store registration. Give the model-facing tool only propose behavior, never approval behavior or database credentials.
4. Qualify on actual PostgreSQL: fresh/upgrade installation, exact schema deparse/ACL admission, list authority/retention and lost delivery, terminal-turn approval, replay after head movement/demotion, commit atomicity, concurrent approvals, revocation/head races, crash windows, legacy pending denial, audit/key-binding mismatch refusal, erasure and committed receipt conservation. Update legacy tests that intentionally expect the now-retired writer prepare grant; retain their pre-cutover upgrade conservation coverage rather than silently weakening assertions.

The new database tests cover discovery after lost delivery, terminal-turn approval/idempotence/receipt-first replay, direct grants and immutability, expiry/erasure, and unapproved-key commit refusal. They are authored but **not database-runtime-qualified here**. No PostgreSQL or Docker is available in this lane. Offline contract/security tests and generation/approval tests are separate evidence, not substitutes for those gates.

## Local verification

Use the pinned `%LOCALAPPDATA%/Weft/Toolchains/dotnet-10.0.112/dotnet.exe`, locked restore, and `%LOCALAPPDATA%/Weft/WindowsLane/cache/nuget`. The workstation's default dotnet is a different SDK and is not used for qualification.

Focused filter: `FullyQualifiedName~ChatMappingEdit|FullyQualifiedName~ChatMappingWriteTests|FullyQualifiedName~PostgresChatEditProposal`.

Initial verification: locked restore succeeded; the focused suite passed **114**, with **5 PostgreSQL tests skipped**. That run used `--no-build --no-restore -m:1` after compilation succeeded but the combined parallel build/test invocation timed out. The retry completed successfully. Migrations 0001–0040 were compared byte-for-byte with `HEAD` and are unchanged. After the concurrent-receipt review fix, the 0043 source checksum is `358f65373c46306b3a547d20d57ba1634e3dcefd7942deb25cd1fba60ebd0109`; the new database regression remains an additional unqualified gate.

Review-fix verification: pinned 10.0.112 compilation and focused tests with `--no-restore -m:1` passed **114**, with **6 PostgreSQL tests skipped**, including the controlled-interleaving regression. No database-runtime result is claimed.
