# AI usage and quotas

Organization administrators can read organization-wide AI usage. Project
administrators see usage only for projects they currently administer. The `/usage`
view and `GET /api/v1/organizations/{organizationId}/usage`
support time windows and grouping by organization, project, principal or model
profile. Each page rechecks current access. Workspace turns belong to the project
in their original turn focus, or to the organization when they have no focus.

## Accounting

Usage is attributed to the turn's admission time. It survives private-chat
erasure and contains counters and fixed failure categories, never prompts, tool
arguments, payloads or provider error text. Historical coverage is explicit:
retained pre-upgrade turns are partial history because previously erased turns
cannot be reconstructed.

Provider input, output and total-token counts are independent. Missing counts
remain unknown. Reports include each known subtotal and the number of rounds
whose corresponding count is unknown. Intermediate stream observations are not
additional rounds. Exact-round terminal metadata is counted once even if the
separate round-completion write was uncertain; that observation does not settle
execution or free reserved quota capacity.

Turn failures, cancellation and interruption have separate categories from
provider-round failures. These categories can overlap, so adding their counts
does not give a count of distinct failed turns. Public counters and quota values
are canonical decimal strings, preserving all signed 64-bit integer values in
browser clients.

## Quota policy

Only organization administrators can configure quotas. `/quotas` and the
administration API manage optional organization and principal limits for UTC
calendar hours, days and months. All applicable limits must admit a new turn.
Turn limits count admitted turns, including failed or cancelled turns.

Token limits are hard caps. A trusted provider integration must establish a
maximum for the entire turn, including input, output, tools and later rounds.
The proof binds the exact conversation, turn, original envelope, model profile,
execution budget and deadline. The default runtime authority supplies no proof,
so token-capped profiles are refused until a qualified authority is configured.

Admission reserves the whole verified maximum against organization and principal
budgets atomically. Configuration and admission serialize on the same organization
row. Original-turn replay keeps its reservation and does not request another
proof. Refusal rolls back new request identities and reservations.

Reservation and insertion share a SQL statement and admission timestamp. Expiry
is checked again after waiting for the organization lock. Only completed or
failed turns with acknowledged rounds and known total usage settle their
reservation. Unknown, interrupted and cancelled work retains its reservation,
including after private-chat erasure. Incomplete historical coverage cannot be
treated as unused capacity.

## HTTP

- `GET .../administration/ai-quotas` reads organization policy and its strong ETag.
- `GET .../administration/principals/{principalId}/ai-quotas` reads principal policy
  and its scope-bound strong ETag.
- `PUT` to either path plus `/{period}` replaces both limits for `hour`, `day` or
  `month`. Supply the exact `If-Match` from that scope's latest read and a JSON
  object with both `maximumTurns` and `maximumTokens`. Each value is a positive
  decimal string or `null`. Two nulls remove that period's limits.

A changed policy returns `412 preconditionFailed`; read and review before another
edit. Writes are never automatically resent after an uncertain response. The
browser retains entered values after an unconfirmed write, hides them after a
failed authority refresh and clears them when write authority is refused or the
identity changes.

Chat admission returns `429` for `quotaTurnLimitReached` or
`quotaTokenLimitReached`, and `409` for `quotaTokenBoundUnavailable` or
`quotaCoverageUnavailable`. The question remains available in the browser and
does not start.
