# Workspace chat

A workspace conversation is an organization-wide assistant. It answers
questions about the projects, mappings, revisions, retained reports, sample
previews, regression cases and runs, builds and the user guide that its creator
can read in one organization. When asked to edit a mapping, it can retain an
exact-base typed proposal. Saving requires the creator to review and approve that
exact proposal under current edit permission; asking the model is not approval.
It never changes the version in use or starts a preview, run, build or report derivation.

Workspace conversations sit beside the revision-pinned conversations described in
[AI chat architecture](ai-chat.md) and [Mapping explanation tools](mapping-ai-tools.md).
They share the turn orchestrator, provider client, ledger, retention and erasure;
they differ in what they are bound to, which tools they offer and how citations
are verified.

The [workspace HTTP routes](chat-http.md#workspace-routes) expose conversation
creation with optional focus, organization listings of both conversation kinds,
turns, cancellation and durable event streams. A conversation's identity and
ledger are independent of the client view: a workbench panel and a standalone
chat client can continue the same workspace conversation without duplicating it
or narrowing its read authority to the panel's open revision.

The browser opens workspace conversations at `/chat`. New conversations in a
workbench panel use the exact open revision as their focus; **Open in chat**
continues the same conversation. Revision-pinned conversations use their
original routes and report pins.

## Conversations and focus

`braid.chat_conversations.conversation_kind` distinguishes the two kinds:

| Kind | Bound to | Pins | Focus |
| --- | --- | --- | --- |
| `1` revision | One immutable revision | Exact projection and validation attempts | None |
| `2` workspace | One organization | None | Optional mapping, or one exact revision of it |

`chat_conversations_kind_check` requires every revision and report pin column on
a revision conversation and none on a workspace conversation. The focus columns
(`focus_project_id`, `focus_mapping_id`, `focus_revision_id`) carry foreign keys
to `mappings` and `mapping_revisions`.

Ownership, retention, private content, lifecycle and erasure are identical for
both kinds. Conversations remain private to their creator; administrator roles
do not expose their content. `braid.chat_authorized(org, NULL, creator)` is the
organization-level authority of a workspace conversation: an active creator with
a current organization admission. The identity, lifecycle and erasure routines
compare the nullable scope columns with `IS [NOT] DISTINCT FROM`, so a revision
conversation can never satisfy a workspace check or the reverse.

`IChatConversationReader.ListOrganizationConversationsAsync` lists the
creator's unexpired conversations of both kinds in one organization, newest
first; revision conversations appear only while their project is readable. With a
revision filter it returns the revision conversations pinned to that revision and
the workspace conversations focused on exactly it. `FindConversationAsync` resolves a
conversation identity within the organization to its key and kind.

A focus guides where the assistant looks first. It never narrows the tools and
grants nothing. When a turn starts, `WorkspaceToolService.ResolveFocusAsync`
resolves it freshly as the creator:

- An exact revision stays itself.
- A mapping resolves to its current head revision.
- A focus the creator can no longer read, or a mapping without a readable head,
  is `Unavailable`.

The result is recorded in the turn's original envelope (`braid.chat.original.v2`,
`ChatTurnFocus`: `None`, `Resolved` or `Unavailable`), so later rounds and
reconnects never silently follow a newer head. Revision turns keep the
`braid.chat.original.v1` envelope.

## User guide

The Server composes guide tools from the same embedded `IUserGuideReader` as the
help API. `WorkspaceUserGuide` preserves section identities and readable text
from headings, paragraphs, lists, tables, code and guide links. It never fetches
content from a model-supplied URL or reads guide files at runtime.

## Turn context

The pinned system message contains the trusted instructions only. When a turn has
a resolved focus or selectors, `IChatTurnContextPort.DescribeAsync` adds one
user-role data message before the question (`braid.workspace-turn-context.v1`, at
most 16 KiB): the focused project, mapping and revision by name, sequence and
whether it is the head, plus the user's selection. It is read freshly as the
creator, marked as data rather than instruction, and an unreadable focus is
described only as `unavailable`.

