# Runtime packaging

## Start with the Compose Session stack

Use the existing Compose Session stack with the Ferrule service as the primary
packaged deployment. It keeps one API instance behind its edge, PostgreSQL,
private objects and the session key ring separate. Ferrule runs without giving
any container the Docker socket. Add the separate runs worker only when you opt
into previews, regression runs and builds.

For Linux development, use [Development](development.md). For cloud deployment,
see [Azure](azure-deployment.md).

1. **Prepare images and private inputs.** Build the `braid` and `edge` targets below
   and the [Ferrule service image](ferrule-service.md#image). Supply the passwords,
   certificates, image identities and pins listed under
   [Hosted-shape local stack](#hosted-shape-local-stack). Keep these inputs private;
   do not print the expanded Compose configuration. The host must support the
   service's [sandbox requirements](ferrule-service.md#request-isolation).
2. **Prepare sign-in and ingress.** Configure the Entra web registration, its
   certificate and exact callback URI (see [Entra app registration](azure-deployment.md#entra-app-registration)).
   Provide HTTPS ingress to the loopback-published edge as described under
    [Edge](#edge); the Compose edge alone does not terminate public TLS.
3. **Start the service-backed stack** from the repository root after supplying
   those inputs. A fresh volume runs role initialization, migrations, private
   storage initialization and key bootstrap before the server starts.

   ```sh
   docker compose -f deploy/compose/compose.yml -f deploy/compose/compose.ferrule-service.yml up -d
   ```

4. **Provision workspace access before the first sign-in.** After database
   initialization and migrations finish successfully, use the separate database
   owner tool to provision the organization, principal, admission and
   [external identity binding](external-identity-bindings.md). These commands need
   the migrated database; sign-in does not create those facts.
5. **Choose optional capabilities.** Add `compose.features.yml` for chat, previews,
   regression runs, builds and release operations; it also adds the separate
   `runs` worker and requires the `braid-worker` image, chat profile and its own
   service-client certificate and compile-toolchain pin. Bearer access and mapping
   endpoints require their own overlays and inputs below. These overlays do not
   enable specification intake, mapping creation or every optional feature; use
   each feature's host-composition instructions.
6. **Check the result.** Use the [smoke check](#packaging-checks) for packaging and
   [hosted-shape acceptance](hosted-acceptance.md) for signed-in workflows and
   recovery. The acceptance-only issuer, relays and fixtures are not deployment
   configuration.

Stop the same stack without deleting its database, objects or key-ring volumes:

```sh
docker compose -f deploy/compose/compose.yml -f deploy/compose/compose.ferrule-service.yml down
```

If you added overlays, use the same file list when stopping it. Do not remove
retained volumes as a recovery shortcut.

## Image reference

`deploy/container/Dockerfile` builds the hosted runtime from the repository root.
Every base image is pinned by digest, and the .NET SDK is the `global.json` version
installed from its checksum-verified official archive. The file uses no BuildKit-only
syntax, so the classic Docker builder also builds it.

| Target | Contents |
| --- | --- |
| `braid` | `Braid.Server` with the built Angular application in `wwwroot`, the operator tools and one entrypoint. |
| `edge` | The same-replica reverse proxy for the `trustedProxyOneHop` host transport. |
| `braid-worker` | `Braid.Worker`, the same entrypoint and the Docker CLI, for the hosted inspection service. |
| `braid-mapping-endpoint` | The [mapping endpoint](mapping-endpoint.md) host and the release operator tool (`Braid.ReleaseTool`), with the same entrypoint and no Docker CLI. |
| `braid-mapping-endpoint-local-docker` | `braid-mapping-endpoint` plus the Docker CLI, for the local Docker compiled runtime only. |

Build only the targets your selected stack needs. The worker and endpoint targets
in this complete target list are optional:

```sh
docker build -f deploy/container/Dockerfile --target braid -t braid:local .
docker build -f deploy/container/Dockerfile --target edge -t braid-edge:local .
docker build -f deploy/container/Dockerfile --target braid-worker -t braid-worker:local .
docker build -f deploy/container/Dockerfile --target braid-mapping-endpoint -t braid-mapping-endpoint:local .
```

## Application image

The build context includes `docs/user-guide/*.md`, which `Braid.Api` embeds in the
server assembly. The API build rejects missing guide sources; the runtime does not
load guide files from disk.

The server serves the API and the Angular application from one origin; unknown
`/api` routes stay JSON 404s and other paths fall back to `index.html`. The process
runs as the non-root `app` user (UID/GID 1654) and writes only to:

| Path | Purpose |
| --- | --- |
| `/tmp` | Runtime temporary files; mount a small tmpfs when the root filesystem is read-only. |
| `/run/braid/secrets` | Private delivered files; a 0700 directory owned by UID 1654, or a tmpfs mounted with `uid=1654,gid=1654,mode=0700`. |
| `/var/lib/braid/storage` | Persistent volume holding the private storage root (for example `/var/lib/braid/storage/root`). |
| `/var/lib/braid/keys` | Persistent volume holding the session key ring (for example `/var/lib/braid/keys/ring`). |

Volumes must provide the local POSIX filesystem semantics required by
[private storage](private-storage.md) and the [session key profile](protected-server-sessions.md).
A new Docker named volume inherits the image directory's owner and 0700 mode.

Configuration comes only from environment variables and the private files described
below; the image contains no configuration or credentials. The session-mode settings
are in [Session host operations](session-host.md). The server has no listener outside
loopback, so health is checked through the edge: `GET /api/v1/health` returns
`{"status":"ok",...}`.

### Entrypoint commands

`/app/bin/braid-entrypoint COMMAND [ARGUMENTS]`; the default command is `server`.

| Command | Effect |
| --- | --- |
| `server` | Delivers private files, removes the base image's default `ASPNETCORE_HTTP_PORTS`, then runs the server. |
| `worker` | The same for `Braid.Worker`; only the `braid-worker` image contains it, and it is that image's default command. |
| `endpoint` | The same for the mapping endpoint; only the endpoint images contain it, and it is their default command. |
| `release ARGS` | Runs `Braid.ReleaseTool` (see [local release activation](local-releases.md#operator-commands)); endpoint images only. |
| `migrate` | Applies migrations with `BRAID_MIGRATION_CONNECTION_STRING` (the migrator login). |
| `database ARGS` | Runs `Braid.DatabaseTool` owner commands. A delivered `BRAID_DELIVER_OWNER_INPUT` becomes its standard input. |
| `storage ARGS`, `maintenance ARGS`, `session-keys ARGS` | Run `Braid.StorageTool`, `Braid.MaintenanceTool` and `Braid.SessionKeyTool`. |
| `initialize-storage ROOT` | First-run step: initializes a v2 storage root for the server content root `/app/server`. An existing path is left unchanged for the server's own admission. |
| `bootstrap-session-keys` | First-run step: creates the configured key directory (0700) when absent and bootstraps it when empty, using `BRAID_SESSION_ORGANIZATION_ID`, `BRAID_SESSION_APPLICATION_NAME`, `BRAID_SESSION_KEY_DIRECTORY`, the delivered session key certificate and `BRAID_SESSION_KEY_LIFETIME_DAYS` (default 90). A nonempty ring is left unchanged. |

`Braid.SessionKeyTool bootstrap-empty ORGANIZATION_ID APPLICATION_NAME KEY_DIRECTORY
CERTIFICATE_FILE PRIVATE_KEY_FILE LIFETIME_DAYS` is the explicit owner command behind
the last step. It requires an existing empty private directory and a current RSA
certificate of at least 2048 bits, creates the first certificate-protected key through
`DurableSessionKeyProfile.BootstrapEmpty`, and prints only `Bootstrapped` or a closed
failure category. The server itself only opens an existing ring; standard Data
Protection adds later keys to that ring before the current key expires.

### Private delivery

Platforms deliver secrets as environment values. The entrypoint writes each
`BRAID_DELIVER_*` value to a new 0600 file in `/run/braid/secrets`, exports the file
setting the host reads, and removes the value from the environment before starting
the process. Supplying both a delivery value and its file setting, or an unknown
`BRAID_DELIVER_*` name, refuses startup. Values are never printed; platform tooling
that can read the container definition can still read them.

| Delivery variable | Produces |
| --- | --- |
| `BRAID_DELIVER_SESSION_READER_CONNECTION_STRING` | `BRAID_SESSION_READER_CONNECTION_STRING_FILE` |
| `BRAID_DELIVER_SESSION_WRITER_CONNECTION_STRING` | `BRAID_SESSION_WRITER_CONNECTION_STRING_FILE` |
| `BRAID_DELIVER_IDENTITY_READER_CONNECTION_STRING` | `BRAID_IDENTITY_READER_CONNECTION_STRING_FILE` |
| `BRAID_DELIVER_SESSION_KEY_PEM` | `BRAID_SESSION_KEY_CERTIFICATE_FILE` and `BRAID_SESSION_KEY_PRIVATE_KEY_FILE` |
| `BRAID_DELIVER_ENTRA_CLIENT_PEM` | `BRAID_ENTRA_CLIENT_CERTIFICATE_FILE` and `BRAID_ENTRA_CLIENT_PRIVATE_KEY_FILE` |
| `BRAID_DELIVER_INSPECTION_CLIENT_PEM` | `BRAID_INSPECTION_CLIENT_CERTIFICATE_FILE` and `BRAID_INSPECTION_CLIENT_PRIVATE_KEY_FILE` (server) |
| `BRAID_DELIVER_INSPECTION_WORKER_PEM` | `BRAID_INSPECTION_SERVER_CERTIFICATE_FILE` and `BRAID_INSPECTION_SERVER_PRIVATE_KEY_FILE` (worker) |
| `BRAID_DELIVER_FERRULE_SERVICE_CLIENT_PEM` | `BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE` and `BRAID_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE` (service callers) |
| `BRAID_DELIVER_SESSION_DECRYPT_PEM_1` … `_8` | Retained decryption pairs listed in `BRAID_SESSION_DECRYPT_CERTIFICATES_FILE` |
| `BRAID_DELIVER_OWNER_INPUT` | Standard input for the `database` command |
| `BRAID_DELIVER_FILE_<NAME>` | `/run/braid/secrets/<name>` (lowercase, `_` becomes `-`), for settings that take an explicit private file path |

A PEM bundle holds exactly one private key block and one or more certificates, leaf
first; this is the format of an exported Key Vault PEM certificate secret. Other
connection strings, such as `BRAID_CATALOG_CONNECTION_STRING`, remain ordinary
environment settings read by the host.

`Braid.Worker` takes each of its database credentials either as the plain setting or as
`<NAME>_FILE`, the path of a protected private file holding the connection string;
supplying both refuses startup. This covers `BRAID_PREVIEW_WRITER_CONNECTION_STRING`, the
four `BRAID_REGRESSION_*_CONNECTION_STRING` settings of the
[regression worker](regression-runs.md) and the six `BRAID_BUILD_*_CONNECTION_STRING`
settings of the [build worker](build-runs.md). The worker admits the file as it admits its
certificate files: an owned, regular, single-link 0600 file under protected parent
directories, holding strict UTF-8 of at most 16 KiB; one final line feed is removed. It
reads a credential only when the enabled component needs it. With the entrypoint, deliver
each value as `BRAID_DELIVER_FILE_<NAME>` and point the `_FILE` setting at the resulting
file, as `compose.features.yml` does for the `runs` worker.

## Edge

The server's proxy transport accepts only a loopback backend and a loopback peer.
The `edge` image runs Caddy in the same network namespace (the same container-app
replica or compose network namespace), listens on port 8080 with HTTP/1.1 and no
access log, and forwards to `127.0.0.1:8081`. It removes every client `Forwarded`,
`X-Forwarded-*` and `X-Original-*` field, sets `Host` and `X-Forwarded-Host` to
`BRAID_PUBLIC_HOST`, sets `X-Forwarded-Proto: https`, and sets exactly one
`X-Forwarded-For`: the rightmost address appended by an ingress in
`BRAID_EDGE_TRUSTED_PROXIES` (space-separated CIDRs), otherwise the connecting peer.
The platform ingress must terminate TLS, redirect or refuse plain HTTP, and be the
only route to port 8080. Configure the server with:

```text
BRAID_HOST_TRANSPORT=trustedProxyOneHop
BRAID_LISTEN_ADDR=127.0.0.1:8081
BRAID_KNOWN_PROXY_IP=127.0.0.1
BRAID_PUBLIC_ORIGIN=https://<BRAID_PUBLIC_HOST>
```

## Hosted-shape local stack

`deploy/compose/compose.yml` runs the hosted layout locally:

- `postgres` creates the restricted roles from `deploy/local/postgres-init.sh` in a new volume.

- `migrate`, `storage-init` and `session-keys` are one-shot steps. Only `migrate`
  receives the migrator credential; the other two have no network.
- `server` runs the Session profile with catalog reads, metadata edits, project
  creation and retained report reads, each through its dedicated login, plus the
  storage and key-ring volumes. `edge` shares its network namespace, and only the
  edge port is published, on loopback. The database network is internal.
- Profile `operator` adds `maintenance`, which runs the maintenance tool against the
  shared storage volume from its own container.
- The override file `deploy/compose/compose.ferrule-service.yml` runs native upload and
  report derivation through the [Ferrule service](ferrule-service.md) instead of the
  worker override: `BRAID_FERRULE_SERVICE_REPLICAS` replicas (default two) of the service
  image on an internal network, each answering health through its own `probe` command, and
  the Session server selecting `BRAID_FERRULE_RUNNER=service`. No container receives the
  Docker socket. It requires `BRAID_FERRULE_SERVICE_IMAGE`, the service's build identity
  `BRAID_FERRULE_SERVICE_IDENTITY`, the PEM bundles `BRAID_FERRULE_SERVICE_PEM` and
  `BRAID_FERRULE_SERVICE_CLIENT_PEM`, and the pins `BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256`
  and `BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256`. Use it instead of the worker
  override, not beside it.
- The override file `deploy/compose/compose.features.yml`, used with the Ferrule service
  override, enables chat, previews, regression cases and runs, and builds on the Session
  server, each through its own login delivered as a protected private file
  (`BRAID_DELIVER_FILE_*`), and adds the `runs` service: a `Braid.Worker` that executes
  previews, regression runs and approved builds through the Ferrule service with its own client
  certificate, on the database and service networks only. Its own logins are delivered as
  private files in the same way, so no connection string stays in its process environment.
  It requires
  `BRAID_CHAT_PROFILES_JSON` (the [chat profile document](chat-http.md#configuration)),
  `BRAID_RUNS_FERRULE_SERVICE_CLIENT_PEM` and that certificate's pin
  `BRAID_RUNS_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256`, which the service admits beside
  the server's. Set `BRAID_FERRULE_COMPILE_TOOLCHAIN` to the trusted service image's
  measured toolchain identity on both hosts. The override enables compilation,
  limits each service replica to one child operation and gives it 3 GiB for the
  compiler and transport. The `identity` image command can measure and check the
  installed toolchain before the stack starts. No host receives the Docker socket.
- The override file `deploy/compose/compose.bearer.yml` turns on
  [bearer access](api-authentication.md) for signed-in users' public clients on the
  Session server, with the audience `BRAID_ENTRA_CLIENT_ID`, the scope
  `BRAID_API_BEARER_SCOPE` (default `braid.api`) and the required allowlist
  `BRAID_API_BEARER_CLIENT_IDS`. The `braid_api_audit_writer` login is delivered as a
  protected private file, like the feature logins.
- The override file `deploy/compose/compose.endpoint.yml` adds the
  [mapping endpoint](mapping-endpoint.md) for one registered target, after `migrate`, and two
  one-shots in the `operator` profile: `endpoint-grants` runs the `Braid.DatabaseTool` grant
  commands through `braid_invocation_grant_writer`, and `endpoint-release` runs
  `Braid.ReleaseTool` to qualify a completed build, register the target, and activate or roll
  back a release. Each of the endpoint's eight logins is delivered as a protected private
  file (`BRAID_DELIVER_FILE_ENDPOINT_*`, named by its `_FILE` setting). The endpoint listens
  with the `platformIngress` transport on `127.0.0.1:${BRAID_ENDPOINT_PORT}` (default 8490),
  answers health through its own `probe ready` command, and has 100 s to stop, so calls drain
  within their deadline before it joins its compiled runtime's work. It requires the target
  settings `BRAID_ENDPOINT_ORGANIZATION_ID`, `_PROJECT_ID`, `_MAPPING_ID`, `_ENVIRONMENT_ID`,
  `_ENVIRONMENT_VERSION` and `_TARGET_ID`, the callers' tenant `BRAID_ENDPOINT_ENTRA_TENANT_ID`,
  the audience `BRAID_ENDPOINT_ENTRA_AUDIENCE`, and the execution identity that the target
  registers, `BRAID_COMPILED_RUNTIME_IMAGE`. Add exactly one compiled runtime; the endpoint and
  `endpoint-release` always use the same one, so qualification executes as calls do:
  - `compose.endpoint-service.yml`, the hosted shape, with `compose.ferrule-service.yml`: both
    execute through the service's execute profile with their own client certificate
    `BRAID_ENDPOINT_FERRULE_SERVICE_CLIENT_PEM`. The service enables execution and admits the pins
    in `BRAID_ENDPOINT_SERVICE_CLIENT_PINS`, which must include the server's, any worker's and the
    endpoint's. They pin the service by `BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256` and name the
    compiler of their builds by `BRAID_FERRULE_SERVICE_IDENTITY` and
    `BRAID_FERRULE_COMPILE_TOOLCHAIN`. `BRAID_COMPILED_RUNTIME_IMAGE` is the identity that the
    service image's `runtime-identity` command reports. No container receives the Docker socket.
  - `compose.endpoint-docker.yml`, local only: both run compiled artifacts through the Docker
    daemon, so both receive the Docker socket, which is equivalent to host root access.
    `BRAID_COMPILED_RUNTIME_IMAGE` is the image ID of an `engines/build-worker` build, and it
    requires `BRAID_ENDPOINT_COMPILER_IDENTITY`, the compiler identity that the target's builds
    name, and `BRAID_DOCKER_GID`.

  `postgres-init.sh` creates every login the endpoint and its tools use.

### Advanced Docker execution alternatives

`deploy/compose/compose.worker.yml` adds the hosted inspection worker instead of
the Ferrule service override. It enables native upload and report derivation on
the server, with the ingestion and derivation logins. Only `worker` mounts the
Docker socket, which is equivalent to host root access; it has no database,
storage or egress network and no published port. The server reaches
`https://worker:8443` over the internal `inspection` network, each side pinning
the other's certificate. It is not a substitute for the service-backed runs
worker. The endpoint's separate local compiled-Docker option is
`compose.endpoint-docker.yml`, described above; do not combine it with
`compose.endpoint-service.yml`.

All runtime containers use a read-only root filesystem, drop every capability and
set `no-new-privileges`. The compose file reads every value from the caller's
environment: the `BRAID_LOCAL_*_PASSWORD` values required by `postgres-init.sh` plus
`BRAID_LOCAL_POSTGRES_ADMIN_PASSWORD`, `BRAID_SESSION_ORGANIZATION_ID`,
`BRAID_SESSION_KEY_PEM`, `BRAID_ENTRA_TENANT_ID`, `BRAID_ENTRA_CLIENT_ID`,
`BRAID_ENTRA_CLIENT_PEM` and `BRAID_PUBLIC_HOST`. The worker override also requires
`BRAID_FERRULE_WORKER_IMAGE`, `BRAID_DOCKER_GID`, `BRAID_INSPECTION_WORKER_PEM` and
`BRAID_INSPECTION_CLIENT_PEM` (separate
PEM bundles) and each certificate's pin, `BRAID_INSPECTION_WORKER_CERTIFICATE_SHA256` and
`BRAID_INSPECTION_CLIENT_CERTIFICATE_SHA256`: the lowercase SHA-256 of the DER
certificate, for example `openssl x509 -in worker.crt -outform DER | openssl dgst -sha256 -r`.
Image names default to `braid:local`, `braid-edge:local`, `braid-worker:local` and
`braid-mapping-endpoint:local` (`BRAID_ENDPOINT_IMAGE`; the Docker runtime override uses
`braid-mapping-endpoint-local-docker:local`). `BRAID_COMPOSE_PORT` chooses the edge's
loopback port (`0` selects a free one).

### Packaging checks

The smoke test builds uniquely tagged images, generates throwaway passwords and
self-signed certificates in a private temporary directory, starts the stack from new
volumes, and removes every container, network, volume and image tag it created. A
failed cleanup step or a remaining owned object fails the run:

```sh
deploy/compose/smoke-test.sh
```

It checks that the base stack renders without the worker settings, then health,
HSTS, the Angular application and its assets from the same origin,
the Session status endpoint, anonymous catalog refusal, rejection of unknown API
routes, replacement of client forwarding fields, completion of the one-shot
steps, maintenance access to the shared storage root, and the same checks after the
replica restarts with its retained volumes. With `BRAID_FERRULE_WORKER_IMAGE` set, it
also applies the worker override and requires the Session server to start with native
upload and derivation enabled, also after the restart; and, on the worker's internal
address, admission of only the pinned server certificate and one exact native validation
frame through the hosted worker. With `BRAID_FERRULE_SERVICE_IMAGE` set to a built service
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
report derivation run through the [Ferrule service](ferrule-service.md), or through the
inspection service of a separate `Braid.Worker` over the
[hosted inspection transport](ferrule-worker.md#hosted-inspection-transport). Chat,
preview, regression and build submission are composed
by the Session host with `_FILE` credential deliveries (see
[feature composition](session-host.md#feature-composition)). Previews, regression runs and
builds execute in the separate `Braid.Worker` `runs` host through the Ferrule service, which
`compose.features.yml` runs locally, and the [mapping endpoint](mapping-endpoint.md) is its
own host. The session key ring supports one API instance on a persistent local filesystem;
private objects use that filesystem or [Azure Blob storage](private-storage-blob.md). [Azure deployment](azure-deployment.md) describes the hosted environment.

## Database initialization

[`postgres-init.sh`](../deploy/local/postgres-init.sh) runs only in a new dedicated
local PostgreSQL volume. Azure's
[`provision-database.sh`](../deploy/azure/provision-database.sh) is the separate
first-time database/role provisioning job. Both create the current schema-required
role set before migrations, including draft, specification and creation roles;
absent opt-in specification/creation passwords leave unusable password logins.
The Azure provisioner refuses existing roles, while
[`initialize-secrets.sh`](../deploy/azure/initialize-secrets.sh) deliberately keeps
existing secrets.

Keep administrator provisioning, migration ownership, runtime credentials,
maintenance, key bootstrap and release/grant tools separate. Reverting an image
does not undo migrations, restore retired objects or roll back a release pointer.
Retain the original database, objects, key ring and decrypt certificates during
recovery; use the [Session host](session-host.md), [Azure operations](azure-deployment.md#operations)
and [release recovery](release-operations.md#conditional-actions-and-recovery)
procedures for the affected operation.
