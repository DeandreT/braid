# Ferrule service

The Ferrule service answers questions about mapping revisions for hosted Weft:
inspection, validation, projection and JSON sample preview. Compilation of
deployable mapping libraries is reserved in the same API. Weft users never see it;
Weft hosts call it on their behalf. It is one stateless, long-running HTTP service
image that any container platform can run and scale by adding replicas. The
per-operation Docker runner remains the local development and test runner.

The service keeps the restricted worker's semantics: the same `ferrule-host`
binary, the same framed protocols (`weft.ferrule-host.v1`,
`weft.ferrule-preview.v1`) and the same fixed limits. Its answers are byte for
byte what the Docker runner returns for the same frame.

## Image

`engines/ferrule-service/Dockerfile` builds from the repository root on top of the
native worker image built from `engines/ferrule-host`. It verifies the native
binary against `WEFT_NATIVE_BINARY_SHA256` and assembles a shell-less image with
the service (`/app/weft-ferrule-service`), the verified worker (`/ferrule-host`),
the .NET and ASP.NET Core shared frameworks and their libraries. It runs as UID/GID
65532 with a read-only root, needs a writable volume at `/tmp`, and needs no
outbound network.

The service host is .NET so that it shares one implementation of the worker
transport server with `Weft.Worker`; Ferrule types and native decoding stay inside
the worker child. The service's **build identity** is `sha256:` followed by the
SHA-256 of the exact `/ferrule-host` it runs. The service computes it at startup,
and the image records the expected digest in its `io.weft.ferrule.worker-sha256`
label. Clients configure that identity and refuse a service reporting another.

## API: `weft.ferrule-service.v1`

