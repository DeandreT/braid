# Chat orchestration adversarial test contract

## Boundary and trusted composition

Application orchestration tests call `ChatTurnOrchestrator.Begin` and observe its
public execution, the real `AiRoundClient`, captured provider requests, and the
public ledger/policy/read-only tool ports. Never replace provider models, leases,
stream events or application results with test lookalikes. Do not mock the
orchestrator or use reflection to obtain private tasks or admission state.

`ChatOrchestrationFixture` is a controlled port fixture, not another persistence
implementation. It uses real Domain/Application facts and `ChatTurnPolicy` for
ledger transitions, scoped ownership, serial expected versions and exact fences.
It intentionally has no database, lifecycle privilege or erasure path. Its commit
and acknowledgement boundaries are separate: a fault can leave a real fixture
reservation/event committed while withholding or corrupting the caller's reply.
Such a reply cannot be repaired by observing the resulting state. Database
transactionality, current project admission and permanent replay tombstones need
the PostgreSQL chat persistence tests.

The shared `AiHttpFixture` is supplied by the provider contract tests. It is an
owned real HTTP/1.1 peer, with authored SSE bytes, not an HTTP mock inserted into
the client. Its requests and complete JSON bodies are the dispatch oracle. Every
profile, secret reference, bearer value and mapping fact here is synthetic. No
test reads environment credentials or calls a live model. Later authorized test
execution uses owned ephemeral loopback sockets; authoring source does not open
them.

## Adversarial oracles

- A held original reservation acknowledgement allows zero credentials or wire
  requests despite a readable committed Uncertain round. Only exact Applied
  facts, request, round/fence/instance, version and counter increments admit the
  first request. Replayed/ambiguous/corrupt admission does not recover permission.
- Concurrent identical intents have one fresh admission and one billable
  dispatch. Re-entering Begin with the same intent after uncertainty is duplicate
  observation, not a second execution or a continuation mechanism.
- Revocation and changed turn/round facts after acknowledgement prevent dispatch.
  Initial private ownership/profile/metadata-destination refusal prevents provider
  and tool work. Tool boundaries recheck current authority before validation,
  after ToolStarted acknowledgement and after the original result returns.
- Lost append, tool-start/completion, round-completion or terminal
  acknowledgements never retry the uncertain publication, execute a tool twice or
  reserve another request. A single close may use only the last acknowledged
  version. An actual attempted request with a broken response is not resent.
- Text/usage retain encounter order, sequence numbers, unique event identities
  and serial fenced versions. Successful Stop publishes typed RoundCompleted
  before Completed. Refusal, filtering and output-limit are failed turns.
  Unknown usage remains null; partial known-zero accounting remains partial.
  A valid ChatCompletions terminal frame can carry both usage and final text;
  `Weft.Ai` emits that frame's usage before its text. Usage before any native finish
  is malformed wire input, not a valid ordering test.
- Withholding native DONE after an actually persisted leading text event permits
  no tool action. A complete tool stream whose real ordinary cleanup callback is
  held likewise permits no validation, tool event or execution. A held final
  schema validation proves the entire batch validates before the first action.
  Malformed/partial/conflicting terminals and unknown tool names admit no tools.
- Distinct native call IDs with identical names/arguments are separate ordered
  calls. The next captured wire request and exact retained bridge request contain
  complete corresponding results in order. Routine tool events contain identity
  and bounded accounting, never arguments, results or exception diagnostics.
- Loaded history is explicitly ordered, completed, same-conversation private
  ledger content. Only each final Stop round's answer is context; intermediate
  tool-round prose is not substituted for it. Missing/duplicate/self/reversed
  selection, malformed text envelopes, gaps and missing terminal facts fail closed.
  Duplicate/self/excess selected-history shapes are synchronous Begin argument
  refusals, not executions with a Completion result. Missing/reversed retained
  history is checked through the admitted execution. Ledger event pages accept
  only limits 1 through 100 and after-sequence values 0 through 4096; a valid
  completed history spanning multiple pages must preserve every text piece and
  advance its cursor without gaps. Scan budgets are separate from page size.
- History turns/events/bytes, logical context, serialized requests, cumulative
  output/calls/iterations, and cumulative tool result bytes/nodes have refusal
  cases. Results remain whole JSON, never truncated or silently dropped to fit.
  Event count/byte exhaustion cannot authorize retries or an unbounded loop.
- Exactly zero remaining retrieved nodes is a valid tool allowance when bytes,
  time, calls, iterations and rounds remain. In a fully validated ordered batch,
  one result may consume the exact node ceiling and a following complete
  zero-node result still completes normally. The same fixture's positive-node
  twin at that zero allowance fails BudgetExceeded before ToolCompleted or a
  continuation request. Spare byte/call/round limits prevent masking by another
  bound. Both exact complete results and call IDs appear in successful wire and
  retained request history; the failed twin admits no continuation.

## Ledger accounting and event identity

