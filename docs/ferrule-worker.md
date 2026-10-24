# Ferrule inspection worker

The C# application uses `IMappingInspectionBackend` to inspect and validate and
`IMappingProjectionBackend` to explain immutable native artifacts. The first
adapter, `FerruleInspectionBackend`, talks
to a restricted Rust worker. It keeps Ferrule types and native JSON decoding
inside that worker; application results contain Weft-owned data and evidence.

Hosted environments that cannot run Docker use the same worker binary and
protocols inside the [Ferrule service](ferrule-service.md).

Inspection and validation were qualified in
[PR #10](https://github.com/DeandreT/weft/pull/10). Native projection is tracked in
[#8](https://github.com/DeandreT/weft/issues/8), followed by the Angular viewer in
[#9](https://github.com/DeandreT/weft/issues/9). See
[Ferrule projection](ferrule-projection.md) for explanation semantics.

## Native authority and results

Ferrule libraries are pinned to `0169809123b8caebd141b26a625be4c07e750536`.
`mapping::project_file::decode_bytes` admits the file;
`engine::validate` supplies validation issues and typed owners. The worker never
evaluates mappings or resolves design-time resources. Inspection returns boundary
inventory, structural counts, located unresolved resource kinds, and operation
capabilities. It omits resource values and format-option contents.

Native bytes pass as a separate binary frame. Both sides verify their SHA-256;
C# and JavaScript never parse and reserialize the native document. Provenance
records the full immutable revision scope, native digest, engine revision,
trusted worker image identity, and adapter build. `Confirmed` means the adapter
accepted matching bounded worker output; it does not imply execution admission.

The current adapter identity is `weft.ferrule-adapter.v3`. Current report keys
include this identity, the full Ferrule commit and the server-selected immutable
image. A changed engine selects a different key without rewriting native bytes,
ingestion facts or retained report provenance. Stored-format readers compare a
retained report with its own complete key and do not resolve a current worker.

Advancing the pin therefore has these effects. Native revisions are unchanged and
decode at the new pin, including legacy unmarked files. Current reads select new
projection and validation keys, so retained reports re-derive with the new worker
on their next current request; reports and ingestion evidence derived earlier keep
their original engine, image and adapter provenance and stay readable by their own
keys. Preview provenance, saved regression cases and build intents carry the full
engine revision and image identity. A saved case runs only on its recorded
runtime, and existing builds, approvals and published artifacts stay bound to
their original compiler and runtime images: the new runtime refuses them, so they
need a new build, regression evidence and approval at the new revision.

Validation validity and total issue count are separate from the emitted bounded
diagnostics. Exact typed owners identify graph/function nodes, endpoints,
schema/scope steps, and failure rules. Upstream node IDs are `u32`; function and
function-parameter IDs are `u64`. IDs and structural indices cross the worker
boundary as strings. Human location text is display data and is never parsed to
recover identity.

Native JSON pointers account for the versioned `/document` envelope. A shallow
header probe follows authoritative decoding and preserves ignored legacy
metadata behavior. A pointer whose precision or size is unqualified falls back
to the document locator while retaining its typed owner. Exact endpoint owner
names exceeding the text budget refuse admission instead of silently changing
identity. Display-name and diagnostic text truncation is explicit.

## Protocol and limits

`weft.ferrule-host.v1` admits one request per worker process:

1. A four-byte unsigned big-endian header length, followed by UTF-8 JSON.
2. A four-byte unsigned big-endian native length, followed by exact native bytes.
3. EOF; additional frames or trailing bytes are refused.
4. One four-byte length-prefixed UTF-8 JSON response, then process exit.

The header contains protocol version, request ID, `inspect`, `validate`, or `project`,
organization/project/mapping/revision IDs, native digest, a logical document ID,
and service-selected limits. The response echoes that context and pinned engine
revision, with exactly one typed result or failure. Transport metadata grants no
artifact paths, commands, payloads, credentials, or execution parameters.

| Boundary | Compiled ceiling |
| --- | --- |
| Header | 16 KiB |
| Native document | 4 MiB, including legacy files |
| Response | 8 MiB |
| Structural traversal items | 8,192 |
| Emitted diagnostics | 128 |
| Full validation issue count | 32,768 |
| Individual display/owner text | 2,048 UTF-8 bytes |
| Docker control output and stderr | 64 KiB per stream |
| Active worker per runner | 1 |
| Worker wall deadline | At most 10 seconds |

Lengths are checked before frame allocation. The Rust serializer uses a bounded
writer. C# preflights JSON depth, duplicate properties, and collection counts
before allocating its typed result. Unsupported input, structural/result limits,
protocol failure, worker failure, cancellation, timeout, unavailable isolation,
and cleanup failure remain distinct outcomes.

## JSON sample preview

`IMappingPreviewBackend` runs one bounded sample preview of an exact native
revision. `FerrulePreviewBackend` (adapter `weft.ferrule-preview-adapter.v1`)
sends a separate `weft.ferrule-preview.v1` request to the same worker image:

1. The length-prefixed UTF-8 JSON header, bounded by the same 16 KiB.
2. The exact native frame.
3. One frame per declared input, primary first, each matching its declared
   length and SHA-256.
4. EOF.

The header carries the revision context, target selection (all, primary or one
named target), an explicit execution clock, typed runtime parameters (string,
integer, float, boolean; numbers as exact decimal text) and service-selected
limits. The worker answers with one JSON header frame followed by exactly the
declared output frames, each with boundary ID, target name, media type, record
count, byte length and SHA-256, in primary-then-named order.

The worker calls Ferrule's public `cli::run_project_value_payloads` with
`with_current_datetime`, the supplied parameters and target selection; it never
substitutes ambient time or design-time preview values. Profile
`weft.ferrule-json-preview.v1` admits only valid mappings whose reachable
boundaries are static plain JSON: no format option other than JSON selection,
any stored design-time path must name a `.json` document, and dynamic inputs
or outputs are refused. Admission, required named inputs and declared parameter
names are checked before any input is parsed. Missing, unexpected or invalid
inputs and parameters, invalid clocks, unsupported capabilities, execution
errors and limits are distinct bounded failures; no partial outputs are returned.

| Preview boundary | Compiled ceiling |
| --- | --- |
| Input documents | 16, 8 MiB combined |
| Output artifacts | 64, 8 MiB combined |
| Runtime parameters | 32; names 256 bytes, text 4 KiB |
| Response header | 1 MiB |
| Container memory | 512 MiB, no swap |
| CPU time and wall deadline | 30 seconds |

Preview containers use the `DockerWorkerProfile.Preview` runner profile. It changes
only memory, CPU time, wall deadline and frame bounds; network, filesystem, user,
capability, process and cleanup policy are identical to inspection. Inspection
results continue to report preview as `notQualified` until a product preview
workflow is exposed.

## Local isolation and lifecycle

The local runner requires Linux, a local Unix Docker socket, cgroup v2 with the
required controls, an active seccomp profile, and a loaded immutable image ID.
It checks daemon, image, and created-container metadata before starting the
worker. Runtime policy uses network `none`, a read-only root, non-root UID/GID
65532, dropped capabilities, no new privileges, private cgroup/IPC policy, no
mounts or ports, one CPU, 256 MiB memory without swap, 16 PIDs, and fixed CPU/file
descriptor limits. Application credentials and user Docker configuration are
excluded. Known Docker default environment values are admitted explicitly.

Creation, attachment, and removal are separate operations. Each admission generates
a fresh random name and an independent random operation marker. The container label
`io.weft.inspection.operation.v1` holds the marker as 32 lowercase hexadecimal
characters; it contains no credentials or mapping data. Neither the name prefix,
image, nor a previous operation's marker establishes ownership. Before startup,
the runner verifies the acknowledged full container ID, exact name, current marker
and isolation policy; attachment addresses only that immutable ID.

A caller cancellation before creation starts no container. Once creation begins,
the runner waits for that side effect within the operation's original wall budget,
then rechecks cancellation before policy inspection, worker startup or native
input. Running-worker cancellation and wall expiry stop the CLI. Cleanup shares
one separate two-second bound across ownership inspection and removal. It inspects
the acknowledged ID, or uses the exact name for read-only discovery if the create
response was lost. Only a matching name, current marker and complete container ID
authorize removal, and removal addresses that ID, never the mutable name. A
collision, mismatched marker, malformed metadata or failed ownership observation
causes no removal and blocks further admission on that singleton runner.

Forced removal can silently succeed on absence. An unacknowledged create therefore
still requires the exact verified ID in the removal acknowledgement; finding no
object by name cannot prove that an in-flight daemon request settled. Exact-ID
absence is accepted only after creation was acknowledged. Cleanup does not retry,
adopt other containers, list by label or prune by prefix/image. An unresponsive
daemon or abrupt host kill does not produce a fabricated cleanup guarantee. The
label is local attribution, not authentication against a malicious daemon or an
actor with Docker control-plane access.

Qualification teardown follows the same marker/name/full-ID rule before removal.
Legacy probe records that retain only a generated name authorize read-only absence
checks; they do not authorize deleting a live object. A live object under such a
record remains a failed qualification requiring separately attributed settlement.

The server host owns the shared runner's shutdown. When the host begins stopping,
the runner closes admission, cancels queued and running work through the normal
cancellation path, and joins every original operation, including its creation
settlement and cleanup, before its own dependencies are released. A host shutdown
timeout ends only the host's wait; service-provider disposal joins the same drain.
Unconfirmed cleanup faults shutdown instead of reporting a clean stop. A signal sent
to the whole process group can also terminate an in-flight Docker CLI directly;
that is reported as uncertain cleanup, never as an acknowledged removal.

Browser qualification gives its API and frontend processes bounded SIGTERM
shutdown and audits every owned worker ID after teardown. The tests retain page
closure with pending requests so ordinary cancellation remains exercised.
The Session host never runs this runner; it reaches the same runner in a separate
worker service through the hosted inspection transport below.

The image builds on pinned Ubuntu 24.04 using checksum-verified rustup 1.29.1 and
stable Rust `1.98.1`, Ferrule's pinned toolchain, with locked dependencies and two
build jobs. Its scratch
runtime contains only `/ferrule-host`, observed allowlisted GNU runtime libraries,
and dependency copyright/license metadata. BuildKit is required to build it.

## Hosted inspection transport

The hosted Session server has no Docker access. `RemoteInspectionWorkerRunner`
implements the same byte-only `IInspectionWorkerRunner` seam and forwards each
inspection, validation or projection frame to the inspection service in
`Weft.Worker`, which runs it with its own singleton runner and the fixed
inspection profile. The Session host keeps the Ferrule adapter: it builds the exact
frame, and it decodes and verifies the exact worker output, including digests,
echoed revision context and typed owners. The transport adds no mapping semantics.

Native upload validates synchronously inside the upload request and its
120-second claim, with explicit resume for recovery; report derivation runs inside
the ensure request under its own 60-second claim. Both already own their leases,
fencing and retry policy, and a worker call has no durable side effect, so the
transport is request/response rather than a durable queue: the claim stays the
only durable record, and a lost call is recovered by the workflow's own resume or
ensure. Durable Wolverine jobs remain for previews, which outlive the submitting
request.

`weft.inspection-transport.v1` runs HTTP/1.1 over TLS 1.2 or 1.3 on a private
network. Each side admits exactly one peer certificate by its SHA-256 pin: the
Session host pins the worker's certificate, and the worker requires a client
certificate and admits only the Session host's pin. Trust stores, host names and
revocation are not consulted. Each operation:

1. Opens one new connection; the handler cannot open a second one, so an internal
   retry cannot deliver a frame twice.
2. Sends `GET /v1/inspection/ready` on it. Under TLS 1.3 the client finishes its
   handshake before the worker has judged its certificate; this answer proves the
   worker admitted the client, and its `Weft-Worker-Image` must equal the Session
   host's configured image. No frame byte is sent before it.
3. Sends `POST /v1/inspection/operations/{operation}` on the same connection with a
   fresh 128-bit operation identity, the exact frame as `application/octet-stream`
   with a known length, and the expected image.
4. Receives `200` with `Weft-Worker-Result` (`none`, `canceled`, `timedOut`,
   `startFailed`, `workerFailed`, `outputLimitExceeded`, `cleanupFailed` or
   `sandboxUnavailable`), the worker's image and, only for `none`, the exact runner
   output with a known length.

The worker admits each operation identity once and remembers recent ones; a
repeated identity is refused. It reads the complete frame before admission, so an
incomplete frame never runs. It accepts at most eight queued or running operations
and refuses an operation that is not admitted to its runner within 20 seconds.
Any refusal before execution is a non-`200` status, which the Session host reports
as an unavailable backend; it does not close admission.

| Situation | Session result |
| --- | --- |
| Worker unreachable, TLS refused, not ready, other image, busy | Backend unavailable; nothing ran |
| Caller cancels before any frame byte | Canceled; nothing was sent |
| Caller cancels later | `POST .../{operation}/cancel`, then the original answer: canceled only after the worker's runner settled |
| No answer within admission, wall deadline, cleanup and margin | Cancellation as above; a settled answer is reported as a timeout |
| No settled answer after cancellation, connection lost after the frame, or an inadmissible answer | Cleanup failure |
| Worker reports `cleanupFailed` | Cleanup failure |

A cancellation that reaches the worker before its operation refuses that operation
when it arrives. A client that disconnects cancels its operation on the worker. A
cleanup failure, like the local runner's, closes the Session runner's admission
and faults the host's shutdown: settlement of work it requested is unconfirmed.
The worker's own runner keeps the local runner's latch for its containers. Restart
the Session host only after the worker's state is known.

Shutdown on either side closes admission and cancels outstanding operations
through the same path. The Session host joins its calls before the provider and
the borrowed client certificate are released. The worker refuses new operations,
answers admitted ones after its runner settled them, then drains its runner;
unconfirmed cleanup faults the worker's exit.

### Worker inspection service

`Weft.Worker` serves the transport only with `WEFT_INSPECTION_WORKER_ENABLED=true`:

| Setting | Value |
| --- | --- |
| `WEFT_INSPECTION_LISTEN_ADDR` | One canonical literal IP endpoint with a nonzero port on the private network, for example `0.0.0.0:8443` inside a container. |
| `WEFT_INSPECTION_SERVER_CERTIFICATE_FILE` / `WEFT_INSPECTION_SERVER_PRIVATE_KEY_FILE` | The worker's current RSA (at least 2048-bit) PEM certificate and key as protected private files, admitted like the Session host's deliveries. |
| `WEFT_INSPECTION_CLIENT_CERTIFICATE_SHA256` | Lowercase hexadecimal SHA-256 of the Session host's client certificate (DER). |
| `WEFT_FERRULE_WORKER_IMAGE` | The immutable image ID shared with the Session host. |
| `WEFT_DOCKER_EXECUTABLE` | Optional Docker CLI path (default `/usr/bin/docker`). |

The worker needs the Docker access described above; it is never reachable from the
internet. A worker that only serves inspection sets `WEFT_PREVIEW_WORKER_ENABLED=false`
and then needs no preview database login or storage root; regression and build
execution require the preview worker. Invalid or incomplete settings stop the
worker at startup. Its client and worker certificates rotate by restart: deliver a
new certificate and the matching new pin to the other side together.

## Qualification

From the repository root, with the pinned .NET SDK and Rust toolchain installed:

```sh
CARGO_BUILD_JOBS=2 CARGO_INCREMENTAL=0 CARGO_TARGET_DIR=target/ferrule-host \
  cargo +1.98.1 test --manifest-path engines/ferrule-host/Cargo.toml --locked
docker build --iidfile /tmp/weft-worker-image-id engines/ferrule-host
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
```

Set `WEFT_FERRULE_WORKER_IMAGE` to the immutable `sha256:...` value written to that
image-ID file, then run the normal .NET tests. CI builds and supplies this image
automatically. Without the image setting, live Docker tests are explicitly
skipped and do not qualify isolation. Unit process transcripts qualify lifecycle
logic; real Docker tests independently check native transport, typed validation
owners, resource isolation, cancellation, timeout, abrupt worker death, cleanup,
and subsequent admission. Projection integration cases also check the admitted
public explanation and native locators against retained artifact bytes. The
termination tests deliberately withhold stdin EOF
so a real worker remains alive while those conditions are exercised.

Fixtures are self-authored. Native correctness is compared with direct pinned
codec/validator calls; metadata and owner expectations are independently stated.
The worker tests retain exact integers and floating-point bits, including
negative zero, and verify that native artifacts remain unchanged.
