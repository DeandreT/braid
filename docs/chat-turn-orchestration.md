# Private chat turn orchestration

`ChatTurnOrchestrator` composes the creator-private chat ledger with the concrete
`Weft.Ai.AiRoundClient`. It does not own another provider adapter, persistence
implementation, scheduler or request protocol. Host/API composition supplies an
authenticated actor and trusted turn/profile/grounding policy facts.

## Admission and dispatch

Conversations already exist and pin immutable projection and validation reports.
The configured profile must match `ChatAiBridge.PinProfile`, permit mapping
metadata and support the requested capabilities. The versioned instruction and
ordered tool manifest must reproduce the turn's grounding-policy pin. A trusted
`IChatProfilePolicy` freshly enforces current organization and destination policy.
No browser endpoint, credentials, provider specification or role assertion is an
input to the orchestrator.

Only the original `StartAsync` Applied acknowledgement with exact fresh facts
starts orchestration. Replayed admission returns the observed duplicate without
provider activity. Selected prior turns are read through the same private ledger,
in explicit oldest-first order, and must be completed, nonoverlapping exchanges
from this conversation. History uses the final Stop round's text, not intermediate
tool-round prose. No history is reconstructed from browser-supplied messages.

Each round is built using the `Weft.Ai` factories and `ChatAiBridge.PrepareRound`.
The ledger persists its exact request, round ID, fence and server instance before
outbound work. `ChatPreparedRound.TakeAfterAcknowledgement` consumes only the
original exact Applied reservation. Additional version/counter/conservation
checks and fresh conversation/turn/round observations precede dispatch. Replayed
reservations, missing acknowledgements and uncertain publication never grant
permission to call the provider. There is no automatic retry or ownership takeover.

Private ownership/project admission and profile/data policy are rechecked before
each provider dispatch, each tool schema-validation boundary, immediately before
tool execution after its start event acknowledges, and after its result returns.
The tool port independently enforces authority inside execution. Sequential
observations are not a distributed revocation fence: a later revocation cannot
undo a request already sent. Stores and policy must not substitute cached grants.

## Ordered provider and tool processing

Text and usage events are published serially using the last acknowledged version
and exact round fence. Provider tool intents are staged, not executed. The full
stream must enumerate to exactly one valid terminal, and the original stream
disposal must succeed, before a ToolCalls batch can be admitted. A failed or
partial terminal admits no tools. Every exact call ID/name/schema in the entire
batch must validate before any tool-start event or execution. Distinct call IDs
remain distinct even for repeated names and arguments.

`IChatReadOnlyToolPort` is an explicitly trusted, scoped read-only port, not an
authority delegated by model text. Its validation is side-effect free; execution
rechecks current authority/schema and honors the remaining result-byte and
retrieved-node budgets while retrieving data. Results are complete `AiJsonObject`
values with honest node accounting. An over-budget result fails closed rather
than being truncated. The same immutable call object supplies each corresponding
`AiToolMessage`, in original order.

The ledger allows event appends only while a round is Uncertain. Each ordered
tool therefore publishes ToolStarted, executes, and publishes ToolCompleted
before typed RoundCompleted(ToolCalls). Only then may the next exact request be
reserved on the same provider lease. Tool activity contains call/name/ordinal
and completion byte/node counts, not raw arguments or results. Exact provider
history, including complete results, is private persisted round request content.

Native Stop publishes typed RoundCompleted then Completed. OutputLimit, refusal
and filtering finish the turn as Failed, retaining the native reason. Provider
failures retain categorical dispatch/usage metadata. Unknown token accounting
stays null and observations remain per-round; no inferred zero or aggregate is
introduced. Non-provider failures do not fabricate provider usage or dispatch.

TextDelta bodies are canonical compact JSON `{"v":1,"text":"..."}`. Consumers
decode them with `ChatOrchestrationEventCodec.ReadText` before delivery. This
preserves exact whitespace-only and escaped NUL text without weakening the
ledger's nonblank private-text admission. JSON pieces never split a Unicode
scalar and stay within the ledger's 8192-byte event bound. History accepts this
canonical representation only; another producer needs an explicit compatible
format contract, not a guessed fallback.

## Bounds and uncertainty

Trusted application limits cap prior turns, total scanned history events, loaded
history text bytes and logical message-context bytes. Provider turn limits retain
their cumulative serialized request/wire/text/call bounds, maximum rounds and
deadline. Application output-byte accounting is independently cumulative. Each
request retains its pinned output-token limit; optional usage is not treated as a
reliable token-budget meter. Tool iterations, calls, complete result bytes and
retrieved nodes are cumulative across the entire turn. The last allowed provider
round cannot execute tools that would require another provider round. Event
counts/bytes and terminal reserve remain governed by `ChatTurnPolicy` and the
existing ledger.

Any ambiguous provider or persistence operation stops forward execution. A single
terminal close may be attempted at the last acknowledged version; it is not
retried or reconstructed after an uncertain acknowledgement. If authority has
been revoked, the version changed, or a publication committed without an
acknowledgement, the existing separate lifecycle recovery lane must mark the
abandoned turn interrupted. No owner-store administrator bypass is introduced.

## Lifetime and diagnostics

`Begin` returns one hot `ChatTurnExecution`. Its `Completion` includes all original
store/policy/tool/open/enumeration work and application-visible required cleanup. `CancelAsync` starts
or joins one private cancellation operation. The caller token and trusted timer
only request this operation; callbacks never execute under application gates.
Returning from CancelAsync is not proof the original work settled; callers still
join Completion and require ReleaseSafe before releasing borrowed scopes.
`WaitAsync` bounds an observer's wait only. A timed-out observer must retain the
execution and its borrowed dependency scope; it cannot release or recreate the
provider client, profile or tool ports while original work remains pending.

Stream and turn disposal use the real retained `Weft.Ai` cleanup tasks. Cancellation
callbacks, timer and caller registration also join before `ReleaseSafe` becomes
true. Failed cleanup keeps `ReleaseSafe` false permanently and retains original
tasks/owners for quarantine. Cleanup is never treated as successful because a
socket closed or an observation timed out. Shared provider capacity is still
controlled exclusively by `Weft.Ai`. The host owns the shared client and stores, must
not dispose them per turn, and must retain a failed execution's dependency scope.
Trusted ports must not detach work behind returned tasks. Noncooperative work can
hold Completion indefinitely; this is deliberate ownership, not bounded latency.

The synchronous `AiRoundClient.OpenTurn` API can return Cancelled after internally
admitting a lease, or throw during initialization, without exposing that lease.
The shared client retains that hidden cleanup and joins it during its actual
shutdown. Such unobservable admission paths conservatively return CleanupUnproven
with ReleaseSafe permanently false; they do not claim a per-turn cleanup join or
dispose the borrowed global client. Its owner must retain the shared scope through
real shutdown. Known non-Cancelled pre-admission refusals have no hidden lease.

Public results contain closed outcomes and acknowledged phases only. All newly
introduced fact/result types have categorical `ToString` implementations. No
prompts, results, raw provider bodies, exception messages, endpoint or credential
values enter routine diagnostics through this module.

[Mapping explanation tools](mapping-ai-tools.md) supply the registered tool
manifest, read port and citation check. Registration and final validated
citation-snapshot delivery are a separate composition boundary. Completing
orchestration does not validate an answer's citations. HTTP/SSE, UI, payloads,
edits, preview/execution, deployment and live model activation are outside this
module.
