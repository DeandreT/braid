# AI chat architecture

Status: proposed v1 boundary. The AI chat window is a core Weft feature.
No model service is connected by this architecture increment.

## User experience

Keep the conversation beside the selected mapping. Suggested questions include:

- "Where does invoice.total come from?"
- "Which records are excluded, and by which condition?"
- "What changes if this source field disappears?"
- "Explain this rule in business terms."

Each conversation pins a project and immutable mapping revision. The UI shows
the selected model profile and the revision being discussed. Switching mapping
revisions starts a new explicit context or requires a deliberate comparison;
old answers never silently become explanations of the new mapping.

Responses can link to fields, rules, and validation issues. Stream answer text
and bounded tool activity; allow stopping a turn. Missing context, unsupported
constructs, and provider failures are shown directly. Chat can be disabled by
organization policy without disabling the mapping viewer or preview.

## Model-independent boundary

Weft owns a conversation protocol rather than adopting one vendor's SDK models
as its domain. Provider adapters may use maintained SDKs internally.

| Contract | Application-owned contents |
| --- | --- |
| Model profile | Provider ID, model ID, endpoint reference, credentials reference, allowed data policy, advertised capabilities |
| Completion request | Trusted application instructions, bounded message history, pinned context, permitted tool schemas, output budget |
| Message | User/assistant text, attachment references, tool call/result IDs, verified citations |
| Stream event | `turn.started`, `text.delta`, `tool.started`, `tool.completed`, `usage`, `turn.completed`, `turn.failed`, `turn.cancelled` |
| Failure | Authentication, rate limit, unsupported capability, context/output limit, unavailable, timeout, cancellation |
| Usage | Known input/output usage and optional provider-specific accounting; unavailable usage remains unknown, not zero |

An adapter translates native role ordering, tool schemas, stop reasons, usage,
and streaming chunks into these contracts. It must advertise text/streaming/tool
support and model context constraints rather than silently dropping unsupported
features. Selecting a model that cannot support the requested grounded-chat
operation returns an explicit admission error; a later text-only mode can have
its own separately qualified behavior.

Profiles can point to hosted or local services. Local inference is a configurable
provider endpoint, not a requirement to ship model weights in Weft. Administrators
configure allowed endpoints and credential references; users select permitted
profiles rather than submitting arbitrary provider URLs to the API.

Changing a model profile changes provider-specific configuration, not mapping
tools or the browser chat protocol. Record the selected profile version and
model identity for every turn. Provider output is text/tool intent, never a
source of application authority.

## Provider adapters (`Weft.Ai`)

`dotnet/src/Weft.Ai` is a BCL-only module without its own host registration;
the Server composes one shared client when chat is configured (see
[Chat turn HTTP API](chat-http.md)). It speaks two fixed wire dialects over HTTP: Chat Completions SSE
(`ChatCompletionsSseV1`) and Ollama `/api/chat` NDJSON (`OllamaChatNdjsonV1`).
Callers see only Weft-owned types; vendor event names and raw bodies stay inside
the readers.

- **Profiles.** `AiProfileSpec` is immutable server configuration: profile key and
  version, dialect, model, fixed endpoint, `None` or `Bearer` authentication by
  credential reference, capabilities (`Text`, `FunctionTools`) and metadata
  policy. `AiProfileCatalog` admits them once. Callers never supply a URL.
  Credentials come from an `IAiCredentialResolver` and stay in a disposable
  `AiBearerSecret`; redirects are not followed, so they cannot forward them.
- **Turns and rounds.** `AiRoundClient.OpenTurn` admits a profile under
  `AiTurnLimits` (duration, rounds, request/wire/text bytes, tool calls) and a
  fixed concurrent-turn capacity. `OpenRoundAsync` sends one round exactly once,
  with no automatic retry. Only a fully valid `ToolCalls` terminal permits another
  round, under a new round ID, on the same turn; every other terminal closes it.
  History must end with a user message or completed tool results, and tool
  results must match earlier call IDs and names in order. A round waits at most
  60 seconds, within the turn deadline, for response headers: a local service can
  withhold them until it has generated a complete tool call.
- **Events.** A round stream yields `AiTextDelta`, `AiToolIntentReady`,
  `AiUsageObserved` and exactly one terminal: `AiRoundFinished` (`Stop`,
  `ToolCalls`, `OutputLimit`, `Refused`, `ContentFiltered`) or `AiRoundFailed`.
  Tool intents are complete, validated JSON objects; read the whole stream before
  executing any. Unknown usage stays unknown rather than zero.
