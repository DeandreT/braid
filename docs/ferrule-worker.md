# Ferrule inspection worker

The C# application uses `IMappingInspectionBackend` to inspect and validate and
`IMappingProjectionBackend` to explain immutable native artifacts. The first
adapter, `FerruleInspectionBackend`, talks
to a restricted Rust worker. It keeps Ferrule types and native JSON decoding
inside that worker; application results contain Weft-owned data and evidence.

Inspection and validation were qualified in
[PR #10](https://github.com/DeandreT/weft/pull/10). Native projection is tracked in
[#8](https://github.com/DeandreT/weft/issues/8), followed by the Angular viewer in
[#9](https://github.com/DeandreT/weft/issues/9). See
[Ferrule projection](ferrule-projection.md) for explanation semantics.

## Native authority and results

Ferrule libraries are pinned to `577359a991a7dd44172a3c4af91fdcab7592457c`.
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

Creation, attachment, and removal are separate operations. A caller cancellation
before creation starts no container. Once creation begins, the runner waits for
that side effect within the original ten-second wall budget, then rechecks
cancellation before inspection, worker startup or native input. Running-worker
cancellation and wall expiry stop the CLI; exact owned removal has a separate
two-second bound. Forced removal can silently succeed on absence, so an
unconfirmed create requires acknowledgement of the exact removed name. A quiet
absence is accepted only after creation was confirmed. Unconfirmed cleanup blocks
further admission on that singleton runner; an unresponsive daemon or abrupt host
kill does not produce a fabricated cleanup guarantee.

Browser qualification gives its API and frontend processes bounded SIGTERM
shutdown and audits every owned worker ID after teardown. The tests retain page
closure with pending requests so ordinary cancellation remains exercised.
This local Docker runner can later be replaced by a separately qualified hosted
worker transport behind the same backend contract.

The image builds on pinned Ubuntu 24.04 using checksum-verified rustup 1.29.1 and
nightly `2026-07-21`, with locked dependencies and two build jobs. Its scratch
runtime contains only `/ferrule-host`, observed allowlisted GNU runtime libraries,
and dependency copyright/license metadata. BuildKit is required to build it.

## Qualification

From the repository root, with the pinned .NET SDK and Rust toolchain installed:

```sh
CARGO_BUILD_JOBS=2 CARGO_INCREMENTAL=0 CARGO_TARGET_DIR=target/ferrule-host \
  cargo +nightly-2026-07-21 test --manifest-path engines/ferrule-host/Cargo.toml --locked
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
