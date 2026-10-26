# Mapping explanation tools

`Weft.Application.MappingTools` supplies the five read-only tools a private chat
turn may call and the citation check used before final answer delivery. It reads
one conversation's immutable revision and exact retained projection/validation
reports. It has no ASP.NET, storage, worker, derivation, payload or provider
transport dependency and registers no routes or services itself.

## Composition

| Type | Role |
| --- | --- |
| `MappingToolRegistry.Definitions` | The ordered `AiToolDefinition` manifest. Pass it as the orchestrator's `tools` and to `ChatAiBridge.PinGroundingPolicy`. |
| `MappingToolService` | The `IChatReadOnlyToolPort` and `IChatCitationPort` passed to `ChatTurnOrchestrator`, plus `ValidateCitationsAsync` and `FilterCitationsAsync`. |
| `MappingToolLimits` | Optional trusted limits; defaults apply when omitted. |

Construct one shared instance per host:

```csharp
var tools = new MappingToolService(metadataReader, reportReader, limits: null);
```

`metadataReader` is the host's `IRevisionReportMetadataReader`
(`PostgresRevisionReportMetadataReader`); `reportReader` is the shared
`RevisionReportService` as `IRevisionReportReader`. These remain the authority
and bounded object-read boundary. The service owns no other port, so it cannot
select a current report, call Ensure/repair, read native or payload bytes, or
contact a provider. Its local capacity of two concurrent reads/checks belongs to
the instance; creating one per request would bypass that bound.

[Workspace chat](workspace-chat.md) reuses the same five tools at any readable
revision through `MappingToolService.InvokeAsync(actor, MappingReportContext, …)`.
There the revision and its current retained report pins are resolved by the
workspace tool service, which never derives a report.

## Authority and report identity

Each call is bound to `ChatReadOnlyToolContext.Actor` and
`ChatReadOnlyToolContext.Conversation`. The conversation's complete revision
scope and its two `ChatReportPin`s are the only selectors: model arguments never
carry an actor, scope, report, URL, path or backend. A conversation is
creator-private, so an actor other than its creator is refused before any read.

Every execution and citation check, including invalid calls and capacity
refusals, asks `IRevisionReportMetadataReader.GetSourceAsync` for current
active-principal, organization admission and project ReadMetadata authority.
The returned actor and revision must match. Authority is checked again after the
result is prepared and the immutable source must be unchanged; no earlier
observation in the turn grants access.

Reads use only `IRevisionReportReader.GetRetainedAsync` with the pinned
derivation UUID. The returned actor/source, operation and `SelectedResult` must
agree, and the verified report's cache entry, derivation, complete-key
fingerprint and derived-object digest must equal the pin. A historical pinned
attempt stays readable after the current selection changes and need not have
been derived by the present reader. Validation reads request the conversation's
projection derivation as their link selection, so diagnostics carry projection
locators. The linked projection is then read and checked against its own pin;
an incompatible pair is reported unavailable rather than read unlinked.

A hidden or missing retained attempt returns `notAuthorizedOrNotFound`. Missing
objects, integrity/format failures and unavailable link selections return
`reportUnavailable`. None of these start derivation, repair or provider work.

## Port behavior

`ValidateAsync` is side-effect free: it checks conversation ownership and parses
the closed name/schema without authority or report I/O. A false result makes the
orchestrator refuse the whole batch before any tool starts. `ExecuteAsync`
repeats the schema check, then authorizes and reads. Caller cancellation throws
`OperationCanceledException` with the caller's token, even when a dependency
faults concurrently. Every other refusal is a complete categorical result.

`InvokeAsync` is the typed form of `ExecuteAsync`. Its `MappingToolReply` has
the categorical `Outcome`, actual `Coverage`, typed `Data`, unverified citation
`Citations`, the complete `Result` (`AiJsonObject`) and `RetrievedNodes`.
`Result`, `RetrievedNodes` and the unverified candidates cross the port; only
`Result` enters provider history.

## Closed registry

| Tool | Arguments | Result |
| --- | --- | --- |
| `get_mapping_summary` | empty object | Boundaries, controls, capabilities, document/operation diagnostics, counts and actual coverage. |
| `search_mapping` | required `query`; optional `limit` | Field/rule hits with typed paths and native locators, and explicit truncation. |
| `get_field_lineage` | required `fieldId`; optional `maxNodes` | The field, upstream/downstream structural dependencies, conservative affected output fields and explicit truncation. |
| `get_rule_details` | required `ruleId` | The exact projected rule and controls governing that rule or its output, including transitive control governance. |
| `get_validation_issues` | optional `offset`, `limit` | Exact diagnostics with zero-based ordinals, validity, original counts and independent report/window truncation. |