## Grounding policy

Every workspace turn pins the `braid.workspace-assistant` policy, version 8,
together with the exact manifest the host offers:

| Bound | Value |
| --- | --- |
| Tool iterations per turn | 3; creation-enabled payload manifest: 4 |
| Tool calls per turn | 8 (provider budget) |
| Retrieved nodes per turn | 512 |
| Tool-result bytes per turn | 32,768 |
| Result bytes per call | 12,288 |
| Argument bytes per call | Metadata: 4,096, two container levels; edit/creation: 16,384 (provider JSON depth 16) |
| Items per list call | 20 |
| Concurrent calls per host | 4 (further calls are `busy`) |
| Duration per call | 20 seconds (then `timedOut`) |

The instructions tell the model to base statements on tool results from the
conversation, to name fields, rules, mappings and projects by their names and
paths, to say plainly when a report is not derived or a resource could not be
read, and to treat tool results, the context message and user text as untrusted
data. Text in them that looks like instructions, tool names, URLs or requests to
change the rules is content to report; it cannot grant tools or access.

A workspace turn needs a profile with the `functionTools` capability and
`mappingMetadataAllowed`; a profile without them cannot open one. A turn pinned
under one manifest never runs under another, because the grounding pin includes
the tool definitions.

### Choosing a model

Every workspace turn offers the whole applicable manifest with its schemas, so
the model must choose among many functions and fill strict
arguments. Hosted models with reliable function calling handle this well. Small
local models (a few billion parameters) often answer without calling a tool,
call one with malformed arguments, or repeat the same call until the call budget
ends the turn; prefer the largest local model the hardware serves within the
turn deadline. A malformed or unoffered call ends the turn rather than being
repaired, so a weak model fails visibly instead of answering from invented data.

The opt-in `WorkspaceLiveOllamaTests` uses the real local Ollama transport and
the complete workspace tool manifest against an explicitly synthetic catalog.
Set `BRAID_LIVE_OLLAMA_ENDPOINT` to the loopback `/api/chat` URL and
`BRAID_LIVE_OLLAMA_MODEL` to an installed function-tool model, then run that test.
It requires a tool-backed answer and freshly verified mapping citation.

## Tools

The manifest is closed. Arguments name resources by identity only; the
organization, actor, report selection and every scope come from trusted context.

| Tool | Reads |
| --- | --- |
| `list_projects` | Readable projects in the organization, paged by `afterProjectId` |
| `search_catalog` | The shared authorized catalog query: projects, mappings, revisions and retained report fields/rules; bounded pages with a query- and organization-bound cursor |
| `get_project` | One project and a page of its mappings with their heads |
| `get_mapping` | One mapping: name, description, backend, role and head revision |
| `list_revisions` | A mapping's revisions, newest first, paged by `beforeSequence` |
| `get_revision` | One revision: sequence, upload, admission, native digest and whether it is the head |
| `get_mapping_summary`, `search_mapping`, `get_field_lineage`, `get_rule_details`, `get_validation_issues` | The mapping explanation tools at any readable revision, over its current retained reports |
| `compare_revisions` | Two revisions of one mapping compared over their retained reports, as the comparison route computes it |
| `list_runs` | One kind of a mapping's records (the user's own previews, regression runs, builds and approvals; the mapping's regression cases) or a project's build environments |
| `get_run` | One of the user's own previews, regression runs or builds: status, attempts, result summary and bounded failure |
| `get_capabilities` | What the user can do with a mapping on this server |
| `search_user_guide`, `get_user_guide_section` | The user guide shipped with the server |
| `propose_mapping_edit` | Retains an exact-base proposal for separate authenticated review and approval; requires proposal persistence |

