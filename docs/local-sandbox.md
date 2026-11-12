# Local deployment sandbox

The local sandbox is a simulation for trying deployments and mappings on a local or
demo instance. It deploys a completed build to a sandbox release target, sends sample
messages to the active release and keeps their results, all on the one machine. It is
available only to a Development host; a Session host refuses every sandbox setting at
startup, so no hosted configuration can turn it on.

Nothing here bypasses the release contract. A deployment runs the same
[qualification](release-qualification.md), [target registration](local-releases.md) and
[release operations](release-operations.md) as the operator tools, with their authority
and audit checks. A message runs through the same [machine invocation](machine-authorization.md)
ledger and `LocalReleaseInvocation` path as the [mapping endpoint](mapping-endpoint.md),
in the isolated Docker compiled runtime.

The server and worker need Linux Docker for the real confinement boundary. Native
Windows supports frontend development and portable checks, not the Linux worker
sandbox. Give a scratch instance separate ports, state and database settings as
described in [local development](development.md#run-a-complete-local-instance).

## Using it

`deploy/local/dev-up.sh` enables the sandbox. Open a mapping, choose **Local sandbox**
from its actions menu, and:

1. **Deploy a build.** Pick one of your completed builds and choose **Deploy to sandbox**.
   The server qualifies it against its saved regression evidence, registers the mapping's
   sandbox target for that environment version (or reuses it), grants the sandbox caller
   and activates the release through the operations flow. Deploying needs the project
   Administrator role, as target registration and grants do.
2. **Send a sample.** In the inbox, send one of the mapping's saved regression cases, or
   paste or upload an input (with optional named inputs). The message runs against the
   target's active release. Its source, status, outcome, duration, release and build
appear in the outbox.
3. **Replay and compare.** **Replay** sends an earlier message's inputs to the current
   active release under a new message ID. Select two messages and choose **Compare
   selected outputs** to see whether each output matches, for example before and after
   a rollback.
4. **Roll back, disable or enable.** These controls call the release operations API with
   the target's current version, exactly as the Operations page does. The Operations page
   shows the full release history and audit.

A deployment or message keeps its original ID until its result arrives. If the result is
uncertain, **Retry original request** repeats it with the same ID, so the server replays
the recorded result instead of acting twice.

## Identities

The sandbox derives its identities deterministically, so a restart or retry reuses them:

- one target per mapping, environment version, build profile and compiled runtime image;
- one machine caller per organization, an `entra` principal in a tenant that exists only
  for the sandbox, with one invocation grant per sandbox target;
- the qualification and release of a deployment from its deployment ID.

There is no bearer token. The server itself is the caller, so token validation is the
one mapping endpoint step the sandbox does not run; the grant, ledger, release pointer,
publisher authority and build approval checks all apply. Revoking the sandbox grant or
disabling the target stops sends as it stops endpoint calls.

## History and retention

Each message's inputs, parameters, outputs, status, stable error outcome and timing are written to
`WEFT_LOCAL_SANDBOX_HISTORY_DIRECTORY`, one JSON file per message under its target, in a
directory only the server user can read. At most 100 messages are kept per target; the
oldest are removed first. A message that ended as `unavailable`, `inProgress`, `refused`,
`noRelease` or `conflict` recorded nothing final and is replaced when retried. The
invocation ledger keeps its own audit and 24-hour retained responses as for any endpoint
call; the sandbox purges expired responses after each send. `dev-down.sh --purge` removes
the history with the rest of the instance state.

The page shows the newest 20 messages. The HTTP history route accepts up to 50
per request; an exact message read retrieves any still-retained message by ID.
History directories belong to one running instance and must not be shared by servers.

## Configuration

| Setting | Value |
| --- | --- |
| `WEFT_LOCAL_SANDBOX_ENABLED` | `true` to enable. Requires a Development host with builds and release operations enabled. |
| `WEFT_LOCAL_SANDBOX_HISTORY_DIRECTORY` | Absolute path of the history directory. |
| `WEFT_LOCAL_SANDBOX_RUNTIME_IMAGE` | `sha256:` image ID of the Docker compiled runtime, the `engines/build-worker` image. |
| `WEFT_LOCAL_SANDBOX_DOCKER_EXECUTABLE` | Optional absolute path of the Docker CLI (default `/usr/bin/docker`). |
| `WEFT_LOCAL_SANDBOX_QUALIFICATION_WRITER_CONNECTION_STRING` | `weft_qualification_writer` |
| `WEFT_LOCAL_SANDBOX_BUILD_READER_CONNECTION_STRING` | `weft_build_reader` |
| `WEFT_LOCAL_SANDBOX_INVOCATION_WRITER_CONNECTION_STRING` | `weft_invocation_writer` |
| `WEFT_LOCAL_SANDBOX_INVOCATION_GRANT_WRITER_CONNECTION_STRING` | `weft_invocation_grant_writer` |

Each login must name exactly its role with an explicit password. The sandbox also uses
the build, regression and release operations logins those features already configure.
Setting any of these without `WEFT_LOCAL_SANDBOX_ENABLED=true` refuses startup.
The sandbox's Docker runner also uses the server's
[Docker CLI budgets](development.md#busy-docker-hosts), and the server drains it on stop.

## HTTP API

The routes are below
`/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox`
and answer `404` when the sandbox is not enabled. They need the Publisher or
Administrator role; deploying needs Administrator.

| Route | Purpose |
| --- | --- |
| `GET /sandbox` | Sandbox targets, your completed builds and the sample sources. |
| `POST /sandbox/deployments/{deploymentId}` | Deploy `{"buildId": "…"}`; the outcome is `activated`, `replayed`, `notQualified`, `unavailable`, `targetDisabled`, `stale` or `conflict`. |
| `GET /sandbox/{targetId}/messages?limit=20` | The newest messages, without document contents (limit 1–50). |
| `GET /sandbox/{targetId}/messages/{messageId}` | One message with its inputs and outputs (Base64). |
| `POST /sandbox/{targetId}/messages/{messageId}` | Send a message. The body names one source: `{"source": "case", "reference": "CASE/VERSION"}`, `{"source": "inline", "inputs": [{"name": null, "content": "…"}], "parameters": []}` or `{"source": "replay", "replayOf": "MESSAGE"}`. |

The `weft` CLI does not have sandbox commands: it signs in with Microsoft Entra and talks
to Session hosts, which never run the sandbox.

## Extending it

Sample sources and delivery sinks are small interfaces in `Weft.Deployment.Sandbox`.
`ISandboxSampleSource` lists and resolves samples of one kind; saved regression cases
are the first source. `ISandboxSink` receives every finished message; the file history is
the first sink. Another source or sink plugs in at host composition without changing the
deployment or invocation path.
