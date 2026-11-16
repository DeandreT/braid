# Chat persistence contract v1

## Canonical bytes

Canonical JSON is UTF-8 without BOM, compact, written by `Utf8JsonWriter` with its
default encoder and the source's fixed property order. GUIDs are lower-case D;
timestamps use invariant round-trip `O` spelling at UTC microsecond precision;
integers are JSON integers; optional values are explicit nulls. Strings are not
Unicode-normalized. Arrays preserve input order. Restore re-emits and compares
the complete canonical string, rejecting extra/duplicate fields, alternate
versions, alternate escaping/numbers and omitted nulls. SHA256 hashes canonical
UTF-8, not a platform string or a provider wire request.

The exact emitted field order and closed shapes are owned by:

- `ChatOriginalEnvelope`: `braid.chat.original.v1`, explicit request UUID, text,
  ordered selectors, explicit grounding-policy ID/version, instruction/tool
  fingerprints, retrieval-node/tool-result-byte/tool-iteration budgets, and
  provider duration/round/request/wire/text/tool/output budgets. Selectors are at
  most 32 distinct typed resources/diagnostic ordinals.
- `ChatCanonicalEnvelope.Turn`: `braid.chat.turn.v1`, stable creator and complete
  scope, conversation UUID, retention instant, exact projection/validation pins,
  turn UUID, embedded original envelope, complete profile pin and fingerprint
  schema, persistence budget and absolute turn deadline. Stored original text,
  request UUID, canonical envelope and fingerprint must match on reopen.
- A workspace turn's original envelope is `braid.chat.original.v2`: the v1 fields
  plus the turn's resolved focus (`None`, `Unavailable`, or `Resolved` with its
  complete revision scope). Its canonical turn is `braid.chat.workspace-turn.v1`:
  the organization, creator, conversation UUID, optional conversation focus and
  retention instant in place of the revision scope and report pins, then the same
  turn fields. Revision turns keep both v1 shapes.
- `ChatAiBridge.PinProfile`: `braid.chat.profile.v1`, provider ID and **every**
  nonsecret `AiProfileSpec` property: key/version, dialect, model, absolute
  normalized endpoint, loopback-fixture flag, authentication mode, credential
  reference, capabilities, metadata-policy ID/version/allow flag, think-false
   flag, and the payload flag only when it is set. No secret resolver/value is used. Configuration changes under an existing
  key therefore fail pin matching. The canonical profile body is not persisted;
  its versioned fingerprint and explicit display/policy identity are.
- `ChatAiBridge.PinGroundingPolicy` pins exact trusted instruction UTF-8 with
  SHA256, and a fixed-order `braid.chat.tools.v1` manifest containing every approved
  tool name/description/parameter JSON string. The explicit policy version and
   retrieval bounds are retained in the original envelope. The orchestrator resolves this
  trusted policy, never a browser/model-supplied instruction or tool definition.
- `ChatAiBridge.PrepareRound`: `braid.chat.round.v1`, full-turn fingerprint,
  **the exact `AiRoundRequest.RoundId`**, output-token limit, ordered messages and
  ordered tools. Every system/user/assistant/tool field, ordered assistant intent,
  call/result ID/name, tool description/schema and argument/result JSON string
  is retained. Embedded JSON-object strings preserve exact factory-owned bytes,
  including their property order/whitespace; they are not semantically reordered.

Low-level persistence round facts are opaque exact request pins, not a way to
manufacture an outbound request. Dispatch uses the sealed `ChatPreparedRound`
produced by this bridge, not reconstruction from a stored string. It accepts only
the original successful matching reservation acknowledgement and can hand out
its immutable request once. Replayed/observed/uncertain acknowledgements refuse.

## `Braid.Ai` bridge contract

`ChatAiBridge.ProfileKey` verifies the supplied resolved immutable profile against
the retained profile pin before returning its frozen `AiProfileKey`.
`OpenLimits` takes a fresh admitted instant and active turn facts. Duration is
the minimum of remaining pinned provider duration (anchored at persisted turn
creation) and the remaining absolute turn deadline; elapsed preparation cannot
restart the duration. Expired/terminal facts refuse.

`PrepareRound` verifies the same resolved profile, resolved grounding-policy pin and mapping-metadata policy,
the pinned output budget and qualified round ceiling, the exact first system
message's instruction fingerprint, and the entire approved tool manifest.
Untrusted mapping context belongs in user/tool data, not appended to the pinned
trusted system instruction. A changed instruction/template/tool schema or policy
version is a different original input, never a silent continuation. It does not resolve an
endpoint/credential, create an `AiRoundClient`, call `OpenTurn`/`OpenRoundAsync`,
execute tools or enumerate a provider. `Usage`, `Failure` and `Completion` map
only frozen public fact types. `Rejection` records an OpenTurn/OpenRound refusal
against the already reserved round, preserving unknown usage and the exact
NotAttempted/Attempted observation. All failure/dispatch/finish enum cases are
explicitly mapped; unknown cases refuse.