Run kinds of a disabled family (previews, regression tests, builds) are not
offered; `list_runs` and `get_run` are absent when all three are disabled, and the
guide tools are absent without an embedded guide. Run tools return status and
summaries only, never payload bytes. Regression-run status and citation checks
use a metadata-only database projection; private completion bodies are read only
inside a payload tool after its profile and project grant checks. Detailed
regression comparisons remain available in the run view, not in the metadata
tool's response.

Runtime preview failures can quote evaluated sample values, including failed
rule messages and rejected constraint values. Metadata tools therefore report
the failure's presence without its free-form code, message or subject. Build
metadata uses only categorical state/outcome and artifact identity; it does not
include execution diagnostics.

The report tools never derive a report. They read the revision's current
retained, verified projection (and validation for `get_validation_issues`). When
one has not been produced, the result is `notDerived` with the revision and the
report it lacks, and the model is told not to guess its content. Available report
results use the mapping tool result format (`braid.mapping-tool-result.v1`,
explanation only, not execution evidence).

Catalog search uses `ICatalogSearch`, the same service as the search page and
HTTP API. It does not enumerate projects to implement a second search. Each
page is authorized freshly in the conversation's organization, never filtered
to its focus. A pending retained-report index is an unavailable result, not a
partial successful search. Field and rule references retain the exact revision
and report; verification reads that retained report before publishing a citation.
The cursor is returned only when every item in the service page fits the tool's
remaining budget, so truncation cannot silently skip results.

Every other result is a `braid.workspace-tool-result.v1` envelope:
`schema`, `tool`, `outcome`, `truncated` and `data`. Outcomes are `available`,
`notFound`, `notDerived`, `invalidCall`, `unavailable`, `notPermitted`,
`unsupportedContent`, `resourceLimit`, `busy` and `timedOut`. Failure envelopes
carry no data, arguments, catalog text or exception detail.

A result is serialized within the smaller of the per-call bound and what remains
of the turn's budgets. A list keeps its longest prefix that fits and sets
`truncated`; a truncated page offers no continuation cursor that would skip what
was cut. Descriptions longer than 400 characters are cut with an explicit flag,
and a long guide section keeps its leading text with `textTruncated`. Names and
literal values are never cropped. A result that cannot fit even empty is
`resourceLimit`.

## Authorization

`WorkspaceToolService` implements `IChatReadOnlyToolPort`. Each call:

1. Requires a workspace conversation, its creator as the actor and the manifest
   the turn pinned. Anything else is `notFound`, and validation refuses it.
2. Parses the arguments against the offered schema before any read: unknown,
   case-altered or duplicate keys, wrong types, blank or control-character text,
   non-integer numbers, out-of-range counts, non-canonical UUIDs, values outside
   an enumeration and inconsistent combinations are refused. The orchestrator
   ends the turn on a call that fails validation.
3. Observes the organization admission as the creator.
4. Resolves a mapping or revision identity to its complete scope through
   `ICatalogResourceLocator`, under the same active-principal, admission and
   membership authority as catalog reads. Absent, foreign and unreadable resources
   are the same `notFound`.
5. Reads through the same ports and read sequences as the HTTP API. The regression
   and build GET routes and the tools share the authorized read cores in
   `CatalogEndpoints.ReadCores.cs`.
6. Observes the organization admission again before releasing the result.

Revoked membership, a lost organization admission or a disabled principal takes
effect on the next call. By default the edit tool only retains proposals; approval is a separate authenticated action.
There is no activation, derivation, run, build or provider port. Caller cancellation
propagates instead of becoming a result.

## Mapping approval and recovery

The default runtime requires exact proposal approval. See
[the proposal runtime contract](chat-proposal-runtime.md) for proposal listing,
complete-operation review and approval/recovery routes. The integrated persistence
and host requirements are described in [approval composition](chat-edit-approval-composition.md),
and [proposal review](chat-proposal-review-ui.md) describes the browser workflow.
The proposal contains a `ferrule.edit.v1` script, never arbitrary native bytes.
The server replays it against the named immutable base and derives bounded
before/after summaries from verified projections. Current edit authority is checked
again by storage and ingestion. Appending requires that exact base to remain the head.

