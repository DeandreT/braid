# Private conversation persistence

## Ownership and immutable context

Conversations belong to the stable organization/internal creator principal and
one complete project/mapping/revision scope. Login epochs and administrator roles
do not supply content ownership. Ordinary ports freshly require active principal,
organization admission and exact project membership. The separately credentialed
reader and writer pools are owned/disposed by their caller; stores borrow them.
Transaction-local RLS scope supplements, rather than replaces, server identity
and explicit project authorization.

Each conversation pins retained usable projection and validation attempts by
cache entry, attempt UUID, complete-key fingerprint and derived-object digest.
Creation verifies the exact operation and revision, not a mutable selected-report
pointer. Restrictive references retain reports/revisions. Chat erasure never
deletes report, native, payload or run evidence.

`ChatOriginalEnvelope` requires an explicit request UUID, bounded user text,
ordered exact context selectors, trusted instruction/tool-manifest and retrieval
policy pins, and provider budgets. `ChatTurnIntent` also pins
the turn UUID, versioned nonsecret model profile identity, persistence budgets and
absolute deadline. There is no bare-text admission constructor or production
retention duration. Canonical turn identity includes the conversation's complete
scope, both exact report pins and retention deadline as well as every turn input.
Changed original input conflicts; no new turn replaces an original request.

The v1 canonical contracts and pure `Weft.Ai` bridge are specified in
[Chat persistence contract v1](chat-persistence-contract-v1.md). Selectors record
untrusted user intent, not tool or citation authority. V1 has no payload,
attachment, action, arbitrary URL or cross-project envelope.

## Durable identity and erasure

`chat_identities` irreversibly reserves conversation, turn, original-request and
provider-round UUIDs within the stable organization/creator namespace. Its global
kind/UUID arbiter survives erasure and serializes conflicting identity admission
across conversations. Unique waits are followed by a fresh statement. A retired
identity is refused even when its previous content is gone or the caller supplies
a different request, conversation, fence or profile.

Erasure requires the trusted content-retention deadline to have passed, no active
turn, and an explicit `ChatErasurePolicy` with policy ID/version and identity
retirement-not-before instant. In one transaction, identity reservations become
non-content tombstones and all chat content is removed. Deferred checks refuse a
committed tombstone transition while the conversation content still exists.
Tombstones retain only organization/creator, typed UUIDs and their conversation/
turn association, erasure time and policy facts. They contain no text, prompt or
response digest, request envelope, report pin, model configuration or usage.

The policy instant is a minimum, not an automatic identity expiry. Passing it
never permits a stale billable request to become new work. No DELETE/TRUNCATE or
identity-recycling port is provided. Future inbound run/evidence references must
be restrictive; chat-to-report/run deletion cascades are absent.

## Rounds, accounting and reconnect

One turn per conversation is active. Writes serialize on its conversation row,
then use fresh statement authority and clocks after waits. Schema/ACL admission
and held schema locks are composed separately. Admission is not a parent-authority
lock through COMMIT. No parent UPDATE privilege is used.

Every provider round is a separate durable one-shot reservation with exact UUID,
ordinal, immutable request, server instance and fence. It becomes `Uncertain`
immediately, even if transport never starts. Only `Applied` acknowledged to the
original awaiting reservation call permits that caller's one dispatch. An
observation/replay, lost acknowledgement, restart, elapsed time or `NotAttempted`
provider failure never grants another send. The pure bridge consumes a matching
acknowledgement once and returns the same frozen `AiRoundRequest` object/UUID.

Only a fully consumed, valid `ToolCalls` round can admit another new round, on
the same process-instance identity and live provider lease. A different process
cannot take over continuation even when the previous round completed ToolCalls.
Other finishes close continuation. Completion, cancellation and interruption are
version/fence conditional; late writers cannot reopen terminal turns. Closing a
turn leaves unresolved rounds uncertain. Recovery does not recreate a provider
lease or resend a request.

Events are immutable, contiguous and bounded from start through one terminal.
Usage observations retain per-round observation/terminal/open-rejection provenance and independent nullable input,
output and total counts: all-null is unknown, known zero stays zero, and partial
usage stays partial. Repeated observations are snapshots, not additive charges.
Provider failures and finish reasons are closed enums, not native error text.
Typed round completion atomically records its accounting event and round result.
The terminal event preserves final accounting; its separate fixed 2 KiB reserve
cannot be consumed by ordinary output. Unknown usage is never priced as zero.

Citation admission requires `ChatValidatedCitation`, constructed from an exact
`VerifiedProjectionReport`/`VerifiedValidationReport` and an existing resource,
whole report or emitted diagnostic ordinal. Metadata retains the exact report pin and revision.
There is no invented diagnostic UUID. Validation ordinals refer to that report's
emitted `Diagnostics` array; projection operation diagnostics are a separate kind
from projected nodes, document diagnostics and coverage gaps. A citation proves
an existing referenced resource, not the interpretation of an answer.

## Trusted lifecycle without administrator content access

`IChatLifecycleStore` uses a third, separately credentialed lifecycle pool. Its
three exact-target SQL routines can observe only phase/version, conditionally
close an active turn as cancelled/interrupted, and erase expired closed content.
They operate after owner revocation, but return no request, conversation history,
round request, event, profile, report or accounting payload. The login receives no
content-table SELECT/DML, membership, ownership or administrator-content capability.
Its only column read is version/checksum in the non-content migration journal;
the bounded `chat_schema_lock` capability supplies relation protection without
granting it a content or tombstone listing port.

Closure may accept only bounded typed usage/failure terminal metadata for the
exact last round, allowing a live controller to preserve final accounting after
owner revocation. It cannot accept prompt/response/citation text, complete a
provider round, authorize a tool, reopen a turn or grant dispatch permission.
Ordinary owner operations retain fresh ACL requirements. Expired owner closure
returns no private facts; read ports deny content at expiry.

Live cancellation must be connected by the future active-turn controller to the
same cancellation source and live `AiTurnLease`. A database terminal write alone
does not close a remote socket or revoke a dispatch permission already handed to
another process. There is no scheduler, background scan, Wolverine job, HTTP/UI
activation or model call in this persistence library/bridge.

## Bounds and diagnostics

Text is strict UTF-8 without NUL; instants are finite UTC microseconds. Original
text is at most 64 KiB; the escaped original envelope is at most 512 KiB and full
canonical turn envelope at most 1 MiB. A canonical provider-round pin allows
2 MiB of escaped application representation, not increased provider-wire limits.
There are at most four rounds, matching `Weft.Ai`; at most 4,096 events; 8 KiB per
ordinary text/metadata event; and a caller-supplied event-byte budget up to 4 MiB
plus the terminal's fixed 2 KiB reserve. Pages contain at most 100 events.
Provider time/wire/text/tool/output budgets remain separately pinned and bounded.

Private-bearing facts/results have categorical `ToString`; SQL and codec failures
are fixed categorical exceptions without private inner exceptions. Callers must
not destructure private properties into logs. No native/provider error body or
raw content is an audit/diagnostic payload.
