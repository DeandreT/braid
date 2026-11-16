# Runtime packaging

## Start with the Compose Session stack

Use the existing Compose Session stack with the Ferrule service as the primary
packaged evaluation recipe. It keeps one API instance behind its edge, PostgreSQL,
private objects and the session key ring separate. Ferrule runs without giving
any container the Docker socket. Add the separate runs worker only when you opt
into previews, regression runs and builds.

This is a recipe to evaluate and qualify, not a claim that a deployment is verified.
For full Linux development instead, use [Development](development.md). Azure is an
[optional, deferred cloud lane](azure-deployment.md), not a prerequisite.

1. **Prepare images and private inputs.** Build the `weft` and `edge` targets below
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
   storage initialization and key bootstrap before the server starts. For an
   existing database, first follow the [upgrade index](#database-upgrade-index).

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
   `runs` worker and requires the `weft-worker` image, chat profile and its own
   service-client certificate and compile-toolchain pin. Bearer access and mapping
   endpoints require their own overlays and inputs below. These overlays do not
   enable specification intake, mapping creation or every optional feature; use
   each feature's host-composition instructions.
6. **Check the result.** Use the [smoke check](#packaging-checks) for packaging and
   [hosted-shape acceptance](hosted-acceptance.md) for signed-in workflows and
   recovery. The acceptance-only issuer, relays and fixtures are not deployment
   configuration. Neither local check establishes live-provider or Azure acceptance.

Stop the same stack without deleting its database, objects or key-ring volumes:

```sh
docker compose -f deploy/compose/compose.yml -f deploy/compose/compose.ferrule-service.yml down
```

If you added overlays, use the same file list when stopping it. Do not remove
retained volumes as an upgrade or recovery shortcut.

## Image reference

`deploy/container/Dockerfile` builds the hosted runtime from the repository root.
Every base image is pinned by digest, and the .NET SDK is the `global.json` version
installed from its checksum-verified official archive. The file uses no BuildKit-only
syntax, so the classic Docker builder also builds it. Tracking work is [#137](https://github.com/DeandreT/weft/issues/137).

| Target | Contents |
| --- | --- |
| `weft` | `Weft.Server` with the built Angular application in `wwwroot`, the operator tools and one entrypoint. |
| `edge` | The same-replica reverse proxy for the `trustedProxyOneHop` host transport. |
| `weft-worker` | `Weft.Worker`, the same entrypoint and the Docker CLI, for the hosted inspection service. |
| `weft-mapping-endpoint` | The [mapping endpoint](mapping-endpoint.md) host and the release operator tool (`Weft.ReleaseTool`), with the same entrypoint and no Docker CLI. |
| `weft-mapping-endpoint-local-docker` | `weft-mapping-endpoint` plus the Docker CLI, for the local Docker compiled runtime only. |

Build only the targets your selected stack needs. The worker and endpoint targets
in this complete target list are optional:

```sh
docker build -f deploy/container/Dockerfile --target weft -t weft:local .
docker build -f deploy/container/Dockerfile --target edge -t weft-edge:local .
docker build -f deploy/container/Dockerfile --target weft-worker -t weft-worker:local .
docker build -f deploy/container/Dockerfile --target weft-mapping-endpoint -t weft-mapping-endpoint:local .
```

## Application image

The build context includes `docs/user-guide/*.md`, which `Weft.Api` embeds in the
server assembly. The API build rejects missing guide sources; the runtime does not
load guide files from disk.

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
| `WEFT_DELIVER_FERRULE_SERVICE_CLIENT_PEM` | `WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE` and `WEFT_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE` (service callers) |
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

  Existing volumes need the applicable administrator steps in the
  [database upgrade index](#database-upgrade-index), not another initialization run.
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
  worker override: `WEFT_FERRULE_SERVICE_REPLICAS` replicas (default two) of the service
  image on an internal network, each answering health through its own `probe` command, and
  the Session server selecting `WEFT_FERRULE_RUNNER=service`. No container receives the
  Docker socket. It requires `WEFT_FERRULE_SERVICE_IMAGE`, the service's build identity
  `WEFT_FERRULE_SERVICE_IDENTITY`, the PEM bundles `WEFT_FERRULE_SERVICE_PEM` and
  `WEFT_FERRULE_SERVICE_CLIENT_PEM`, and the pins `WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256`
  and `WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256`. Use it instead of the worker
  override, not beside it. This is the recommended evaluation route.
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
- The override file `deploy/compose/compose.bearer.yml` turns on
  [bearer access](api-authentication.md) for signed-in users' public clients on the
  Session server, with the audience `WEFT_ENTRA_CLIENT_ID`, the scope
  `WEFT_API_BEARER_SCOPE` (default `weft.api`) and the required allowlist
  `WEFT_API_BEARER_CLIENT_IDS`. The `weft_api_audit_writer` login is delivered as a
  protected private file, like the feature logins.
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
environment: the `WEFT_LOCAL_*_PASSWORD` values required by `postgres-init.sh` plus
`WEFT_LOCAL_POSTGRES_ADMIN_PASSWORD`, `WEFT_SESSION_ORGANIZATION_ID`,
`WEFT_SESSION_KEY_PEM`, `WEFT_ENTRA_TENANT_ID`, `WEFT_ENTRA_CLIENT_ID`,
`WEFT_ENTRA_CLIENT_PEM` and `WEFT_PUBLIC_HOST`. The worker override also requires
`WEFT_FERRULE_WORKER_IMAGE`, `WEFT_DOCKER_GID`, `WEFT_INSPECTION_WORKER_PEM` and
`WEFT_INSPECTION_CLIENT_PEM` (separate
PEM bundles) and each certificate's pin, `WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256` and
`WEFT_INSPECTION_CLIENT_CERTIFICATE_SHA256`: the lowercase SHA-256 of the DER
certificate, for example `openssl x509 -in worker.crt -outform DER | openssl dgst -sha256 -r`.
Image names default to `weft:local`, `weft-edge:local`, `weft-worker:local` and
`weft-mapping-endpoint:local` (`WEFT_ENDPOINT_IMAGE`; the Docker runtime override uses
`weft-mapping-endpoint-local-docker:local`). `WEFT_COMPOSE_PORT` chooses the edge's
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
replica restarts with its retained volumes. With `WEFT_FERRULE_WORKER_IMAGE` set, it
also applies the worker override and requires the Session server to start with native
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
report derivation run through the [Ferrule service](ferrule-service.md), or through the
inspection service of a separate `Weft.Worker` over the
[hosted inspection transport](ferrule-worker.md#hosted-inspection-transport). Chat,
preview, regression and build submission are composed
by the Session host with `_FILE` credential deliveries (see
[feature composition](session-host.md#feature-composition)). Previews, regression runs and
builds execute in the separate `Weft.Worker` `runs` host through the Ferrule service, which
`compose.features.yml` runs locally, and the [mapping endpoint](mapping-endpoint.md) is its
own host. The session key ring supports one API instance on a persistent local filesystem;
private objects use that filesystem or [Azure Blob storage](private-storage-blob.md). [Azure deployment](azure-deployment.md) describes the hosted environment.

## Database upgrade index

Use this order for an existing database. The source of truth is
[`migrations/postgres`](../migrations/postgres), currently `0001`–`0047`, and the
role scripts linked below. Keep every applied migration unchanged. The migration
runner requires the exact consecutive version/checksum history, refuses an
unversioned schema and applies the pending suffix in one locked transaction.
Do not run SQL files individually or replace the journal to bypass a refusal.

1. **Identify the installed prefix and missing prerequisites.** Read the existing
   migration journal and restricted-role inventory through an authorized database
   connection. Review the pending migrations and backups before the cutover.
2. **Provision only missing roles as the database administrator.** The local
   `postgres-add-*.sh` helpers read their named passwords from a private
   environment and use `POSTGRES_USER` / `POSTGRES_DB` (the database is `weft`).
   They refuse existing roles rather than adopting them or rotating passwords.
   Do not run every helper blindly: some create several roles in one transaction.
   Resolve a partially provisioned group before proceeding; do not widen grants
   or drop existing roles to make the helper pass.
3. **Coordinate the stop/migrate/start cutover.** Drain affected hosts and workers
   and confirm their original shutdown tasks settled. Apply the complete pending
   suffix with the packaged `migrate` command using only `weft_migrator`, then
   start the selected images with their own restricted credentials. Validate
   health, sign-in, retained state and enabled workflows before resuming use.

The table is an ordered prerequisite index, not a list of migrations to run by
hand. A disabled feature can still require its roles for schema installation.
Most helpers supply CONNECT-only logins; the migrations install the specific
schema/function grants. Account display is the optional exception noted below.

| Migration boundary | Existing-database administrator step |
| --- | --- |
| `0001` ownership | Requires the separately provisioned `weft_migrator` database owner and restricted `weft_runtime`; this is not a fresh-install reset. |
| `0002` ingestion | [`postgres-add-ingest.sh`](../deploy/local/postgres-add-ingest.sh) |
| `0003` catalog reads | [`postgres-add-catalog-reader.sh`](../deploy/local/postgres-add-catalog-reader.sh) |
| `0004` metadata edits | [`postgres-add-metadata-editor.sh`](../deploy/local/postgres-add-metadata-editor.sh) |
| `0005` project creation | [`postgres-add-project-creator.sh`](../deploy/local/postgres-add-project-creator.sh) |
| `0006` reports and derivation | [`postgres-add-report-roles.sh`](../deploy/local/postgres-add-report-roles.sh) |
| `0007` private-object maintenance | [`postgres-add-maintenance-role.sh`](../deploy/local/postgres-add-maintenance-role.sh) |
| `0008` external identities | [`postgres-add-identity-role.sh`](../deploy/local/postgres-add-identity-role.sh) |
| `0009` payload-read grants | [`postgres-add-payload-permission-role.sh`](../deploy/local/postgres-add-payload-permission-role.sh) |
| `0010` sessions | [`postgres-add-session-roles.sh`](../deploy/local/postgres-add-session-roles.sh) |
| `0011` chat | [`postgres-add-chat-roles.sh`](../deploy/local/postgres-add-chat-roles.sh) when none of the chat roles exist; also grants the migrator SET on `weft.organization_id` and `weft.chat_principal_id`. It now creates all four chat roles, including the later approver. |
| `0012`–`0013` previews and dispatch | [`postgres-add-preview-roles.sh`](../deploy/local/postgres-add-preview-roles.sh) |
| `0014`–`0017` regression cases, runs, dispatch and history | [`postgres-add-regression-roles.sh`](../deploy/local/postgres-add-regression-roles.sh) |
| `0018` build publication | [`postgres-add-build-roles.sh`](../deploy/local/postgres-add-build-roles.sh) |
| `0019` build dispatch | [`postgres-add-build-run-roles.sh`](../deploy/local/postgres-add-build-run-roles.sh) |
| `0020` build metadata | [`postgres-add-build-metadata-role.sh`](../deploy/local/postgres-add-build-metadata-role.sh) |
| `0021` build qualification | [`postgres-add-qualification-roles.sh`](../deploy/local/postgres-add-qualification-roles.sh) |
| `0022` local releases | [`postgres-add-release-roles.sh`](../deploy/local/postgres-add-release-roles.sh) |
| `0023` storage-kind provenance | Uses existing publication roles; no add-role helper. |
| `0024` machine invocation | [`postgres-add-invocation-roles.sh`](../deploy/local/postgres-add-invocation-roles.sh) |
| `0025` bearer-request audit | [`postgres-add-api-audit-role.sh`](../deploy/local/postgres-add-api-audit-role.sh), even with bearer access off. |
| `0026` account display | Optional [`postgres-add-account-display-role.sh`](../deploy/local/postgres-add-account-display-role.sh). May run before or after `0026`; it adds the exact column grants if the table already exists. Neither fresh initializer creates this optional role. |
| `0027` administration | [`postgres-add-administration-role.sh`](../deploy/local/postgres-add-administration-role.sh) |
| `0028` interchange provenance | Uses existing ingestion roles; no add-role helper. |
| `0029` catalog search | DBA provisions private `pg_trgm` and removes PUBLIC schema/function privileges; follow [catalog search](catalog-search.md#indexing-and-existing-reports). Do not adopt an extension from another schema. |
| `0030`–`0035` automation, release operations, workspace chat, preview reads and document builds | Reuses existing roles; no new add-role helper. Keep the real ordered migration files. |
| `0036` comparison settlement | [`postgres-add-comparison-role.sh`](../deploy/local/postgres-add-comparison-role.sh) |
| `0037`–`0038` mapping drafts and draft previews | Separately provision CONNECT-only `weft_draft_reader` and `weft_draft_writer`; see [mapping editing](mapping-editing.md#drafts-and-authority). No add-role helper exists. |
| `0039`–`0042` chat write journal, CSV and XML | Uses existing chat/preview/build roles; no add-role helper. |
| `0043` exact chat approval | If reader/writer/lifecycle already exist but approver does not, use [`postgres-add-chat-approver-role.sh`](../deploy/local/postgres-add-chat-approver-role.sh). Do not also run the all-chat-roles helper. See [approval composition](chat-edit-approval-composition.md). |
| `0044` specification bundles | Separately provision CONNECT-only `weft_spec_reader`, `weft_spec_writer`, `weft_spec_maintenance`; see [specification storage](spec-bundle-registry.md). No add-role helper exists. |
| `0045` mapping creation | Separately provision CONNECT-only `weft_creation_reader`, `weft_creation_writer`, `weft_creation_approver`; see [creation roles](mapping-creation-persistence.md#roles-and-deployment). No add-role helper exists. |
| `0046`–`0047` document evidence and JSON contract | Uses the existing specification/creation roles and exact installed prefix; no new role helper. |

For rows without a helper, use a separately reviewed DBA provisioning step that
matches the restricted definitions in the fresh initializer and the migration's
role checks. Do not improvise a privileged runtime login or assume those rows
are optional because their feature flag is off.

### Fresh initialization is not an upgrade

[`postgres-init.sh`](../deploy/local/postgres-init.sh) runs only in a new dedicated
local PostgreSQL volume. Azure's
[`provision-database.sh`](../deploy/azure/provision-database.sh) is the separate
first-time database/role provisioning job. Both create the current schema-required
role set before migrations, including draft, specification and creation roles;
absent opt-in specification/creation passwords leave unusable password logins.
Neither is a substitute for adding only missing roles to an existing database.
The Azure job must first resolve the documented
[credential-wiring gap](azure-deployment.md#open-questions-and-limits).
The Azure provisioner refuses existing roles, while
[`initialize-secrets.sh`](../deploy/azure/initialize-secrets.sh) deliberately keeps
existing secrets. Secret initialization is not a database-role upgrade.

Keep administrator provisioning, migration ownership, runtime credentials,
maintenance, key bootstrap and release/grant tools separate. Reverting an image
does not undo migrations, restore retired objects or roll back a release pointer.
Retain the original database, objects, key ring and decrypt certificates during
recovery; use the [Session host](session-host.md), [Azure operations](azure-deployment.md#operations)
and [release recovery](release-operations.md#conditional-actions-and-recovery)
procedures for the affected operation.
