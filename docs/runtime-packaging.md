# Runtime packaging

`deploy/container/Dockerfile` builds the hosted runtime from the repository root.
Every base image is pinned by digest, and the .NET SDK is the `global.json` version
installed from its checksum-verified official archive. The file uses no BuildKit-only
syntax, so the classic Docker builder also builds it. Tracking work is [#137](https://github.com/DeandreT/weft/issues/137).

| Target | Contents |
| --- | --- |
| `weft` | `Weft.Server` with the built Angular application in `wwwroot`, the operator tools and one entrypoint. |
| `edge` | The same-replica reverse proxy for the `trustedProxyOneHop` host transport. |
| `weft-local-docker` | `weft` plus the Docker CLI for the local Development worker runner only. |
| `weft-worker` | `Weft.Worker`, the same entrypoint and the Docker CLI, for the hosted inspection service. |
| `weft-mapping-endpoint` | The [mapping endpoint](mapping-endpoint.md) host and the release operator tool (`Weft.ReleaseTool`), with the same entrypoint and no Docker CLI. |
| `weft-mapping-endpoint-local-docker` | `weft-mapping-endpoint` plus the Docker CLI, for the local Docker compiled runtime only. |

```sh
docker build -f deploy/container/Dockerfile --target weft -t weft:local .
docker build -f deploy/container/Dockerfile --target edge -t weft-edge:local .
docker build -f deploy/container/Dockerfile --target weft-worker -t weft-worker:local .
docker build -f deploy/container/Dockerfile --target weft-mapping-endpoint -t weft-mapping-endpoint:local .
```

## Application image

The server serves the API and the Angular application from one origin; unknown
`/api` routes stay JSON 404s and other paths fall back to `index.html`. The process
runs as the non-root `app` user (UID/GID 1654) and writes only to:

| Path | Purpose |
| --- | --- |
| `/tmp` | Runtime temporary files; mount a small tmpfs when the root filesystem is read-only. |
| `/run/weft/secrets` | Private delivered files; a 0700 directory owned by UID 1654, or a tmpfs mounted with `uid=1654,gid=1654,mode=0700`. |
| `/var/lib/weft/storage` | Persistent volume holding the private storage root (for example `/var/lib/weft/storage/root`). |
| `/var/lib/weft/keys` | Persistent volume holding the session key ring (for example `/var/lib/weft/keys/ring`). |

Volumes must provide the local POSIX filesystem semantics required by
[private storage](private-storage.md) and the [session key profile](protected-server-sessions.md).
A new Docker named volume inherits the image directory's owner and 0700 mode.

Configuration comes only from environment variables and the private files described
below; the image contains no configuration or credentials. The session-mode settings
are in [Session host operations](session-host.md). The server has no listener outside
loopback, so health is checked through the edge: `GET /api/v1/health` returns
`{"status":"ok",...}`.

### Entrypoint commands

`/app/bin/weft-entrypoint COMMAND [ARGUMENTS]`; the default command is `server`.

| Command | Effect |
| --- | --- |
| `server` | Delivers private files, removes the base image's default `ASPNETCORE_HTTP_PORTS`, then runs the server. |
| `worker` | The same for `Weft.Worker`; only the `weft-worker` image contains it, and it is that image's default command. |
| `endpoint` | The same for the mapping endpoint; only the endpoint images contain it, and it is their default command. |
| `release ARGS` | Runs `Weft.ReleaseTool` (see [local release activation](local-releases.md#operator-commands)); endpoint images only. |
| `migrate` | Applies migrations with `WEFT_MIGRATION_CONNECTION_STRING` (the migrator login). |
| `database ARGS` | Runs `Weft.DatabaseTool` owner commands. A delivered `WEFT_DELIVER_OWNER_INPUT` becomes its standard input. |
| `storage ARGS`, `maintenance ARGS`, `session-keys ARGS` | Run `Weft.StorageTool`, `Weft.MaintenanceTool` and `Weft.SessionKeyTool`. |
| `initialize-storage ROOT` | First-run step: initializes a v2 storage root for the server content root `/app/server`. An existing path is left unchanged for the server's own admission. |
| `bootstrap-session-keys` | First-run step: creates the configured key directory (0700) when absent and bootstraps it when empty, using `WEFT_SESSION_ORGANIZATION_ID`, `WEFT_SESSION_APPLICATION_NAME`, `WEFT_SESSION_KEY_DIRECTORY`, the delivered session key certificate and `WEFT_SESSION_KEY_LIFETIME_DAYS` (default 90). A nonempty ring is left unchanged. |

`Weft.SessionKeyTool bootstrap-empty ORGANIZATION_ID APPLICATION_NAME KEY_DIRECTORY
CERTIFICATE_FILE PRIVATE_KEY_FILE LIFETIME_DAYS` is the explicit owner command behind
the last step. It requires an existing empty private directory and a current RSA
certificate of at least 2048 bits, creates the first certificate-protected key through
`DurableSessionKeyProfile.BootstrapEmpty`, and prints only `Bootstrapped` or a closed
failure category. The server itself only opens an existing ring; standard Data
Protection adds later keys to that ring before the current key expires.

### Private delivery

Platforms deliver secrets as environment values. The entrypoint writes each
`WEFT_DELIVER_*` value to a new 0600 file in `/run/weft/secrets`, exports the file
setting the host reads, and removes the value from the environment before starting
the process. Supplying both a delivery value and its file setting, or an unknown
`WEFT_DELIVER_*` name, refuses startup. Values are never printed; platform tooling
that can read the container definition can still read them.

| Delivery variable | Produces |
| --- | --- |
| `WEFT_DELIVER_SESSION_READER_CONNECTION_STRING` | `WEFT_SESSION_READER_CONNECTION_STRING_FILE` |
| `WEFT_DELIVER_SESSION_WRITER_CONNECTION_STRING` | `WEFT_SESSION_WRITER_CONNECTION_STRING_FILE` |
| `WEFT_DELIVER_IDENTITY_READER_CONNECTION_STRING` | `WEFT_IDENTITY_READER_CONNECTION_STRING_FILE` |
| `WEFT_DELIVER_SESSION_KEY_PEM` | `WEFT_SESSION_KEY_CERTIFICATE_FILE` and `WEFT_SESSION_KEY_PRIVATE_KEY_FILE` |
| `WEFT_DELIVER_ENTRA_CLIENT_PEM` | `WEFT_ENTRA_CLIENT_CERTIFICATE_FILE` and `WEFT_ENTRA_CLIENT_PRIVATE_KEY_FILE` |
| `WEFT_DELIVER_INSPECTION_CLIENT_PEM` | `WEFT_INSPECTION_CLIENT_CERTIFICATE_FILE` and `WEFT_INSPECTION_CLIENT_PRIVATE_KEY_FILE` (server) |
| `WEFT_DELIVER_INSPECTION_WORKER_PEM` | `WEFT_INSPECTION_SERVER_CERTIFICATE_FILE` and `WEFT_INSPECTION_SERVER_PRIVATE_KEY_FILE` (worker) |
| `WEFT_DELIVER_SESSION_DECRYPT_PEM_1` … `_8` | Retained decryption pairs listed in `WEFT_SESSION_DECRYPT_CERTIFICATES_FILE` |
| `WEFT_DELIVER_OWNER_INPUT` | Standard input for the `database` command |
| `WEFT_DELIVER_FILE_<NAME>` | `/run/weft/secrets/<name>` (lowercase, `_` becomes `-`), for settings that take an explicit private file path |

A PEM bundle holds exactly one private key block and one or more certificates, leaf
first; this is the format of an exported Key Vault PEM certificate secret. Other
connection strings, such as `WEFT_CATALOG_CONNECTION_STRING`, remain ordinary
environment settings read by the host.

`Weft.Worker` takes each of its database credentials either as the plain setting or as
`<NAME>_FILE`, the path of a protected private file holding the connection string;
supplying both refuses startup. This covers `WEFT_PREVIEW_WRITER_CONNECTION_STRING`, the
four `WEFT_REGRESSION_*_CONNECTION_STRING` settings of the
[regression worker](regression-runs.md) and the six `WEFT_BUILD_*_CONNECTION_STRING`
settings of the [build worker](build-runs.md). The worker admits the file as it admits its
certificate files: an owned, regular, single-link 0600 file under protected parent
directories, holding strict UTF-8 of at most 16 KiB; one final line feed is removed. It
reads a credential only when the enabled component needs it. With the entrypoint, deliver
each value as `WEFT_DELIVER_FILE_<NAME>` and point the `_FILE` setting at the resulting
file, as `compose.features.yml` does for the `runs` worker.

## Edge

The server's proxy transport accepts only a loopback backend and a loopback peer.
The `edge` image runs Caddy in the same network namespace (the same container-app
replica or compose network namespace), listens on port 8080 with HTTP/1.1 and no
access log, and forwards to `127.0.0.1:8081`. It removes every client `Forwarded`,
`X-Forwarded-*` and `X-Original-*` field, sets `Host` and `X-Forwarded-Host` to
`WEFT_PUBLIC_HOST`, sets `X-Forwarded-Proto: https`, and sets exactly one
`X-Forwarded-For`: the rightmost address appended by an ingress in
`WEFT_EDGE_TRUSTED_PROXIES` (space-separated CIDRs), otherwise the connecting peer.
The platform ingress must terminate TLS, redirect or refuse plain HTTP, and be the
only route to port 8080. Configure the server with:

```text
WEFT_HOST_TRANSPORT=trustedProxyOneHop
WEFT_LISTEN_ADDR=127.0.0.1:8081
WEFT_KNOWN_PROXY_IP=127.0.0.1
WEFT_PUBLIC_ORIGIN=https://<WEFT_PUBLIC_HOST>
```

## Hosted-shape local stack

`deploy/compose/compose.yml` runs the hosted layout locally:

- `postgres` creates the restricted roles from `deploy/local/postgres-init.sh` in a new volume.

  Existing volumes need the explicit administrator step in
  `deploy/local/postgres-add-regression-roles.sh` before the regression migrations.
- `migrate`, `storage-init` and `session-keys` are one-shot steps. Only `migrate`
  receives the migrator credential; the other two have no network.
- `server` runs the Session profile with catalog reads, metadata edits, project
  creation and retained report reads, each through its dedicated login, plus the
  storage and key-ring volumes. `edge` shares its network namespace, and only the
  edge port is published, on loopback. The database network is internal.
- Profile `operator` adds `maintenance`, which runs the maintenance tool against the
  shared storage volume from its own container.
- The override file `deploy/compose/compose.worker.yml` adds the hosted inspection
  worker and enables native upload and report derivation on the server, with the
  ingestion and derivation logins. Only `worker` mounts the Docker socket, which is
  equivalent to host root access; it has no database, storage or egress network and
  no published port. The server reaches `https://worker:8443` over the internal
  `inspection` network, each side pinning the other's certificate.
- The override file `deploy/compose/compose.examples.yml` adds a Development server
  in the Disabled profile with the Docker socket, so its worker runner can explain
  native examples. Docker socket access is equivalent to host root access; this file
  is local only and never part of the hosted shape.

- The override file `deploy/compose/compose.ferrule-service.yml` runs native upload and
  report derivation through the [Ferrule service](ferrule-service.md) instead of the
  worker override: `WEFT_FERRULE_SERVICE_REPLICAS` replicas (default two) of the service
  image on an internal network, each answering health through its own `probe` command, and
  the Session server selecting `WEFT_FERRULE_RUNNER=service`. No container receives the
  Docker socket. It requires `WEFT_FERRULE_SERVICE_IMAGE`, the service's build identity
  `WEFT_FERRULE_SERVICE_IDENTITY`, the PEM bundles `WEFT_FERRULE_SERVICE_PEM` and
  `WEFT_FERRULE_SERVICE_CLIENT_PEM`, and the pins `WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256`
  and `WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256`. Use it instead of the worker
  override, not beside it.
- The override file `deploy/compose/compose.features.yml`, used with the Ferrule service
  override, enables chat, previews, regression cases and runs, and builds on the Session
  server, each through its own login delivered as a protected private file
  (`WEFT_DELIVER_FILE_*`), and adds the `runs` service: a `Weft.Worker` that executes
  previews, regression runs and approved builds through the Ferrule service with its own client
  certificate, on the database and service networks only. Its own logins are delivered as
  private files in the same way, so no connection string stays in its process environment.
  It requires
  `WEFT_CHAT_PROFILES_JSON` (the [chat profile document](chat-http.md#configuration)),
  `WEFT_RUNS_FERRULE_SERVICE_CLIENT_PEM` and that certificate's pin
  `WEFT_RUNS_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256`, which the service admits beside
  the server's. Set `WEFT_FERRULE_COMPILE_TOOLCHAIN` to the trusted service image's
  measured toolchain identity on both hosts. The override enables compilation,
  limits each service replica to one child operation and gives it 3 GiB for the
  compiler and transport. The `identity` image command can measure and check the
  installed toolchain before the stack starts. No host receives the Docker socket.
- The override file `deploy/compose/compose.endpoint.yml` adds the
  [mapping endpoint](mapping-endpoint.md) for one registered target, after `migrate`, and two
  one-shots in the `operator` profile: `endpoint-grants` runs the `Weft.DatabaseTool` grant
  commands through `weft_invocation_grant_writer`, and `endpoint-release` runs
  `Weft.ReleaseTool` to qualify a completed build, register the target, and activate or roll
  back a release. Each of the endpoint's eight logins is delivered as a protected private
  file (`WEFT_DELIVER_FILE_ENDPOINT_*`, named by its `_FILE` setting). The endpoint listens
  with the `platformIngress` transport on `127.0.0.1:${WEFT_ENDPOINT_PORT}` (default 8490),
  answers health through its own `probe ready` command, and has 100 s to stop, so calls drain
  within their deadline before it joins its compiled runtime's work. It requires the target
  settings `WEFT_ENDPOINT_ORGANIZATION_ID`, `_PROJECT_ID`, `_MAPPING_ID`, `_ENVIRONMENT_ID`,
  `_ENVIRONMENT_VERSION` and `_TARGET_ID`, the callers' tenant `WEFT_ENDPOINT_ENTRA_TENANT_ID`,
  the audience `WEFT_ENDPOINT_ENTRA_AUDIENCE`, and the execution identity that the target
  registers, `WEFT_COMPILED_RUNTIME_IMAGE`. Add exactly one compiled runtime; the endpoint and
  `endpoint-release` always use the same one, so qualification executes as calls do:
  - `compose.endpoint-service.yml`, the hosted shape, with `compose.ferrule-service.yml`: both
    execute through the service's execute profile with their own client certificate
    `WEFT_ENDPOINT_FERRULE_SERVICE_CLIENT_PEM`. The service enables execution and admits the pins
    in `WEFT_ENDPOINT_SERVICE_CLIENT_PINS`, which must include the server's, any worker's and the
    endpoint's. They pin the service by `WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256` and name the
    compiler of their builds by `WEFT_FERRULE_SERVICE_IDENTITY` and
    `WEFT_FERRULE_COMPILE_TOOLCHAIN`. `WEFT_COMPILED_RUNTIME_IMAGE` is the identity that the
    service image's `runtime-identity` command reports. No container receives the Docker socket.
  - `compose.endpoint-docker.yml`, local only: both run compiled artifacts through the Docker
    daemon, so both receive the Docker socket, which is equivalent to host root access.
    `WEFT_COMPILED_RUNTIME_IMAGE` is the image ID of an `engines/build-worker` build, and it
    requires `WEFT_ENDPOINT_COMPILER_IDENTITY`, the compiler identity that the target's builds
    name, and `WEFT_DOCKER_GID`.

  `postgres-init.sh` creates every login the endpoint and its tools use; existing volumes need
  the explicit `postgres-add-*-roles.sh` steps for the qualification, release and invocation
  logins.

All runtime containers use a read-only root filesystem, drop every capability and
set `no-new-privileges`. The compose file reads every value from the caller's
environment: the `WEFT_LOCAL_*_PASSWORD` values required by `postgres-init.sh` plus
`WEFT_LOCAL_POSTGRES_ADMIN_PASSWORD`, `WEFT_SESSION_ORGANIZATION_ID`,
`WEFT_SESSION_KEY_PEM`, `WEFT_ENTRA_TENANT_ID`, `WEFT_ENTRA_CLIENT_ID`,
`WEFT_ENTRA_CLIENT_PEM` and `WEFT_PUBLIC_HOST`; the examples override additionally
requires `WEFT_FERRULE_WORKER_IMAGE` and `WEFT_DOCKER_GID`. The worker override requires
those two plus `WEFT_INSPECTION_WORKER_PEM` and `WEFT_INSPECTION_CLIENT_PEM` (separate
PEM bundles) and each certificate's pin, `WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256` and
`WEFT_INSPECTION_CLIENT_CERTIFICATE_SHA256`: the lowercase SHA-256 of the DER
certificate, for example `openssl x509 -in worker.crt -outform DER | openssl dgst -sha256 -r`.
Image names default to `weft:local`, `weft-edge:local`, `weft-local-docker:local`,
`weft-worker:local` and `weft-mapping-endpoint:local` (`WEFT_ENDPOINT_IMAGE`; the Docker runtime
override uses `weft-mapping-endpoint-local-docker:local`); `WEFT_COMPOSE_PORT` and `WEFT_COMPOSE_EXAMPLES_PORT` choose the
loopback ports (`0` selects a free one).

The smoke test builds uniquely tagged images, generates throwaway passwords and
self-signed certificates in a private temporary directory, starts the stack from new
volumes, and removes every container, network, volume and image tag it created. A
failed cleanup step or a remaining owned object fails the run:

```sh
deploy/compose/smoke-test.sh
```

It checks that the base stack renders without the examples settings, then health,
HSTS, the Angular application and its assets from the same origin,
the Session status endpoint, anonymous catalog refusal, absence of the Development
examples API, replacement of client forwarding fields, completion of the one-shot
steps, maintenance access to the shared storage root, and the same checks after the
replica restarts with its retained volumes. With `WEFT_FERRULE_WORKER_IMAGE` set, it
also applies the examples and worker overrides and requires native projection and
validation through the examples worker runner; the Session server to start with native
upload and derivation enabled, also after the restart; and, on the worker's internal
address, admission of only the pinned server certificate and one exact native validation
frame through the hosted worker. With `WEFT_FERRULE_SERVICE_IMAGE` set to a built service
image, it applies the Ferrule service override instead and requires the configured number
of healthy, read-only, non-root replicas on an internal network, the Session server to
start with the service selection (also after the restart), and on every replica: admission
of only the pinned server certificate, its build identity and capabilities, and one exact
native validation frame. The generated Entra values are placeholders, so
sign-in and therefore browser upload are not exercised; the
[hosted-shape acceptance](hosted-acceptance.md) runs them, with Blob storage, chat,
previews, regression runs and recovery, in the same shape.

## Hosted boundaries

The Session host does not register a Docker worker runner. Native ingestion and
report derivation run through the inspection service of a separate `Weft.Worker`
over the [hosted inspection transport](ferrule-worker.md#hosted-inspection-transport);
examples remain a Development feature. Chat
and preview submission are composed by the Session host with `_FILE` credential
deliveries (see [feature composition](session-host.md#feature-composition)); preview
execution additionally needs the separate `Weft.Worker` host, which
`compose.features.yml` runs locally.
Private storage and the session key ring support one API instance on a persistent
local filesystem. [Azure deployment](azure-deployment.md) describes the hosted environment.