- **Failures.** `AiFailure` has a closed code and records whether dispatch was
  attempted. Disallowed profiles, metadata policy, missing capabilities or
  credentials, busy capacity and over-budget requests fail with `NotAttempted`
  and make no request. Malformed, truncated, unsupported or over-limit responses
  fail closed with categorical, redacted text.
- **Cleanup.** A turn holds its capacity slot until its full cleanup succeeds.
  Native completion or a closed connection is not enough. Failed cleanup keeps
  the slot quarantined rather than releasing it.

Tool execution, project permission checks, conversation persistence and browser
streaming belong to the application layers above this module. The routes, server
profile configuration and SSE event stream are described in
[Chat turn HTTP API](chat-http.md).

### Azure OpenAI profiles

Azure OpenAI's v1 API speaks the Chat Completions dialect at
`https://<resource>.openai.azure.com/openai/v1/chat/completions`; `model` is the
deployment name. Its streams also carry content-filter annotations: a prompt
annotation before the completion, `content_filter_results` on chunks and, with an
asynchronous filter, annotation chunks for text already sent. They have an empty
`object` and carry no text, tool call or usage. A `content_filter` finish,
including one that arrives after another finish, ends the round as
`ContentFiltered`. A prompt the filter rejects is an HTTP 400, which the round
reports as `providerRejected`.

In a Session host on Azure Container Apps, a profile can authenticate with the
app's managed identity instead of a key (see [Azure deployment](azure-deployment.md#chat)):

```json
{
  "version": 1,
  "retentionDays": 30,
  "profiles": [
    {
      "id": "azure-openai",
      "version": 1,
      "displayName": "Azure OpenAI",
      "provider": "azure-openai",
      "dialect": "chatCompletionsSse",
      "model": "<deployment name>",
      "endpoint": "https://<resource>.openai.azure.com/openai/v1/chat/completions",
      "credentialReference": "azure-openai",
      "capabilities": ["text", "functionTools"],
      "metadataPolicy": { "id": "mapping-metadata", "version": 1, "mappingMetadataAllowed": true }
    }
  ],
  "credentials": [
    { "reference": "azure-openai", "managedIdentity": { "clientId": "<identity client ID>" } }
  ]
}
```

The identity needs the `Cognitive Services OpenAI User` role on the resource. With
an API key, the credential is a private file instead,
`{ "reference": "azure-openai", "file": "/run/weft/secrets/model-provider-credential" }`,
and the key is sent as the Bearer value. A profile's `mappingMetadataAllowed`
records that the deployment may receive mapping metadata; set it only when the
resource's data handling permits that.

### Live provider check

Ordinary test runs use the owned HTTP fixtures only. `AiProviderLiveTests` runs a
streamed text round and a tool round followed by an answer against a real local
Ollama service when both variables are set:

```sh
docker run -d --name weft-ollama -p 127.0.0.1:11434:11434 \
  -v weft-ollama-models:/root/.ollama docker.io/ollama/ollama
docker exec weft-ollama ollama pull qwen2.5:1.5b
WEFT_LIVE_OLLAMA_ENDPOINT=http://127.0.0.1:11434/api/chat \
WEFT_LIVE_OLLAMA_MODEL=qwen2.5:1.5b \
  dotnet test dotnet/Weft.slnx -c Release --filter "FullyQualifiedName~LiveOllama"
```

The endpoint must be a loopback IP literal, because only loopback profiles may use
plain HTTP. CPU inference on a heavily loaded machine can exceed the header wait;
a smaller model or an idle machine avoids that.

## Orchestration and context

1. Authenticate the user, authorize conversation/revision access, and resolve
   an allowed model profile.
2. Build bounded context from the selected viewer projection and the user's
   selected fields/rules. Do not send the complete native artifact by default.
3. Send application instructions, recent conversation messages, context, and
   only the tools allowed for this user/turn to the provider adapter.
4. Validate every tool call against a typed schema and recheck project/revision
   permissions before executing it. Feed a bounded result back into the turn.
5. Validate cited resource IDs against the authorized pinned revision, then
   stream the answer and completion state to the browser.
6. Persist permitted conversation content and usage under its own retention
   policy, and record bounded audit metadata.