The orchestrator owns fresh identity/project/data-policy/tool admission, exact retained report
retrieval, context construction, complete stream enumeration, live lease and
cancellation ownership. Reserve the first round before even opening the local
provider turn lease, so both TurnRejected and RoundRejected have a durable round
identity. Only call the prepared request after durable reservation acknowledgement.
Fully finish enumeration before committing a
ToolCalls continuation or executing a tool. A later round has a newly reserved
UUID/fence on the same already bounded live provider lease and process-instance
identity. Instance IDs are fresh per process, not rehydrated from stored facts.
Restart never takes over a turn which already reserved a round, even if the last
round had completed ToolCalls; it explicitly interrupts instead. A zero-round
turn has made no provider call under this admission ordering.

## Events and accounting

Event numbers are 1 start, 2 text, 3/4 tool activity, 5
completed, 6 failed, 7 cancelled, 8 interrupted, 9 usage, 10 closed
provider failure, 11 validated citation and 12 round completion. Workspace
conversations add 13 workspace citation and 14 shared data; kind 11 is admitted
only in revision conversations and kinds 13 and 14 only in workspace
conversations. All are ordered
by durable sequence, not provider arrival timestamps. Event-ID replay compares
kind, exact text/metadata, round and fence and never appends again.

SQL metadata admission distinguishes missing properties from explicit JSON null
and checks each required property's JSON type before casts and semantic checks.
Version, round/report/scope identities, enum values and usage provenance cannot
be null or a coercible wrong type. Nullable token/accounting/target properties
must still be present, with null permitted only by that metadata shape. In
particular a whole-report citation has null resource/ordinal, not null scope;
diagnostics require an integer ordinal and resource targets require a string.
The predicate returns true/false, never SQL NULL; exact scope matching is also
null-safe. Valid optional usage and unknown counts remain unchanged.

Usage stores round UUID, source (`ProviderObservation`, `ProviderRoundTerminal`
or `ProviderOpenRejection`) and independent nullable input/output/total counts.
`IsKnown` means any count exists, not that every count exists. Each count is
0–10^12; all-null is unknown. Counts are snapshots for that round and source,
not increments to sum on reconnect. An open rejection has all-null usage because
the public rejection exposes none; its dispatch observation may still be Attempted.
No pricing/cost value is invented.

Provider failures contain round UUID, the closed `Braid.Ai` failure category and
NotAttempted/Attempted observation. This observation **never** changes a durable
reservation into retry permission. Terminal/round-completion metadata may also
retain the closed Stop/ToolCalls/OutputLimit/Refused/ContentFiltered finish.

Typed round completion writes one accounting event and its immutable round
result in the same transaction. Failure of any publication leaves the prior
state; no acknowledged outcome escapes COMMIT failure. On content-budget
exhaustion, the caller can conditionally publish a failed/cancelled/interrupted
turn with at most 2 KiB of typed accounting in its reserved terminal event. The
potentially billed round stays uncertain if round completion could not commit.

Citation metadata is revision plus generic exact report pin plus target kind.
Whole projection/validation reports have no resource ID or diagnostic ordinal.
For boundary/field/parameter/rule/control, target is the verified resource ID.
For projection operation diagnostics or validation diagnostics, target is a
zero-based index in that exact report's emitted top-level `Diagnostics` array.
Total issue counts do not make non-emitted diagnostics addressable. No diagnostic
UUID, implicit concatenation of diagnostic arrays, mutable selected-report
pointer or cross-revision interpretation is admitted.

The mapping-tool seam represents whole reports as operation plus revision and
retained derivation, nodes as a closed reference type and validation diagnostics
as emitted ordinals. Whole reports map to ProjectionReport/ValidationReport
targets and node types to the corresponding resource kind. The fresh citation
check obtains the exact verified report and constructs `ChatValidatedCitation`
for each target it can verify; the turn controller publishes only those, after
rechecking authority, and omits every other candidate. Exact
report/ordinal addresses can reconstruct native/projection locators from that
retained report under fresh authority; the controller must never infer a new
locator, substitute a selected report or render an unvalidated model string.

## Lifecycle and erasure

Ordinary content access always uses current owner/project ACL. The lifecycle
profile has only exact-target, no-content routines:

1. Observe phase/version, without listing targets or returning private facts.
2. CAS an active turn to cancelled/interrupted with optional bounded typed final
   accounting for its exact last round; leave uncertainty unchanged.
3. Erase expired closed content with explicit policy ID/version and identity
   retirement-not-before input.

Conversation-row locks serialize these operations with owner writes. They expose
no administrator message/report/request access. Fixed-search-path owner-defined
functions encapsulate needed writes. Both conservation checks remain SECURITY
INVOKER with queued-row function-local scope. Lifecycle close/erase defer and
flush only chat constraints before returning from the trusted routine, then leave
them deferred; no content privilege is given to the lifecycle login at COMMIT.
All profiles use the exact no-content `chat_schema_lock` capability, with a
pre-call exact routine check and full post-lock schema/role admission. It grants
no content or identity listing. The journal's version/checksum are the lifecycle
login's only column reads. See the security document for the full boundary.

Identity reservations are permanent within organization/creator/kind/UUID. They
become minimal non-content tombstones atomically with erasure. The mandatory
retirement input is a policy minimum; no automatic expiry, identity recycling or
physical deletion operation exists. Even after that instant, stale conversation,
turn, original-request and round identities remain rejected. This preserves
billed replay safety without retaining prompts, responses, their hashes or model
configuration.

Live cancellation connects to the controller's actual cancellation source
and `AiTurnLease`. No lifecycle/database method stops an already dispatched
provider socket by itself.
