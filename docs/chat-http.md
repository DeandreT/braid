# Chat turn HTTP API

Private, revision-pinned chat is exposed under the existing catalog identity
boundary. Development and session hosts use the same routes; the catalog request
boundary admits the caller, and every operation then reads fresh project/revision
authority before touching the chat ledger. The ledger independently rechecks the
creator's active principal, organization and project membership. Conversations
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
| `WEFT_CHAT_READER_CONNECTION_STRING` / `WEFT_CHAT_WRITER_CONNECTION_STRING` | Distinct `weft_chat_reader` and `weft_chat_writer` logins (see [chat security](private-chat-security-and-upgrade.md)). Pools open lazily. The Session host reads only the `_FILE` forms (`WEFT_CHAT_READER_CONNECTION_STRING_FILE` / `WEFT_CHAT_WRITER_CONNECTION_STRING_FILE`) through its [protected connection delivery](session-host.md); a plain connection-string setting refuses Session startup. |

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
  `/openai/v1/chat/completions` for [Azure OpenAI](ai-chat.md#azure-openai-profiles)) or
  `ollamaChatNdjson` (`/api/chat`). Endpoints are fixed HTTPS URLs; a plain HTTP
  loopback endpoint requires `"loopbackFixture": true`. `requestThinkFalse` is
  allowed only for the Ollama dialect. Up to 16 profiles with distinct IDs.
- `credentialReference` is `null` for unauthenticated local endpoints, otherwise
  an identifier naming one `credentials` entry. Every bearer reference must
  resolve and every listed credential must be used. The credential file holds
  only the bearer value (one optional trailing LF) and is read with the same
  private-file admission at dispatch time, so rotation needs no restart. An
  unreadable credential fails that turn as `credentialUnavailable`.
- A credential can instead name a managed identity,
  `{ "reference": "azure-openai", "managedIdentity": { "clientId": "<uuid>" } }`
  (an empty object selects the system-assigned identity). Its bearer value is a
  Microsoft Entra access token for Azure OpenAI (`https://ai.azure.com/.default`)
  from the Azure Container Apps managed-identity endpoint, which the host admits
  at startup only as plain HTTP on loopback (`IDENTITY_ENDPOINT`, with
  `IDENTITY_HEADER`). The token is cached in memory until five minutes before it
  expires, for at most an hour, with one refresh at a time per identity. A failed
  refresh keeps the current token until a minute before it expires; after that the
  turn fails as `credentialUnavailable`.
- A profile whose metadata policy disallows mapping metadata is never offered.
  Changing any nonsecret profile field under an existing ID/version changes its
  pinned fingerprint; bump the version when changing a profile.

The browser selects a profile by ID and version; it never supplies an endpoint.
Each turn records the selected profile version and model.

## Routes

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
