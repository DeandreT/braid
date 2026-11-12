# Exact proposal review UI (#437)

This frontend slice targets the proposal API implemented in
`weft-approval-runtime`. It does not replace the generated CSV/intake contracts
or add backend endpoints.

## Review and approval

The shared revision/workspace chat component displays workspace proposals from
the durable per-turn list, independently of `mapping.write` events. It reloads
after tool completion, turn completion/interruption, conversation reopening,
and explicit refresh. Cards and action state are keyed by turn and proposal ID.

Summaries always explain that they are not the complete edit, including their
truncation marker. **Review complete operations** fetches the separately
authorized canonical script. The UI checks proposal ID, fingerprint, operation
count, script size/shape, and the SHA-256 of its exact UTF-8 bytes before retaining
it for review. Script content is rendered as text in a bounded, keyboard-focusable
scroll region. Approval requires acknowledgement of those complete operations.

The approval action submits only the displayed `expectedFingerprint` to that
proposal's route. It cannot grant turn-wide permission. Per-proposal busy guards
block duplicate clicks before asynchronous identity admission. Both operations
detail and action results are discarded after owner, conversation, generation,
or proposal-identity changes. Turn read versions prevent older list requests from
overwriting an action result.

An ambiguous action response never becomes a saved version; approval is blocked
until durable status has been refreshed. Pending recovery uses only the exact
proposal's `/recover` action. Explicit repeat approval is separate and requires
review acknowledgement again. An out-of-date proposal prepares an **unsent**
question identifying the old proposal and mapping; sending it requests a new
proposal, which requires new review and approval.

Legacy stream history remains readable. Unidentified pending/not-saved receipts
are read-only: the integrated receipt DTO has only requestId, status and write,
with no proposal ID or legacy provenance. An empty proposal list does not prove
a receipt is legacy. Turn-wide recovery is therefore not offered by this UI.
Exact proposal recovery and either status-refresh control reload both lists;
mutation versions discard receipt reads started before a proposal action.
Saved receipt/history cards are deduplicated against proposal cards only by the
confirmed saved revision ID and organization/project/mapping scope. Pending
receipts cannot be associated safely and remain explicitly unidentified.
Restoring separate legacy recovery requires an API provenance discriminator and
an appropriately scoped recovery route; no frontend association is invented.
The shared help text now describes proposal review rather than model-driven saves.

## Contract integration

`apps/web/src/chat/mapping-proposal-types.ts` is a narrow, runtime-validated local
DTO on this older-base branch. Its proposal and operations fields were checked
against the integrated `f4646073` generated schemas and already match, including
numeric sequences and the closed status union. Adopt generated aliases at
integration while retaining the validators; no generated file was copied here.
The inspected server DTO uses **numeric** `baseSequence` and `revisionSequence`,
unlike legacy `mapping.write` strings. Unsafe JSON integers are rejected rather
than rounded. A later server representation change requires updating these local
guards/tests before replacing them with generated types.

All requests use the existing authenticated chat transport, fresh context,
same-origin credentials and unsafe-request token. The required route family is:

```text
/api/v1/organizations/{organizationId}/chat/conversations/{conversationId}/turns/{turnId}
  /mapping-edit-proposals
  /mapping-edit-proposals/{proposalId}/operations?expectedFingerprint=...
  /mapping-edit-proposals/{proposalId}/approve
  /mapping-edit-proposals/{proposalId}/recover
```

The list is bounded to eight proposals, changes to 64 entries, operations to
2,000, and the complete script to 1 MiB UTF-8. Its HTTP envelope permits bounded
JSON escaping overhead. Missing/unavailable proposal APIs produce a visible read
failure and never a synthetic empty-success response or direct-save fallback.

Deployment still requires the separately reviewed runtime and PostgreSQL
verified-generation/approval store, credentials, migrations, and database
qualification. Frontend tests use synthetic API responses and do not establish
those backend guarantees.

## Local verification

Use Node 22.23.3 and npm 12.1.0 from the local pinned toolchain, `npm ci
--ignore-scripts`, the project checks and production build. Pure tests are in
`mapping-proposal-types.spec.ts`; browser tests are in
`chat-proposal-review.spec.ts` alongside the legacy `chat-mapping-writes.spec.ts`.

The browser lane uses actual headless Chromium through
`WEFT_CHROMIUM_EXECUTABLE`. The installed Chromium 1254 was selected explicitly
because this lockfile's default Playwright headless-shell 1248 is absent. The
browser suite covers exact approval, duplicate clicks, lost acknowledgements,
reload without proposal SSE, pending recovery, explicit reproposal, owner and
conversation changes, literal script rendering, and narrow-screen axe/keyboard
accessibility. It is distinct from TypeScript compilation.

The P2 regression cases populate both proposal and receipt lists and cover exact
proposal recovery plus receipt-side refresh after server completion. They hold
both stale list responses across recovery, verify saved-card deduplication, and
check that no pending recovery control remains after saved confirmation. Generic
turn-wide recovery is intentionally not exercised as a UI action because the
current API cannot establish which receipts are legacy.
