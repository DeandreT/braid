# Mapping endpoint

The optional [service compiled runtime](compiled-service-runtime.md) removes the
Docker socket requirement without changing the invocation or release contract.

`Weft.MachineEndpoint` (`weft-mapping-endpoint`) is the machine-facing HTTP host for one
registered release target. Machine callers send mapping inputs and receive the outputs
of the target's current release. It is separate from the Session host: it serves no
browser session, cookie, static file or redirect, and has no default authentication
scheme.

Each call authenticates the caller with a bearer token, requires a current invocation
grant, claims the request in the invocation ledger and resolves the current release
again. See [Machine authorization](machine-authorization.md) for tokens, grants and the
ledger, and [Local release activation](local-releases.md) for the release pointer.

## HTTP contract

| Route | Purpose |
| --- | --- |
| `POST /v1/invocations` | Invoke the target's current release. |
| `GET /v1/health/live` | `204` while the process serves requests. |
| `GET /v1/health/ready` | `204` once started; `503` before start, while stopping and once the compiled runtime has closed. |

An invocation needs these request headers:

- `Authorization: Bearer <access token>`, validated as described in
  [Machine authorization](machine-authorization.md#token-validation).
- `Weft-Request-Id`: a caller-chosen canonical lowercase UUID. Retrying with the same ID
  and body returns the original outcome.
- `Content-Type: application/json`, with no content encoding.

The body is at most 2 MiB:

```json
{
  "inputs": [
    { "content": "<base64 primary document>" },
    { "name": "rates", "content": "<base64 named document>" }
  ],
  "parameters": [ { "name": "region", "kind": "string", "value": "west" } ]
}
```

The first input is the unnamed primary document; every other input names a declared
source, and the compiled artifact requires all of them. Input content is base64 so the
exact bytes are used and digested. Parameter kinds are `string`, `integer`, `float` and
`boolean`, with exact lexical values. Inputs total at most 1 MiB and outputs at most
1 MiB; the other preview bounds on counts, names and parameters apply. Every call runs
all targets of the qualified artifact with the server's current UTC clock.

A completed call returns `200` with `Weft-Request-Id`, `Weft-Invocation-Replayed` and:

```json
{
  "schema": "weft.mapping-invocation-response.v1",
  "requestId": "…", "releaseId": "…", "buildId": "…", "revisionId": "…",
  "artifactDigest": "…", "currentDateTime": "2026-10-22T12:00:00.0000000Z",
  "outputs": [ { "name": null, "sha256": "…", "content": "<base64>" } ]
}
```

Other answers carry `{"requestId": "…", "outcome": "…"}`:

| Status | Outcome | Meaning |
| --- | --- | --- |
| `400` | `invalidRequestId`, `invalidRequest` | Malformed request ID or body. Nothing is recorded. |
| `401` | `unauthorized` | Missing, refused or expired token, with `WWW-Authenticate: Bearer`. |
| `403` | `refused` | No active grant, a disabled target, or a release the publisher can no longer run. |
| `409` | `conflict` | A different body or client application under this request ID. |
| `409` | `inProgress` | An attempt for this ID is running; `Retry-After` applies. |
| `410` | `expired` | The call completed, but its retained response has expired. |
| `413`, `415` | | Body too large, or not JSON. |
| `422` | `invalidInput`, `invalidParameter`, `executionFailed`, `resourceLimit`, `unsupported` | A recorded mapping outcome; the same request returns it again. |
| `500` | `integrityFailure`, `attemptsExhausted` | A recorded failure; use a new request ID. |
| `503` | `busy`, `unavailable`, `releaseChanged`, `deadline`, `noRelease` | Nothing was recorded as final; retry the same request after `Retry-After`. |

## Execution

The endpoint admits at most `WEFT_ENDPOINT_CONCURRENCY` calls at once (1 to 4, the
compiled runtime's limit), without a waiting queue. A saturated or stopping endpoint
answers `503` with `Retry-After` before validating the token or reading the body. Each
call has a 90-second deadline that covers validation, the body, the ledger, one
execution and the response. Work stops after 85 seconds, which leaves time for the
answer. Successful response transfer is also bounded by credential expiry; expiry
during delivery aborts the connection, while the original recorded result remains
recoverable with the same request ID and a valid credential. A client that has not
taken the response by the deadline loses the connection,
and its slot is admitted again. The deadline is shorter than the ledger's 120-second
attempt lease. A finish that reaches the ledger after the lease has expired records
nothing; the call answers `503`, and the same request can be retried.

The bearer token also bounds the call. When it expires, the endpoint stops reading the
body or running the call, releases any claimed attempt and answers `401`. It claims
nothing on an expired token, and it does not deliver an outcome recorded after the
token expired. A retry under the same request ID with a valid token replays a recorded
outcome, or runs a released attempt again.

A claimed call runs as the current release's publisher through the existing
`LocalReleaseInvocation`. That path checks the pointer, the publisher's current
authority, the build approval and the artifact bytes before and after execution, and only
the isolated compiled runtime loads the artifact. The endpoint reads the pointer before
and after the call. A release ID never becomes current again, so an unchanged pointer
identifies the release that ran. A rollback, new activation or disable during the call
releases the attempt instead of delivering stale output. Revoking the grant, rolling back
or disabling the target applies to the next call, including a replay. Expired retained
responses are purged every ten minutes.

## Configuration

All settings are environment values admitted at startup. An invalid or incomplete
configuration stops the process with `WeftMappingEndpointConfigurationFailed` and no
values echoed.

| Setting | Value |
| --- | --- |
| `WEFT_ENDPOINT_LISTEN_ADDR` | One literal IP endpoint with a nonzero port. |
| `WEFT_ENDPOINT_TRANSPORT` | `platformIngress` (plain HTTP behind an ingress that terminates TLS) or `directHttps`. |
| `WEFT_ENDPOINT_TLS_CERTIFICATE_FILE` / `WEFT_ENDPOINT_TLS_PRIVATE_KEY_FILE` | `directHttps` only: protected PEM files, leaf first. |
| `WEFT_ENDPOINT_ORGANIZATION_ID`, `_PROJECT_ID`, `_MAPPING_ID`, `_ENVIRONMENT_ID`, `_ENVIRONMENT_VERSION`, `_TARGET_ID` | The one registered target. |
| `WEFT_ENDPOINT_ENTRA_TENANT_ID` / `WEFT_ENDPOINT_ENTRA_AUDIENCE` | The token issuer's tenant and the endpoint app registration's client ID. |
| `WEFT_ENDPOINT_CONCURRENCY` | Optional, 1 to 4 (default 4). |
| `WEFT_COMPILED_RUNTIME_IMAGE` | The immutable `sha256:` runtime image the target registration names. |
| `WEFT_FERRULE_WORKER_IMAGE` | The immutable compiler image the target's builds name. |
| `WEFT_DOCKER_EXECUTABLE` | Optional absolute path (default `/usr/bin/docker`). |

Each restricted login arrives only as a protected private file named by `<SETTING>_FILE`
(owned by the process user, mode 0600, at most 16 KiB). The plain setting refuses startup,
as do ambient `PG*` credentials, URL bindings, startup hooks and profilers. Each file must
name exactly its login, with an explicit password:

| Setting | Login |
| --- | --- |
| `WEFT_ENDPOINT_INVOCATION_WRITER_CONNECTION_STRING` | `weft_invocation_writer` |
| `WEFT_ENDPOINT_RELEASE_READER_CONNECTION_STRING` | `weft_release_reader` |
| `WEFT_ENDPOINT_BUILD_READER_CONNECTION_STRING` | `weft_build_reader` |
| `WEFT_ENDPOINT_BUILD_RUN_READER_CONNECTION_STRING` | `weft_build_run_reader` |
| `WEFT_ENDPOINT_ACCESS_CONNECTION_STRING` | `weft_runtime` |
| `WEFT_ENDPOINT_REPORT_READER_CONNECTION_STRING` | `weft_report_reader` |
| `WEFT_ENDPOINT_RUN_READER_CONNECTION_STRING` | `weft_run_reader` |
| `WEFT_ENDPOINT_CASE_READER_CONNECTION_STRING` | `weft_case_reader` |

The process writes only the fixed lines `WeftMappingEndpointReady`,
`WeftMappingEndpointStartupFailed` and `WeftMappingEndpointShutdownFailed`. Logging
providers and IdentityModel diagnostics are off. On stop, it closes readiness, drains
in-flight calls, joins every compiled-runtime container and then closes its stores. A
nonzero exit after stop means cleanup was not confirmed.

The local runtime is the Docker compiled runtime, so the process needs Docker access.
Hosted platforms without Docker need a sandbox runtime behind the same
`ICompiledBuildRuntime` seam.