The protocol extends `weft.inspection-transport.v1` (see
[Ferrule worker](ferrule-worker.md#hosted-inspection-transport)) for a horizontally
scaled service. A route selects a trusted profile; the frame's own header selects
the Ferrule operation within it.

| Route | Purpose |
| --- | --- |
| `GET /v1/ready` | `204` with `Weft-Worker-Image` (build identity) and `Weft-Service-Protocol`. Over pinned TLS, the answer proves that the service admitted the client certificate. |
| `GET /v1/capabilities` | JSON: protocol, build identity, engine revision, per-replica concurrency, capacity and admission wait, the served profiles with their limits, and reserved profiles. |
| `POST /v1/operations/{profile}/{operation}` | One exact frame (`application/octet-stream`, known length, `Weft-Worker-Image` equal to the build identity) under a fresh 128-bit operation identity (32 lowercase hexadecimal characters). |
| `POST /v1/operations/{profile}/{operation}/cancel` | Cancels that operation, or refuses it when it arrives later. |
| `GET /v1/health/live`, `GET /v1/health/ready` | Platform probes; readiness fails while stopping or after an unconfirmed cleanup. |

Profiles are `inspection` (inspect, validate and project frames) and `preview`.
`compile` is reserved: the service answers `501` until it implements it. The
inspection transport's own routes (`/v1/inspection/...`) are also served, so an
existing pinned inspection client works against a single service instance
unchanged.

An operation is admitted once. The service reads the complete frame before
admission, so an incomplete frame never runs. An admitted operation answers `200`
only after its child has settled, with `Weft-Worker-Result` (`none`, `canceled`,
`timedOut`, `startFailed`, `workerFailed`, `outputLimitExceeded`, `cleanupFailed`
or `sandboxUnavailable`) and, only for `none`, the exact worker output. Any other
status means nothing ran:

| Status | Meaning |
| --- | --- |
| `400` | Malformed route, identity, media type, encoding or length. |
| `404` / `501` | Unknown profile / reserved profile. |
| `409` | Other build identity, or a repeated operation identity. |
| `503` with `Retry-After` | The replica is full or stopping. A fresh operation may be sent to any replica. |

Every request of one operation, including its cancellation, must reach the same
instance. Over pinned TLS, the client sends ready, operation and cancellation as
streams of one HTTP/2 connection; a layer-4 load balancer keeps a connection on
one replica. A platform sandbox routes an operation's requests to one session by
its identifier.

## Request isolation

Every operation runs in a fresh worker child process,
`/ferrule-host --service-sandbox --cpu-seconds=… --memory-bytes=… --open-files=…`,
with an empty environment, a fresh private empty working directory under `/tmp`,
and only its three standard streams. Before it reads any input, the child:

- sets fixed hard limits: CPU time, address space and open files from the profile,
  no processes or threads (`RLIMIT_NPROC` 1), no file writes (`RLIMIT_FSIZE` 0),
  and no core dumps, locked memory, file locks, message queues or realtime priority;
- becomes non-dumpable, so no other process of the same user can trace it or read
  its procfs entries, and sets no-new-privileges;
- refuses to start if any other file descriptor was inherited;
- installs seccomp filters that deny socket creation and networking, process and
  thread creation, exec, tracing and cross-process memory access, signals to any
  other process, namespace, mount and kernel-module operations, BPF, io_uring and
  keyrings (`EPERM`), every file open (`ENOENT`, like an empty image) and every
  filesystem change (`EROFS`, like a read-only root). x32 system calls are fatal.

The child then writes a four-byte ready marker. The service verifies the
running child in procfs (no-new-privileges, filter mode and count, every limit,
non-dumpable) before it sends the first frame byte. A child that cannot prove its
sandbox receives nothing and the operation reports `sandboxUnavailable`. At the
wall deadline, on cancellation or on an output limit, the service kills the child
and waits for it to be reaped. A child that cannot be confirmed reaped, or a
private directory that cannot be removed, is unconfirmed cleanup: the replica
reports `cleanupFailed`, stops admitting operations and fails readiness, and its
shutdown faults.

| Profile | CPU and wall deadline | Address space | Open files | Request / response |
| --- | --- | --- | --- | --- |
| `inspection` | 10 s | 256 MiB | 64 | 8 MiB + 4 bytes / 8 MiB + 4 bytes |
| `preview` | 30 s | 512 MiB | 64 | the preview policy's request and response bounds |

Concurrent children of one replica share its kernel and user ID. The controls
above confine each child, but tenant-grade isolation between concurrent
operations comes from a platform sandbox around the service (see
[isolation adapters](#isolation-adapters)), or from running each replica with a
concurrency of one inside a sandboxed runtime.

## Concurrency and scaling

Each replica runs at most `WEFT_FERRULE_SERVICE_CONCURRENCY` children and reserves
at most that concurrency plus `WEFT_FERRULE_SERVICE_QUEUE` request slots. A slot
is held before buffering an incoming body until the request settles, so incomplete
uploads share the capacity limit with queued and running operations. Body reading
has its own deadline bounded by the selected profile's wall limit; an incomplete
or abandoned body releases its slot without recording an executed operation.
Cancellation routes do not consume those slots. A queued operation that does not start
within `WEFT_FERRULE_SERVICE_ADMISSION_WAIT_SECONDS` is refused unexecuted, and an
operation beyond capacity is refused at once with `503` and `Retry-After: 1`.
Nothing ran, so a client may retry with a fresh operation, and new replicas absorb
load as a platform autoscaler adds them. Budget for concurrency times 512 MiB (the
preview address-space limit), capacity times the largest admitted request frame,
and the service's own memory; give each concurrent child an allotted CPU. The queue
limit is therefore a memory budget as well as a waiting-time policy. A replica holds no state
between operations, so platforms may add or remove replicas at any time; a
stopping replica refuses new operations and answers admitted ones after their
children settle.

## Weft service runner

`FerruleServiceRunner` implements the inspection and preview runner seam
(`IInspectionWorkerRunner`) against the service, one runner per profile. The Ferrule
adapters above it are unchanged: they build the exact frames and verify the exact
answers, digests, echoed revision context and typed owners.

For every operation the runner opens one fresh channel from its connector, checks
`GET /v1/ready` (status, build identity and protocol), then sends the frame on the
same channel under a fresh operation identity. Cancellation and deadline expiry
send the cancellation on that channel and wait for the settled answer, so a caller
sees `canceled` only after the child has settled. The runner sends a call again,
with a fresh identity on a fresh channel and at most three attempts, only when the
service proves that nothing ran: a `503` from the service, or a failure before any
frame byte was written. Any answer after frame bytes that does not come from the
service, a lost connection, or a missing settled answer is a cleanup failure. As
with the hosted inspection transport, it closes the runner's admission and faults
host shutdown.

The pinned connector (`PinnedServiceConnector`) opens one HTTP/2 connection per
channel and can never open a second, so retries inside the HTTP stack cannot
deliver a frame elsewhere or twice. Place the service behind a layer-4 load balancer
(for example a Kubernetes `Service` or Container Apps TCP ingress); TLS terminates
in the service.

Hosts select the runner explicitly with `WEFT_FERRULE_RUNNER`:

| Host | Values (default first) |
| --- | --- |
| Development host (`DOTNET_ENVIRONMENT=Development`) | `docker`, `service`, `azureContainerAppsSessions` |
| Session host (native upload and report derivation) | `worker` (the hosted inspection transport), `service`, `azureContainerAppsSessions` |
| `Weft.Worker` previews (and the regressions that run them) | `docker`, `service`, `azureContainerAppsSessions` |

With `service`, the host requires:

| Setting | Value |
| --- | --- |
| `WEFT_FERRULE_SERVICE_URL` | The service's canonical HTTPS origin, for example `https://ferrule:8443`. |
| `WEFT_FERRULE_SERVICE_CERTIFICATE_SHA256` | Lowercase SHA-256 of the service certificate (DER); the only certificate admitted for it. |
| `WEFT_FERRULE_WORKER_IMAGE` | The service's build identity, recorded in admission, report and preview provenance. |
| `WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE` / `WEFT_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE` | The host's current RSA client PEM certificate and key as protected private files; its pin is in the service's admitted pins. |

Settings for a runner that is not selected are refused as ambiguous: for example, service
scalars beside the default hosted worker, or `WEFT_INSPECTION_WORKER_*` beside
`service` on the Session host. Startup does not contact the service; an unreachable
service fails operations as a retryable unavailable backend. `Weft.Worker`'s own
inspection transport and build execution keep using Docker.

## Isolation adapters

Stronger isolation than one replica's child processes comes from the platform. A
platform sandbox plugs in as an `IFerruleServiceConnector` (in
`Weft.MappingBackends.Workers`) without changing core Weft, the runner or the
service. A connector opens one channel per operation and supplies only the
transport and the caller's authentication. It must deliver every request of a
channel, including the cancellation, to the same service instance, and must never
send a request twice. The service runs unchanged inside the sandbox.

### Azure Container Apps dynamic sessions

`Weft.MappingBackends.AzureSessions` is the only project that depends on the Azure
SDK. `AzureSessionsConnector` runs each operation in a fresh session of a
custom-container session pool that runs the Ferrule service image:

- every channel is a new random session identifier (`weft-` plus 32 hexadecimal
  characters), passed as `identifier` on every request and never used again;
- requests go to the pool management endpoint with a managed-identity token for
  `https://dynamicsessions.io/.default`; the pool forwards the path to the session's
  service, so ready, operation and cancellation reach the same session;
- after the operation settles, the connector stops the session
  (`POST /.management/stopSession`); if the stop is lost, the pool's lifecycle
  removes the session;
- a pool refusal before any frame byte (for example an exhausted pool) is retried in
  a fresh session; an answer after frame bytes that does not come from the service,
  such as a gateway error, is a cleanup failure.

Select it with `WEFT_FERRULE_RUNNER=azureContainerAppsSessions` in the Development
host, the Session host or `Weft.Worker`:

| Setting | Value |
| --- | --- |
| `WEFT_FERRULE_SESSIONS_POOL_ENDPOINT` | The pool's management endpoint, one canonical HTTPS origin such as `https://<pool>.<environment>.<region>.azurecontainerapps.io`. |
| `WEFT_FERRULE_WORKER_IMAGE` | The service image's build identity. |
| `WEFT_FERRULE_SESSIONS_MANAGED_IDENTITY_CLIENT_ID` | Optional client ID of a user-assigned managed identity; without it, the system-assigned identity is used. |

The host's identity needs the *Azure ContainerApps Session Executor* role on the
pool. Configure the pool as a custom container pool with network egress disabled,
running the service image with `WEFT_FERRULE_SERVICE_AUTHENTICATION=platformSandbox`,
`WEFT_FERRULE_SERVICE_LISTEN_ADDR=0.0.0.0:8080` (the pool's target port),
`WEFT_FERRULE_SERVICE_CONCURRENCY=1` and `WEFT_FERRULE_SERVICE_QUEUE=0`, a writable
`/tmp`, and liveness and startup probes on `/v1/health/live` and `/v1/health/ready`.
Each session is a Hyper-V sandbox that serves exactly one operation, so concurrent
operations never share a kernel. The pool's maximum sessions bound concurrent
operations, and its ready sessions absorb bursts.

### Other platforms

- **Kubernetes with a sandboxed runtime.** Run the same image as a Deployment with a
  gVisor or Kata `RuntimeClass` behind a layer-4 `Service`, and use the pinned
  connector unchanged. With `WEFT_FERRULE_SERVICE_CONCURRENCY=1`, each pod's
  sandbox runs one operation at a time; scale pods with an HPA or KEDA on concurrent
  connections or CPU. The child-process controls stay in force inside the sandbox.
- **Per-operation sandboxes on other platforms** (for example a sandbox API that
  creates an isolated instance per request) need a connector that creates or
  addresses one instance per channel, authenticates to the platform, and routes the
  channel's requests to that instance; the service runs there in `platformSandbox`
  mode.
- **Platforms that terminate TLS and route each request independently** cannot
  keep an operation's cancellation on its instance. They need a connector that
  provides that affinity, such as a session identifier, before they can host the
  service.

## Configuration

| Setting | Value |
| --- | --- |
| `WEFT_FERRULE_SERVICE_LISTEN_ADDR` | One canonical literal IP endpoint with a nonzero port, for example `0.0.0.0:8443`. |
| `WEFT_FERRULE_SERVICE_AUTHENTICATION` | `pinnedMutualTls` (default) or `platformSandbox`. |
| `WEFT_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256` | One to eight comma-separated lowercase SHA-256 pins of admitted client certificates (DER), for example the Session host's and `Weft.Worker`'s. |
| `WEFT_FERRULE_SERVICE_PEM` | The service's current RSA (at least 2048-bit) certificate and private key as one PEM bundle, for platforms that deliver secrets as environment values. |
| `WEFT_FERRULE_SERVICE_CERTIFICATE_FILE` / `WEFT_FERRULE_SERVICE_PRIVATE_KEY_FILE` | The same as protected private files (owned by the service user, mode `0600`); exclusive with the PEM bundle. |
| `WEFT_FERRULE_SERVICE_HEALTH_ADDR` | Optional plain HTTP probe listener (another port); it serves only liveness and readiness. |
| `WEFT_FERRULE_SERVICE_CONCURRENCY` | Children per replica, 1–64 (default: the available processor count). |
| `WEFT_FERRULE_SERVICE_QUEUE` | Queued operations per replica (default: the concurrency). |
| `WEFT_FERRULE_SERVICE_ADMISSION_WAIT_SECONDS` | 0–20 (default 5). |
| `WEFT_FERRULE_SERVICE_WORKER_EXECUTABLE` / `WEFT_FERRULE_SERVICE_TEMPORARY_ROOT` | `/ferrule-host` and `/tmp` in the image. |

Invalid or incomplete settings stop the service with
`WeftFerruleServiceConfigurationFailed` and no echoed values; so do an unreadable
worker or an unwritable temporary root. Certificates rotate by restart: deliver
the new certificate and its pin to the clients together.

`platformSandbox` serves the service routes over plain HTTP on the listen address
and refuses any pinned material or separate probe listener beside it. Use it only
inside a per-operation platform sandbox whose front end authenticates every
caller and is the only route to the listener.

## Qualification

```sh
CARGO_BUILD_JOBS=2 CARGO_INCREMENTAL=0 CARGO_TARGET_DIR=target/ferrule-host \
  cargo +nightly-2026-07-21 test --manifest-path engines/ferrule-host/Cargo.toml --locked
```

`sandbox_qualification` runs each sandbox scenario in a fresh process. It checks
the argument shape, the procfs-visible controls, byte-identical answers to the
container mode for every fixture and operation, refusal of an inherited
descriptor, the network, process, thread, file and signal denials, the
address-space limit, and termination at the CPU limit.

Set `WEFT_FERRULE_HOST_EXECUTABLE` to a worker built from this source to run the
live .NET service tests. With `WEFT_FERRULE_WORKER_IMAGE` naming an image that
contains the same binary, the equivalence test also compares every child answer
with the Docker runner byte for byte. Without these settings the live tests are
explicitly skipped. The dynamic-sessions adapter is qualified against a local stub
of the pool management API (token checks, per-identifier sessions, forwarding,
stop, pool refusal and gateway failure); provisioning a real pool is a deployment
step.