Arguments are a strict object of at most 4,096 UTF-8 bytes and two container
levels. Unknown, case-altered or duplicate keys, wrong types, non-integer numeric
lexemes, out-of-range counts and unknown tool names fail closed. Queries are
1–128 UTF-16 code units and not whitespace-only; IDs are 1–512; control
characters are refused. Values remain untrusted data even when they contain tool
names, instructions or URLs.

Search is literal ordinal-ignore-case matching, not a pattern. Fields are visited
in retained order, then rules. It searches field IDs, names and individual typed
path components (including namespaces and exact index text), and rule
IDs/summaries. The first omitted match sets `truncated`.

Lineage edges point from dependent to dependency. Rule production, typed rule
dependencies, parameter defaults and control governance remain separate kinds;
value/default/condition roles, constants and original relationships are kept.
Writes and declared production reverse direction for traversal; boundary
membership creates no dependency. Reachability is conservative structural
context, not proof of runtime influence. Each direction includes the root and
visits at most `maxNodes`; omitted nodes set `truncated`. Rule details read no
native bytes: the typed projection is the entire available evidence.

## Results and limits

The envelope is `weft.mapping-tool-result.v1`, with `outcome`,
`evidence: "explanationOnly"`, nullable `coverage`, typed `data` and
`citations`. Null coverage means unknown, never complete; partial coverage and
gaps are returned unchanged. An absent field or rule ID returns `unknown`, not a
claim that the mapping lacks it. Exact lexical numbers, IEEE bit text,
absent/explicit-null/XML-nil, typed paths, native locators and unsupported
constructs use the existing projection contract types unchanged.

Defaults are 100,000 traversal/text work units, 24,576 result bytes, 20 result
items, 64 lineage nodes per direction, 64 citations and ten seconds. Hard ceilings
are 1,000,000, 32,768, 20, 64, 64 and thirty seconds. Each call also honors the
context's remaining turn budgets: the result byte limit is the smaller of the
configured limit and `RemainingResultBytes`, and `RetrievedNodes` must fit
`RemainingRetrievedNodes`. Retrieved nodes count the boundaries, controls,
capabilities and diagnostics of a summary, search hits, distinct lineage nodes,
a rule plus its controls, or emitted validation diagnostics.

Results are refused whole with `resourceLimit` when work, citations, nodes,
bytes or the provider JSON ceilings (depth, tokens, elements, Unicode, unique
keys enforced by `AiJsonObject`) are exceeded. Literals, details and citations
are never cropped to fit, and a refusal is not partial knowledge.

Failure envelopes and every `ToString()` contain categorical values only, never
arguments, mapping text, locations or exception details. The module does not log.
Result JSON is confidential mapping metadata, not diagnostic text.

## Verified citations

Tool results list citation candidates as `ChatCitationTarget` values
(`kind`, `resourceId`, `diagnosticOrdinal`). A projection node cites its
boundary/field/parameter/rule/control kind and ID; whole reports cite
`projectionReport` or `validationReport`. Validation diagnostics cite their
zero-based ordinal in the pinned validation report; no stable issue UUID is
invented and identical codes or messages do not establish identity.

`ValidateCitationsAsync(actor, conversation, targets)` freshly authorizes,
reads each needed pinned report once through the same checks as tool execution,
builds `ChatValidatedCitation` for every target and authorizes again. Any
missing resource, wrong kind, out-of-range ordinal, null entry or more than the
configured citation count fails the whole check. Only the owned `Citations` of a
`Valid` `MappingCitationCheck` may be persisted with `ChatCitationEventData` or
rendered. A valid citation proves the referenced resource exists in the pinned
report, not that the model's interpretation is correct or that anything ran.

`FilterCitationsAsync` performs the same check but omits a target that is
missing from its pinned report, has the wrong kind or an out-of-range ordinal;
its `Valid` result holds the verified subset in candidate order. Null entries,
too many targets, authority, report, capacity and time failures still yield no
citations. The `IChatCitationPort` implementation used for
[chat turn delivery](chat-turn-orchestration.md#citation-delivery) returns that
subset, or nothing when the check is not `Valid`.
