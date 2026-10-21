# Runtime packaging

`deploy/container/Dockerfile` builds the hosted runtime from the repository root.
Every base image is pinned by digest, and the .NET SDK is the `global.json` version
installed from its checksum-verified official archive. The file uses no BuildKit-only
syntax, so the classic Docker builder also builds it. Tracking work is [#137](https://github.com/DeandreT/weft/issues/137).

| Target | Contents |
| --- | --- |
| `weft` (default) | `Weft.Server` with the built Angular application in `wwwroot`, the operator tools and one entrypoint. |
| `edge` | The same-replica reverse proxy for the `trustedProxyOneHop` host transport. |
| `weft-local-docker` | `weft` plus the Docker CLI for the local Development worker runner only. |

```sh
docker build -f deploy/container/Dockerfile --target weft -t weft:local .
docker build -f deploy/container/Dockerfile --target edge -t weft-edge:local .
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
| `WEFT_DELIVER_SESSION_DECRYPT_PEM_1` … `_8` | Retained decryption pairs listed in `WEFT_SESSION_DECRYPT_CERTIFICATES_FILE` |
| `WEFT_DELIVER_OWNER_INPUT` | Standard input for the `database` command |
| `WEFT_DELIVER_FILE_<NAME>` | `/run/weft/secrets/<name>` (lowercase, `_` becomes `-`), for settings that take an explicit private file path |

A PEM bundle holds exactly one private key block and one or more certificates, leaf
first; this is the format of an exported Key Vault PEM certificate secret. Other
connection strings, such as `WEFT_CATALOG_CONNECTION_STRING`, remain ordinary
environment settings read by the host.

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
- The override file `deploy/compose/compose.examples.yml` adds a Development server
  in the Disabled profile with the Docker socket, so its worker runner can explain
  native examples. Docker socket access is equivalent to host root access; this file
  is local only and never part of the hosted shape.

All runtime containers use a read-only root filesystem, drop every capability and
set `no-new-privileges`. The compose file reads every value from the caller's
environment: the `WEFT_LOCAL_*_PASSWORD` values required by `postgres-init.sh` plus
`WEFT_LOCAL_POSTGRES_ADMIN_PASSWORD`, `WEFT_SESSION_ORGANIZATION_ID`,
`WEFT_SESSION_KEY_PEM`, `WEFT_ENTRA_TENANT_ID`, `WEFT_ENTRA_CLIENT_ID`,
`WEFT_ENTRA_CLIENT_PEM` and `WEFT_PUBLIC_HOST`; the examples override additionally
requires `WEFT_FERRULE_WORKER_IMAGE` and `WEFT_DOCKER_GID`. Image names default to
`weft:local`, `weft-edge:local` and `weft-local-docker:local`; `WEFT_COMPOSE_PORT`
and `WEFT_COMPOSE_EXAMPLES_PORT` choose the loopback ports (`0` selects a free one).

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
also applies the examples override and requires native projection and validation
through the worker. The generated Entra values are placeholders, so sign-in is not exercised.

## Hosted boundaries

The Session host does not register a Docker worker runner. Native ingestion and
report derivation therefore need a separately qualified hosted worker transport, and
stay disabled in hosted configuration; examples remain a Development feature. Chat
and preview submission are composed by the Session host with `_FILE` credential
deliveries (see [feature composition](session-host.md#feature-composition)); preview
execution additionally needs the separate `Weft.Worker` host.
Private storage and the session key ring support one API instance on a persistent
local filesystem. [Azure deployment](azure-deployment.md) describes the hosted environment.