Provider requests can include only context permitted by the model profile's
data policy. A mapping can contain confidential field names, literals, URLs, or
descriptions even when it has no production rows. Profile policy therefore
covers mapping metadata as well as sample/production payloads. A disallowed
external profile must fail before making a provider request.

Start with deterministic retrieval through field/rule identifiers and text
search. Add embeddings/vector storage only if measured retrieval needs justify
them. Mapping text, user-uploaded documents, tool results, and chat content are
untrusted data; they cannot replace application instructions or grant tools.

First-version read tools:

| Tool | Bounded result |
| --- | --- |
| `get_mapping_summary` | Boundary summary, controls, capabilities, coverage |
| `search_mapping` | Relevant fields/rules with stable locators |
| `get_field_lineage` | Input dependencies and downstream affected outputs |
| `get_rule_details` | Native/projected rule structure, conditions, repetition |
| `get_validation_issues` | Issues for the pinned revision with locations |

Every tool is scoped to the already authorized conversation/revision. Cross-
project search, raw filesystem/network access, shell/SQL execution, and payload
retrieval are absent from this tool registry. Organization-wide questions use a
separate workspace conversation with its own closed read-only manifest; see
[Workspace chat](workspace-chat.md). Revision comparison becomes
a tool only after the comparison API is independently qualified. The registry,
read port and citation check are described in
[Mapping explanation tools](mapping-ai-tools.md).

Citations identify pinned-report resources that a read-only tool retrieved in the
turn, verified under fresh authority. They do not prove which resources the answer
text relies on, and they do not guarantee the model's interpretation is correct. Keep original rules inspectable. Never label
an explanation as execution evidence unless it is linked to an actual authorized
run with the matching revision.

## Actions and payloads

V1 chat explains mappings; users run previews through the ordinary preview UI.
Later AI action tools may propose parameters or mapping edits, but execution
requires the corresponding typed application action and permission. Proposed
edits must identify their base revision and show a concrete diff before creating
another revision; deployment remains a separate approved workflow.

Production input/output bytes are not model context by default. Reading them
requires the separate payload permission and a model profile that permits their
destination. Workspace chat applies this to sample data; see
[Workspace chat](workspace-chat.md#payload-data). Persisted chat text can itself include sensitive user-provided
content, so conversations have explicit ownership, retention, deletion, and
sharing policy. V1 conversations are private to their creator within a project;
administration privileges do not silently expose their message content.

## Streaming, cancellation, and limits

The API creates a turn ID before initiating provider work. Persist turn state
and sequence-numbered bounded events so reconnects can detect completion or
failure without issuing a second completion. Use an authenticated fetch stream
with SSE framing; keep vendor event names and raw response bodies private.
Do not send credentials in URLs. Creating and cancelling turns use CSRF-protected
state-changing requests.

Bound input characters/attachments, retrieved nodes, tool-result bytes, tool
iterations, total turn time, output budget, and concurrent turns. A disconnect
does not authorize an unbounded model loop. Explicit cancellation closes the
provider stream and marks the turn terminal. A restarted API instance recovers
or marks an abandoned turn interrupted; it does not blindly reissue a billable
request. Retries must respect provider idempotency and whether output began.

Token usage and pricing vary by provider. Keep usage optional and preserve its
provenance; character limits and maximum tool rounds remain enforceable even
when a provider does not return token accounting. Default logs contain turn IDs,
model profile identity, timing, and bounded failures, not prompts or responses.

## Qualification gate

- Two independent provider protocol fixtures pass the same request/event/tool
  contract; no vendor SDK types appear in the API or browser contract.
- One configured real provider passes opt-in integration checks before chat is
  advertised as connected. Secrets and paid calls are not required in ordinary CI.
- A hosted and a local profile can be selected through configuration without
  changing the UI/tool protocol; only qualified capabilities are advertised.
- Answers cite actual authorized fields/rules from the pinned revision.
- Unauthorized project access, prompt-injected tool requests, excess budgets,
  invalid citation IDs, malformed streams, rate limits, reconnects, timeouts, and
  cancellation produce bounded, explicit outcomes.
- Read-only tools cannot mutate revisions, access payloads, or initiate runs.
- Disallowed destinations make no provider request, and credentials/payloads do
  not appear in default logs.
- Missing model configuration leaves the viewer and other non-AI features usable.
