# AI chat architecture

The AI chat window uses application-owned conversations, tool permissions and
provider adapters. Model connections are explicit server configuration.

## User experience

Keep the conversation beside the selected mapping. Suggested questions include:

- "Where does invoice.total come from?"
- "Which records are excluded, and by which condition?"
- "What changes if this source field disappears?"
- "Explain this rule in business terms."

Workspace conversations may have an explicit project, mapping or revision focus;
legacy revision conversations retain their immutable pin. The UI shows the model
profile and the focus used for each turn. Following the current mapping head and
pinning an exact revision remain distinct; old answers never silently become
explanations of a newer mapping.

Responses can link to fields, rules, and validation issues. Stream answer text
and bounded tool activity; allow stopping a turn. Missing context, unsupported
constructs, and provider failures are shown directly. Chat can be disabled by
organization policy without disabling the mapping viewer or preview.

## Model-independent boundary

braid owns a conversation protocol rather than adopting one vendor's SDK models
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
[Chat turn HTTP API](chat-http.md)). It speaks three fixed wire dialects over HTTP: Chat Completions SSE
(`ChatCompletionsSseV1`), its Azure Databricks variant (`DatabricksChatCompletionsSseV1`) and Ollama
`/api/chat` NDJSON (`OllamaChatNdjsonV1`).
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

### Azure Databricks profiles

The `databricksChatCompletionsSse` dialect sends turns through Azure Databricks
model serving in the configured workspace. The workspace route alone does not
establish data residency or the downstream model's data policy; administrators
must approve the serving endpoint's data handling before allowing mapping
metadata or sample data. The dialect
admits exactly one HTTPS endpoint, `/serving-endpoints/chat/completions` on an Azure
Databricks workspace host of the form `adb-<workspace ID>.<n>.azuredatabricks.net`
(digits only, default port), where `model` is the serving endpoint name. Other
`azuredatabricks.net` names (such as `accounts.*` or `privatelink.*`), internationalized
or trailing-dot hosts, other paths and ports, queries and credentials in the URL are
refused; only a `loopbackFixture` profile may use plain HTTP on loopback. A private
endpoint keeps the workspace host name, so it needs no other form.

The profile never substitutes another API route or provider endpoint when this
route fails.

```json
{
  "id": "databricks",
  "version": 1,
  "displayName": "Azure Databricks",
  "provider": "azure-databricks",
  "dialect": "databricksChatCompletionsSse",
  "model": "<serving endpoint name>",
  "endpoint": "https://adb-<workspace ID>.<n>.azuredatabricks.net/serving-endpoints/chat/completions",
  "credentialReference": "databricks",
  "capabilities": ["text", "functionTools"],
  "metadataPolicy": { "id": "mapping-metadata", "version": 1, "mappingMetadataAllowed": true }
}
```

In Azure the credential is the app's managed identity with the Azure Databricks
resource, `{ "reference": "databricks", "managedIdentity": { "clientId": "<identity client ID>", "resource": "azureDatabricks" } }`:
the bearer value is a Microsoft Entra token for
`2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`, cached as described in
[Chat turn HTTP API](chat-http.md). On Azure Container Apps the token comes from the
platform's identity endpoint (the default `"source": "containerApps"`); on an Azure
virtual machine, add `"source": "instanceMetadata"` to read it from the VM's Instance
Metadata Service at `169.254.169.254`. There, the VM must let only the braid server's
network reach that address, since any process that reaches it can obtain the VM's
tokens. A local setup can use a `file` credential holding
a Databricks token instead. Profile loading keeps the two apart: a credential that a
Databricks profile uses, whether a file or a managed identity, cannot also be used by
another dialect's profile, and a Databricks profile accepts no Azure AI identity.

The workspace needs:

- a chat serving endpoint that supports function calling when the profile advertises
  `functionTools`;
- the identity added to the workspace as a service principal, with **Can Query** on
  the serving endpoint;
- network access from braid to the workspace, publicly or through a private endpoint
  whose DNS zone is linked to braid's virtual network, and no IP access list that
  refuses it.

The request carries `model`, `stream`, `max_tokens`, `messages` and `tools`, and not
`n`, `store`, `stream_options` or `max_completion_tokens`. An assistant message with
tool calls is sent without `content`, a form Databricks accepts; any text the model
streamed before those calls is therefore not sent back in later rounds.

Streams are `text/event-stream` and end with `data: [DONE]`. Deltas may repeat `role`,
including beside `tool_calls`, and reasoning models send `reasoning_content` before
their answer; that reasoning is never shown or kept as answer text. `usage` is
cumulative and may be on every chunk, partial (null counts) until the final chunk,
or absent. The round keeps the last complete usage, with input, output and total
counts all present, and reports it once with its terminal; a partial report never
replaces known counts. A round that fails keeps the usage already received, so
tokens billed for an aborted round are still recorded; usage is unknown, never
zero, only when no complete report arrived. Error responses (`application/json`
with an `error_code` and `message`, also for a rejected streaming request) fail the
round by HTTP status, a success that is not an event stream fails it as
`unsupportedResponse`, and an error object inside a stream fails it as
`providerUnavailable`; their text is never shown.

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
