# Chat turn HTTP API

Private revision-pinned and organization-wide workspace chat are exposed under the existing catalog identity
boundary. Development and session hosts use the same routes; the catalog request
boundary admits the caller. Revision operations read fresh project/revision
authority before touching the ledger; workspace operations require current
organization admission and re-authorize each tool's resources independently.
The ledger independently rechecks the creator's active principal and organization
membership, plus project membership for revision conversations. Conversations
are private to their creator; project administrators cannot read them.

## Configuration

Chat is off unless `WEFT_CHAT_ENABLED=true`. When it is off, chat routes answer
`404 {"code":"unavailable"}`, the catalog context reports `features.chat=false`,
and no profile file, credential, pool or provider client is opened. Enabling chat
requires the catalog and revision report reads, because conversations pin the
revision's verified reports; an inconsistent profile fails host startup.

| Setting | Value |
| --- | --- |
| `WEFT_CHAT_ENABLED` | `true` or `false` (case-sensitive). |
| `WEFT_CHAT_PROFILES_FILE` | Canonical absolute path of a private profile document (owned by the effective user, mode 0600, at most 64 KiB, protected ancestors). |
| `WEFT_CHAT_READER_CONNECTION_STRING` / `WEFT_CHAT_WRITER_CONNECTION_STRING` / `WEFT_CHAT_APPROVER_CONNECTION_STRING` | Required, independently credentialed `weft_chat_reader`, `weft_chat_writer`, and `weft_chat_approver` logins to the same database. Pools open lazily. The Session host reads only the corresponding `_FILE` forms through its [protected connection delivery](session-host.md); a plain connection-string setting refuses Session startup. Missing approver configuration has no writer fallback. See [exact approval composition](chat-edit-approval-composition.md). |
| `WEFT_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING` | Optional. The restricted `weft_payload_permission_reader` login (see [payload permission](project-payload-permissions.md)), distinct from both chat logins. Without it no workspace payload tool exists. The Session host reads only `WEFT_CHAT_PAYLOAD_PERMISSION_CONNECTION_STRING_FILE`. |

The Compose feature profile and Azure chat template deliver that existing
payload-permission reader through an owner-only file. Delivering the reader does
not grant any user payload access, and model profiles still default to refusing
payload data.

The profile document is strict JSON; unknown or duplicate properties are refused:

```json
{
  "version": 1,
  "retentionDays": 30,
  "turnSeconds": 120,
  "maxOutputTokens": 1024,
  "profiles": [
    {
      "id": "hosted-default",
      "version": 1,
      "displayName": "Hosted model",
      "provider": "example",
      "dialect": "chatCompletionsSse",
      "model": "model-name",
      "endpoint": "https://models.example.net/v1/chat/completions",
      "credentialReference": "hosted-key",
      "capabilities": ["text", "functionTools"],
      "metadataPolicy": { "id": "mapping-metadata", "version": 1, "mappingMetadataAllowed": true }
    }
  ],
  "credentials": [{ "reference": "hosted-key", "file": "/etc/weft/private/hosted-key" }]
}
```

- `retentionDays` (1–3650) is required: it sets each new conversation's
  retention deadline. There is no built-in default. `turnSeconds` (10–180,
  default 120) bounds a turn; `maxOutputTokens` (1–2048, default 1024) bounds
  each provider round.
