# Upload an immutable revision

The mapping page offers **Upload revision** when the current catalog feature and
that mapping's upload action permit it. The first supported backend is Ferrule.
The dedicated `revisions/new` page shows the reviewed head, accepts one native
file and an editable saved filename, and leaves the mapping's name and description
unchanged. It sends exact captured bytes without parsing or rewriting native JSON.
A successful append creates a new immutable revision; older revisions and saved
explanations remain available under their ordinary read permissions.

## Review and concurrency

Before its first deliberate send, the page refreshes identity and mapping access
and verifies that the current head still matches the reviewed revision. A changed
head requires explicit review before any new upload intent or key is allocated.
The server makes the final head and authority checks. If a competing append wins,
a terminal head conflict creates no revision. Starting another append requires
explicit current-head review and a new file selection; the original request is
never silently rebased.

## Acknowledgement and recovery

The first send freezes the full scope, owner, reviewed head, filename, exact bytes
and one idempotency key in private tab memory. An unverified response to a
dispatched append or resume, or **Stop waiting** during that wait, can leave its
outcome unknown. Stopping a read does not erase a verified save. Deliberate
resend retains that original
request, even when the current head has advanced. Current authority must still
permit the action. Recovery never searches the catalog for a possible match.

An admitted known operation supports an explicit status read; a known nonterminal
operation can also support source-free resume. The UI offers neither terminal
resume nor automatic mutation retries. A verified saved acknowledgement identifies
the historical revision. Current access to that revision is checked separately;
retrying that access performs reads only. Read failure cannot erase a verified
save, and a later refusal cannot prove an unknown request failed to commit.

## Identity and tab lifetime

An unsent filename, selected file and reviewed head survive a failed, timed-out
or negative session check in page-scoped memory. The same owner (mode, principal,
organization and Session identity context) must pass fresh mapping admission
before the work is restored. An unfinished file capture restarts from its
retained in-memory `File` only after admission. Old capture callbacks stay fenced.
No append is sent automatically, and a changed mapping head still requires
explicit review before a new append intent can be allocated.

Another confirmed owner or mapping, explicit discard, or leaving the page clears
the unsent work. Sent-request recovery remains separate and unchanged.

Identity or scope changes hide private recovery material and invalidate late
callbacks. Returning to the original owner requires fresh complete mapping
admission before private details or deliberate recovery become available. A
same-owner known save may retain a generic acknowledgement while details remain
unavailable. Current project permissions remain authoritative.

One unfinished append occupies one private tab-memory slot. Files, request keys,
tokens and recovery data are excluded from browser storage and navigation URLs.
The page prompts before leaving at-risk work; reload, full-page navigation or
closing the tab loses that memory. Abandonment or stopping a wait does not undo an
already dispatched request. This page does not edit mapping rules, execute a
preview or deploy the mapping.

## Verification boundary

Controlled frontend cases and a separate packaged API/PostgreSQL/native-worker
scenario cover different boundaries. Their source declarations are not execution
evidence. Record the exact integrated source, discovered case census, packaging
and actual outcomes when qualification runs. The fixture's separately labelled
terminal source-free API resume is backend evidence, not a completed UI flow.

The packaged scenario mediates each already-allowlisted catalog read through one
actual API request and delivers that complete bounded JSON entity to the original
browser Request. Exact response identity/status, current capability/token, saved
revision identity and immutable persistence assertions remain required. This is
mediated API and frontend evidence; native browser cookie ordering, cancellation
propagation and streaming timing retain separate verification boundaries.