The persisted turn-terminal publication increments version and event count but does
not increment EventBytes. Its typed metadata uses the separate fixed 2 KiB
terminal reserve. RoundCompleted metadata is ordinary counted content; terminal
metadata is not. Seeded history follows the same accounting rule. A successful
typed terminal must not become PersistenceUncertain because fixture counters
charged its reserved metadata twice.

Round-scoped text/usage/tool/RoundCompleted events identify their round in
RoundId. Started and turn-terminal events have RoundId=null. A turn terminal's
usage/failure metadata identifies the provider round; consumers must not infer a
different ledger row shape from that metadata.

## Lifetime evidence

The fixture's gate is inside the original public port operation. Captured
credential, tool and timer-disposal tasks are the exact tasks returned by those
ports, not `WaitAsync` wrappers. A signal published after capture avoids confusing
gate entry with capture completion. While a gate is held, its task, the original
execution Completion and ReleaseSafe are checked directly. Opening the gate is
not settlement evidence: tests then join the original execution and check the
captured task's actual successful/faulted completion.

Store acknowledgement gates are awaited inside the original public store method,
after the fixture mutation. Completion must not skip those original methods to
publish a parallel terminal. These tests do not claim the gate's `Returned`
signal is the whole store task; it identifies a checkpoint inside that task.
The append fixture additionally retains the exact Task returned from its public
AppendAsync. A held application acknowledgement keeps that original task and
execution Completion pending and ReleaseSafe=false. It does not prove a provider
capacity slot remains held: cancelled provider cleanup can settle while the
application consumer is still waiting. Busy assertions require an actual held
provider operation or provider cleanup gate, never just an application waiter.

An observer token or observer timeout cannot request provider cancellation, free
capacity or replace Completion. Actual CancelAsync, caller-token cancellation and
trusted deadline request real cancellation but still await noncooperative work.
Capacity is tested through real public OpenTurn admissions: retain the other 31
leases in the client's 32-slot limit, require Busy while original admitted
work/cleanup is held, and require exactly one recovered slot after successful
settlement. A cleanup failure remains quarantine, including after another cancel.

Ordinary Stop/tool cleanup tests do not cancel or dispose the execution before
their callback signal. That signal is an actual invocation from the provider
cleanup path; a server write/close alone is not used to claim enumeration ended.
Actual text delivery is separately observed at the ledger port. Event-specific
terminal delivery and every private MoveNext/disposal task are not directly
exposed by the sealed provider/orchestrator APIs. Do not describe these indirect
causal checks as a private event enumerator interception or coverage proof.

Caller cancellation inside a policy callback is still inside the recheck: its
subsequent token check may correctly reject before admission. That early case
must have zero provider timer creation, credentials, dispatch and tool actions,
with ordinary successful cleanup and ReleaseSafe=true.

The separate admitted-cancellation test holds the actual provider clock
CreateTimer operation. Its entry proves network-free admission has begun after
the fresh recheck; only then does the test request/join caller cancellation.
Admission remains uncancelable until its actual lease is captured. After timer
creation returns, cancellation is observed before credentials, dispatch or tools.
A second gate inside the actual provider timer Dispose operation holds original
cleanup, Completion and ReleaseSafe, with public capacity Busy. Successful
cleanup yields Cancelled/ReleaseSafe=true and recovered capacity before shared
owner shutdown. Neither a gate's entry/return checkpoint nor a public waiter is
mislabeled as the unexposed private AiTurnLease disposal Task.

The synchronous OpenTurn API can still return no lease/task for genuine internal
deadline, shared shutdown or initialization failures. Public clock gates exercise
the initialization exception path; it requires permanent
CleanupUnproven/ReleaseSafe=false. Ordinary caller cancellation during network-free
admission is not a hidden-fault quarantine oracle. Actual cleanup failures and the
independent application-timer disposal fault still quarantine unchanged. The
shared client's actual owner DisposeAsync is joined separately; it does not turn
an unreturned per-turn task into an observable one. There is no public oracle for
its exact identity or internal settlement instant. Do not fabricate one with
reflection, a fresh waiter, a timeout, or a disposed borrowed global client.
In the `Weft.Ai` throwing-initialization path, failure is latched: actual owner
DisposeAsync faults with the fixed categorical cleanup error and returns the
same cached task on every call. The test and fixture teardown must both expect
that same cached failure, not a successful shutdown. ReleaseSafe stays false.

Application timer async-disposal is injected separately from provider clocks. Its
original task is held and faulted through the public TimeProvider/ITimer seam.
This proves the application lifetime boundary without selecting a private timer
ordinal, substituting a provider stream or inventing capacity recovery.

## Limits of these tests

These tests compose the public provider and ledger contracts. End-to-end
output/request/call limits can be enforced by either real layer; without a new
seam they cannot independently prove both redundant checks executed.

Resolve a failing case against the provider and ledger contracts; do not weaken
lifetime or no-retry assertions to obtain a passing run. Database, live provider,
host, browser and mapping-specific citation behavior are covered elsewhere.