- `dialect` is `chatCompletionsSse` (`/v1/chat/completions`, or
  `/openai/v1/chat/completions` for [Azure OpenAI](ai-chat.md#azure-openai-profiles)),
  `databricksChatCompletionsSse` (`/serving-endpoints/chat/completions` on an
  `adb-<workspace ID>.<n>.azuredatabricks.net` workspace; see
  [Azure Databricks](ai-chat.md#azure-databricks-profiles)) or
  `ollamaChatNdjson` (`/api/chat`). Endpoints are fixed HTTPS URLs; a plain HTTP
  loopback endpoint requires `"loopbackFixture": true`. `requestThinkFalse` is
  allowed only for the Ollama dialect. Up to 16 profiles with distinct IDs.
- `metadataPolicy.payloadsAllowed` (optional, default `false`) records that the
  destination may also receive sample payload data in
  [workspace chat](workspace-chat.md#payload-data). It requires
  `mappingMetadataAllowed`; set it only when the provider's data handling
  permits sample data.
- `credentialReference` is `null` for unauthenticated local endpoints, otherwise
  an identifier naming one `credentials` entry. Every bearer reference must
  resolve and every listed credential must be used. The credential file holds
  only the bearer value (one optional trailing LF) and is read with the same
  private-file admission at dispatch time, so rotation needs no restart. An
  unreadable credential fails that turn as `credentialUnavailable`. A credential
  used by a `databricksChatCompletionsSse` profile cannot also be used by a profile
  of another dialect.
- A credential can instead name a managed identity,
  `{ "reference": "azure-openai", "managedIdentity": { "clientId": "<uuid>" } }`
  (without `clientId`, the system-assigned identity). Its bearer value is a
  Microsoft Entra access token for the identity's `resource`: `azureAi` (the
  default, Azure OpenAI, `https://ai.azure.com/.default`) or `azureDatabricks`
  (Azure Databricks, `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default`). An
  `azureDatabricks` identity serves only `databricksChatCompletionsSse` profiles,
  and those profiles accept no other managed identity.
- The identity's `source` names the endpoint that issues its tokens; Weft never
  detects or falls back between them:
  - `containerApps` (the default): the Azure Container Apps managed-identity
    endpoint, which the host admits at startup only as plain HTTP on loopback
    (`IDENTITY_ENDPOINT`, with `IDENTITY_HEADER`);
  - `instanceMetadata`: an Azure virtual machine's Instance Metadata Service at the
    fixed address `http://169.254.169.254/metadata/identity/oauth2/token`
    (`api-version=2018-02-01`, header `Metadata: true`, `client_id` only for a
    user-assigned identity), without a proxy or redirects. A 404, 410, 429, 5xx
    or timeout is retried twice, after one and then two seconds; any other
    failure, or a third failed attempt, fails that turn as `credentialUnavailable`.
    Only the server's network may reach the address on the VM: other containers
    on the host must not be able to obtain the VM's tokens.

  The token is cached in memory until five minutes before it expires, for at most
  an hour, with one refresh at a time per identity, resource and source. A failed
  refresh keeps the current token until a minute before it expires; after that the
  turn fails as `credentialUnavailable`. Response bodies are never logged or shown.
- A profile whose metadata policy disallows mapping metadata is never offered.
  Changing any nonsecret profile field under an existing ID/version changes its
  pinned fingerprint; bump the version when changing a profile.

The browser selects a profile by ID and version; it never supplies an endpoint.
Each turn records the selected profile version and model.

## Workspace routes

Workspace routes are relative to `/api/v1/organizations/{organizationId}/chat`.
They share revision chat's request admission, creator privacy, idempotency,
retention, streaming, cancellation and interrupted-turn recovery rules.

| Method and path | Result |
| --- | --- |
| `GET` | Workspace-capable profiles, their `payloadsAllowed` destination policy, and turn limits. |
| `GET /conversations?limit=1..100&after=<conversationId>&revisionId=<revisionId>` | Both conversation kinds, newest first, with `hasMore`. Default limit 50; continue after the final identity from the preceding page. The optional revision filter requires fresh read access. |
| `POST /conversations` | `{"conversationId":"<uuid>","focus":null}` or focus `{ "projectId":"<uuid>","mappingId":"<uuid>","revisionId":null }`. An exact revision can replace the null revision. Creates (201) or replays (200); a different focus for the same identity conflicts. No reports are derived or required. |
| `GET /conversations/{conversationId}` | Workspace conversation and its most recent 100 turns, oldest first. |
| `POST /conversations/{conversationId}/turns` | The same turn body as revision chat. A mapping focus resolves its head once when the turn starts; `focus` in the returned turn records that exact revision or its unavailability. |
| `GET /conversations/{conversationId}/turns/{turnId}/events?after=N` | The durable event stream, strictly after `N`, default 0. |
| `POST /conversations/{conversationId}/turns/{turnId}/cancel` | Bodyless cancellation, unchanged for a terminal turn. |

Focus supplies context, not authority or a tool restriction: workspace tools can
read any resource the caller can currently read in the organization. A missing
or revoked focus does not remove access to other readable projects. Profiles
without function tools and mapping-metadata permission cannot start workspace
turns. Payload reads additionally require the destination policy and a freshly
observed project-specific payload-read grant; profile availability alone never
grants payload access. See [workspace chat](workspace-chat.md).

The organization listing also returns legacy revision-pinned conversations.
Those retain `kind: "revision"` and their pinned revision and continue through
the revision routes below; the workspace detail/turn routes do not reinterpret
them as workspace conversations.

Workspace streams add `workspace.citation` (a freshly verified resource reference)
and `data.shared` (project, source identity and byte count, never the shared
content). Shared-data records do not claim that every payload tool call was
permitted; each call is checked independently for its own project.

## Revision routes

All routes are relative to
`/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat`.
IDs are canonical lowercase UUIDs. Responses are `no-store`; errors are
`{"code": ...}` (`ChatErrorV1`). An absent, foreign or unreadable revision,
conversation or turn is `404 notFound`.

| Method and path | Result |
| --- | --- |
| `GET` | Selectable profiles, limits and whether read-only mapping tools are registered (`toolsEnabled`). |
| `GET /conversations?limit=1..100` | The caller's newest conversations for this revision (default 50) and `hasMore`. |
| `POST /conversations` | Body `{"conversationId"}`. Creates a conversation pinned to the revision's current verified projection and validation reports (201), or returns the existing one (200). Missing reports are `409 reportsUnavailable`; creation never derives reports. |
| `GET /conversations/{conversationId}` | The conversation and its most recent 100 turns, oldest first, with `hasEarlierTurns`. |
| `POST /conversations/{conversationId}/turns` | Body `{"turnId","text","profileId","profileVersion","selectors"}`. Starts a turn (201) once its start is durably acknowledged. |
| `GET /conversations/{conversationId}/turns/{turnId}/events?after=N` | Authenticated SSE stream of the turn's events after sequence `N` (default 0). |
| `POST /conversations/{conversationId}/turns/{turnId}/cancel` | Bodyless. Cancels an active turn and returns its current state; a terminal turn is returned unchanged. |

Both cancellation routes verify an empty decoded request body before reading or
changing a turn. An empty chunked request from a reverse proxy is supported; the
HTTP server must finish decoding it, including the final chunk and trailer
section. Nonempty, malformed, truncated, trailer-bearing or stalled requests are
refused. Conflicting length/transfer framing (including Kestrel's preserved
`X-Content-Length` marker), Content-Type and nonidentity content encodings are
unsupported. The empty-body check reads at most one byte and has a fixed
15-second deadline.
Rejected HTTP/1 bodies close that connection rather than admitting an appended
request. HTTP/2 and HTTP/3 keep their independent stream boundaries.

Authentication, the configured Origin and the antiforgery token are checked
before this read. Proxies must preserve HTTP framing and must not turn an unread
or rejected body into an apparent zero-length request. Framing workarounds that
strip Transfer-Encoding or discard bytes cannot establish safe cancellation.

`POST` routes are unsafe requests: they require the exact configured `Origin` and
the catalog context's `X-XSRF-TOKEN` (session mode additionally binds it to the
session epoch). A request admitted with an [Entra bearer
token](api-authentication.md) sends neither, and each `POST` is audited before it
runs; bearer refusals answer `400 invalidRequest`, `401`/`403 identityRejected`,
`429 rateLimited` with `Retry-After`, or `503 unavailable`. Bodies are strict `application/json` (optional `charset=utf-8`,
identity encoding), at most 1 KiB for conversations and 256 KiB for turns, with
unknown or duplicate properties refused. Turn text is 1–32 KiB of UTF-8, not
blank and without NUL. `selectors` (required, possibly empty) holds at most 32
distinct `{kind,resourceId,diagnosticOrdinal}` references to the pinned reports;
they record what the user was looking at and grant nothing.

### Idempotency

The client generates conversation and turn IDs. Repeating a conversation create
returns the original conversation and its original report pins. Repeating a turn
with the same text, profile and selectors returns the original turn (200) and
never dispatches again; changed input is `409 idempotencyConflict`. Only one turn
per conversation is active: another start is `409 turnActive`. A turn that could
not start is `409 turnNotStarted` (for example an exhausted conversation
retention); retry with a new turn ID. An unknown or disallowed profile is
`409 profileUnavailable`.

## Turn execution and events

The server builds the turn from trusted configuration: versioned instructions,
the registered read-only tool manifest, provider and persistence budgets, the
turn deadline and history read from the ledger (the most recent completed
exchanges that fit the orchestrator's history bounds). Nothing in the request
selects instructions, tools, endpoints or history. When chat is enabled the Server
registers one shared instance of the five read-only
[mapping explanation tools](mapping-ai-tools.md) over the host's report metadata
and report readers, as both the tool port and the citation port; tool calls are
offered only to profiles with the `functionTools` capability. A tool result's
citation candidates are verified against the conversation's pinned reports
under fresh authority and delivered as `citation` events after that tool's
`tool.completed` (see [citation delivery](chat-turn-orchestration.md#citation-delivery)).
Model text never produces a citation. Execution belongs to the server
process, not to the request: disconnecting neither cancels nor repeats provider
work, and a stopping server cancels its live turns.

The stream uses SSE framing over an authenticated `fetch` (`Accept:
text/event-stream`). Each event is one frame: `id` is the durable sequence, `event`
is the type and `data` is one compact JSON `ChatTurnEventV1` whose `type` repeats
the event name. Comment lines (`: keep-alive`) may appear while a turn is quiet.

| Type | Data |
| --- | --- |
| `turn.started` | Sequence 1. |
| `text.delta` | Answer text in order, with its application round ID. |
| `tool.started` / `tool.completed` | Tool name and ordinal within the round; completion adds result bytes and retrieved nodes. Arguments and results are never streamed. |
| `usage` | Per-round token snapshot (`observation`, `roundTerminal` or `openRejection`). Null counts are unknown, never zero; snapshots are not additive. |
| `citation` | A tool-retrieved target verified against the conversation's pinned report, with that report's derivation ID and the tool's round ID. Each target appears at most once per turn; unverifiable candidates are never streamed. |
| `turn.completed` | Terminal. |
| `turn.failed` | Terminal. `reason` is `provider` (with `providerFailure`), `outputLimit`, `refused`, `contentFiltered`, `interrupted` or `stopped` (an application bound, authority change or persistence stop). |
| `turn.cancelled` | Terminal. |

Ledger-internal records (provider failure details and rounds without known usage)
are not streamed, so sequences can skip. To resume after a disconnect, reconnect
with `after` set to the last received `id`; the server replays strictly later
durable events and then follows the live turn. Reading never starts, resumes or
re-dispatches provider work. The stream ends after the terminal event, when
authority is revoked or the conversation expires, or shortly after the turn
deadline. A stream that ends without a terminal event can be resumed.

Cancellation of a turn running in this process closes its provider stream; the
execution then publishes `turn.cancelled`. A turn left active by an earlier process
(or past its deadline, or whose execution here ended without a terminal record) is
closed with a conditional ledger write as `turn.failed` with reason `interrupted`
when it is next streamed, or `turn.cancelled` when cancelled. Neither path sends
another provider request. The supported profile is one API instance; turns are
not taken over by another instance.

Default logs contain no prompts, answers, endpoints or credentials.

## Local model check

A complete turn can be exercised against a local Ollama model through the same
server path as a hosted profile:

1. Start the loopback Ollama service and pull a model that supports tool calls,
   as in the [live provider check](ai-chat.md#live-provider-check).
2. Configure an `ollamaChatNdjson` profile with endpoint
   `http://127.0.0.1:11434/api/chat`, `"loopbackFixture": true`,
   `"credentialReference": null` and capabilities `["text","functionTools"]`,
   with an empty `credentials` list.
3. Run the Server with Development identity, catalog reads, project creation,
   revision ingestion with `WEFT_FERRULE_WORKER_IMAGE`, report reads and
   derivation, and chat with its reader and writer logins.
4. Through product HTTP or the browser, create a project, upload a native
   mapping and ensure its projection and validation reports. Then ask a question
   that needs a mapping tool (for example a `search_mapping` query) from the
   revision page's chat panel, or create a conversation and turn through the
   routes above.
5. Follow the event stream: a tool round delivers `tool.started`,
   `tool.completed` and that tool's `citation` events before the answer's
   `text.delta` events and the terminal event.

Each provider request must return response headers within 60 seconds, and the
turn must finish within `turnSeconds`. CPU inference over large tool results on
a loaded machine can exceed either limit and fail the turn with
`providerFailure` `deadlineExceeded`; a smaller model or an idle machine avoids
this. A model can describe a tool call in its answer without making one; that
text produces no tool activity or citations.