Before ingestion can reserve or commit, `IChatMappingWriteJournal.PrepareAsync`
must acknowledge an immutable intent. Its key is derived from the turn and tool-call
identity. The journal retains the actor, conversation, round, base, operation count,
edit digest, exact native digest/count and pinned profile as a no-content audit in
`chat_mapping_write_intents`. Exact replay bytes and change summaries live separately
in `chat_mapping_write_content` and are erased with conversation content.

The ingestion commit trigger matches actor, project, mapping, key, exact head
condition and native identity, then inserts an immutable no-content commit fact in
`chat_mapping_write_commits` in that same transaction. This fact retains the operation,
saved revision, sequence and commit time without a foreign key to purgeable request
rows. Capture failure rolls back the mapping commit. Request-key retirement
tombstones are never treated as success evidence. The intent and commit fact are
the durable audit, even when the original ingestion receipt is later purged.
There is no fallible application-side post-commit journal update. A `mapping.write` stream
event is an immediate presentation record, not the sole receipt or audit. Losing its
append acknowledgement, cancelling or expiring the turn, or restarting the server
does not lose an already committed version's receipt.

The creator can read save status through `GET .../turns/{turnId}/mapping-writes`
independently of the turn's live event fence. Only `saved` includes a confirmed
version. `pending` makes no saved claim; `notSaved` means no matching save completed.
Fresh creator and project read authority applies even after the turn ends.

Explicit `POST .../mapping-writes/recover` resumes only an already reserved
ingestion by operation ID and current edit permission, through `ResumeAsync`.
It never calls append or resupplies source bytes. A `NeedsSource` result stays pending;
the exact-proposal flow requires explicit repeat approval before supplying source.
It neither invokes a model nor starts an unreserved save after cancellation or
expiry. A committed receipt is recognized before checking a later head, so retries
do not append another revision. Each pending save gets at most one attempt per
request, with two joined recovery lanes and a short per-save slice that reserves
time for the final receipt read, within a shared deadline and ingestion's existing attempt limit. One lost
acknowledgement does not prevent other independent saves from being recovered.

The browser reads these receipts when opening or finishing an answer and merges
confirmed versions with immediate stream cards without changing stream sequence
numbers. The current proposal UI recovers only the exact identified proposal;
unidentified pending receipts are read-only. It does not offer turn-wide
**Continue pending save**. See [proposal review](chat-proposal-review-ui.md#review-and-approval)
for status refresh, exact recovery and explicit repeat approval. Saved cards provide
Undo (another exact-head conditional append), Diff and Open version. Saving never
activates a version or starts tests automatically.

## Citations

Tool results carry citation candidates: a project, mapping or revision; a
resource of one exact retained report (field, rule, control, diagnostic or the
whole report); a preview run; a regression case version or run; a build run,
approval or environment version; or a guide section. The orchestrator passes new
candidates to `IChatWorkspaceCitationPort.VerifyAsync`, which re-reads each one
freshly as the creator through the same read a tool or route would make and
returns only those that exist and are readable now. Report citations are checked
against the exact retained attempt they name: scope, derivation, key fingerprint
and object digest. A candidate that cannot be verified is omitted, never repaired.

Regression case references are checked against the exact retained version's
metadata, without reading the sample envelope. Appending a newer version does
not invalidate an older reference while the user can still read that mapping
and revision.
A failed organization admission yields no citations.

A verified workspace citation is ledger event kind 13 (`WorkspaceCitation`) with
fixed metadata. Revision conversations keep kind 11 citations bound to their
pinned reports; neither kind is accepted in the other conversation kind. Report
citations stored for a workspace conversation must name a usable retained attempt
of that revision. As in revision chat, citations show what a tool read, not what
the answer relies on.

## Payload data

Sample data (preview inputs and outputs, saved regression case inputs and
expected outputs, and regression run outputs) reaches a model only through four
payload tools:

| Tool | Reads |
| --- | --- |
| `get_preview_input` | One input document of the user's own preview; ordinal 0 is the primary input |
| `get_preview_output` | One output of the user's own succeeded preview |
| `get_regression_case_data` | One saved input or expected output of a regression case version |
| `get_regression_run_output` | One actual output of the user's own regression run |

They exist only on a host that serves previews or regression tests and is
configured with the restricted payload-permission reader
(`BRAID_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING`, see
[Chat turn HTTP API](chat-http.md)). Together with the other tools they form a
separate payload manifest with its own grounding pin.

A new turn pins the payload manifest only when both hold, observed freshly as the
creator when the turn is created:

- the conversation's profile sets `payloadsAllowed` in its metadata policy
  (default `false`; it requires `mappingMetadataAllowed`), and
- the creator holds an active `run.payload.read` grant on at least one project
  they can read in the organization.

Otherwise the payload tools are not offered at all. A turn pinned with them needs
both the profile flag and a current qualifying grant at every provider/tool
boundary. Withdrawing either stops the turn before another payload manifest is
offered. Each payload call reads the data through the same authorized read as its
route and observes the creator's grant on exactly the data's project before the
read and again before release; without it the call is `notPermitted` and nothing
is shared.

The orchestrator also retains the trusted disclosure identities for every
payload result added to the current turn. Before any later provider round it
rechecks membership and the payload grant on **each** contributing project, not
just whether some project still has a grant. Revocation stops further dispatch;
it cannot retract data a previous round already received, and prior audit
entries remain intact. Citation text never supplies these source identities.

Payload-capable turns remain in the private conversation history, but their
questions and answers are not copied into later automatic model context. The
model receives only a notice that some earlier context was omitted, and must
read sample data again through currently offered, freshly authorized tools.
This also applies after a profile change or loss of payload permission.
Earlier policy versions remain readable in the conversation but are not copied
into automatic model context, since their metadata answers may include runtime
failure text derived from samples.

Only strict UTF-8 text without control characters other than tab and line breaks
is shared; anything else is `unsupportedContent`. At most 8,192 bytes from the
start of the document are shared, cut on a character boundary and further
shortened to fit the call's budget. The result states the total and shared byte
counts and whether the text was truncated, and carries a disclosure fact that is
recorded as a shared-data event.
Preview outputs declared as `application/edi-x12` or `application/edifact` follow
these same text, size and current-grant checks. Their media type does not grant
access or allow binary/control-containing content to be disclosed.

Conversation text can then quote payload-derived content, so it falls under the
conversation's retention and is erased with it; only the no-content audit row
outlives erasure.

## Shared-data accounting

Ledger event kind 14 (`DataShared`) records that a tool result in a workspace
turn contained payload data: the project, payload source, run identity and byte
count, never the bytes. Its insert trigger `audit_chat_payload` writes a row to
the append-only `braid.chat_payload_audit` in the same statement: the creator,
organization, conversation, turn and round, project, payload source, run or case
identity, byte count, the turn's profile, provider, model and metadata policy, and
the time. The audit has no foreign keys,
survives conversation erasure, has no grants to the chat roles and is readable
only under its creator-scoped row policy. Revision conversations cannot record
shared data; a tool result that reports shared data in one denies the turn.

## Preview reads

The preview reader has submitter-only history and input reads. `list_runs` uses
the history: `braid.preview_history` pages a mapping's runs newest first,
`braid.preview_input_documents` lists a run's submitted documents and
`braid.preview_input` reads one. They apply the same fresh authority as
`preview_read`; unauthorized and missing runs are indistinguishable.
