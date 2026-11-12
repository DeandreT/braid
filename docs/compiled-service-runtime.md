# Compiled execution through the Ferrule service

The optional `execute` profile carries the
`weft.ferrule-compiled-json.v1`, `weft.ferrule-compiled-document.v1` and
`weft.ferrule-compiled-csv.v1` frames. It runs only the installed
`Weft.CompiledRuntime` host in a fresh child. Mapping assemblies remain outside
the service and endpoint processes; the child loads the already verified bytes
through the same generated-library ABI used by the Docker runtime.

## Explicit admission

`WEFT_FERRULE_SERVICE_EXECUTION_ENABLED=true` enables execution. Without it the
profile stays reserved and returns 501. The installation settings are:

| Setting | Default |
| --- | --- |
| `WEFT_FERRULE_SERVICE_RUNTIME_ASSEMBLY` | `/runtime/weft-compiled-runtime.dll` |
| `WEFT_FERRULE_SERVICE_RUNTIME_LAUNCHER` | `/weft-compile-sandbox` |
| `WEFT_FERRULE_SERVICE_RUNTIME_DOTNET_ROOT` | `/usr/share/dotnet` |

These paths are trusted host settings, never request fields. Startup proves the
launcher's controls before serving execution, and that the installed runtime host
starts and runs under them: an empty frame must fail with the host's fixed decode
line, so readiness never reports a runtime that cannot start. Configuration does
not enable compilation implicitly; the two profiles have independent opt-ins.

The service reports its execution identity in `Weft-Compiled-Runtime` and the
`execute` capability. Every execution request must contain that exact pin, and
the client verifies it before sending a frame and on the answer. A missing or
changed pin refuses admission. An inconsistent answer after delivery is uncertain
and closes client admission rather than being retried automatically.

The CSV frame is restricted to a primary flat CSV-to-CSV build. It invokes
`ExecuteCsvDocumentBytes(byte[], FerruleExecutionContext)` from the pinned
generated library. Both replay and output descriptors carry the exact dialect
bound into the build intent. Explicit zero-byte CSV frames are valid; missing
frames are not. JSON/EDI retain their nonempty input rules and original wire
representations. Media comes from the admitted boundary, not a filename.

The generated parser enforces native CSV input limits. Output retains Weft's
smaller requested byte limit and is checked for the native 1 MiB cell,
100,000-record (including header), 256-column and 1,000,000-node ceilings before
publication. Typed CSV size failures return a resource-limit outcome without
exposing raw parser diagnostics. Build qualification still requires exact bytes
for every output; it does not substitute row comparison for the release proof.

Run the trusted service image's `runtime-identity` command to measure its native
and runtime pins and prove the execution sandbox before configuring an endpoint.
It reports them as `weft.compiled-runtime-image-identity.v1` and needs only the
installed files and a private writable `/tmp`, not a listener or credentials.

The `weft.compiled-runtime-identity.v1` digest binds the selected entry filename,
the complete installed runtime-host directory, the .NET host and pinned runtime,
the launcher and the runner implementation. It also covers the host's ELF
interpreter and every system library the host, runtime and launcher can load,
found by the same dependency walk as the compile toolchain identity
([build-compiler.md](build-compiler.md)), because the image installs those
libraries from unpinned packages and outputs can depend on them. It is independent
of the native emitter and original compiler identities. Changing the runtime
bytes, the entrypoint or a library the child can load changes its identity even
when the native worker is unchanged.

## Child boundary

The existing launcher installs Landlock and seccomp before its readiness marker;
the service checks the child in procfs before sending its start byte or payload.
The native launcher stays the process. It loads only the pinned .NET 10.0.12
hostfxr in-process, runs its one fixed installed managed entrypoint and never
executes another image. Before loading hostfxr it seals dumpability at zero,
no-new-privileges and empty active capabilities. Its filters deny both exec
syscalls, every dumpability/ptracer-setting prctl (including upper-bit aliases),
credential/group changes and capability mutation. Managed code and its threads
cannot re-enable dumpability. Runtime initialization refuses a different framework,
extra dependency roots or external probing. A host incompatibility fails closed.

This privacy continuity begins at successful native hardening and covers trusted
.NET startup and compiled execution. A trusted service account and deployment
origin are prerequisites, not facts established by these checks: an unrestricted
same-UID actor must not attach a tracer or open a memory handle before hardening.
The launcher does not claim protection from process birth or from an already
attached tracer. Untrusted workloads begin only after hardening; already hardened
peers cannot trace them. Keep the admitted installation immutable and the initial
launcher environment cleared, including loader, profiler, startup-hook and probing
settings. `host_path` is .NET metadata, not the executed native image. Identity
measurement includes the actual surviving launcher, its ELF interpreter and their
dependencies, hostfxr, hostpolicy, CoreCLR and the installed runtime configs/deps.

The `WFC2` readiness marker rejects the older launch contract. The changed runner
and launcher also change the execution/compiler pins. A stale pin is refused;
the service never approves or re-pins an installation automatically.

The child can read its own empty operation directory, installed runtime and
system libraries. No file is writable. Network sockets, new processes, metadata
mutations and foreign-process access remain denied. The service and every child
run as the same user, so the launcher's syscall filter also refuses each route by
which a child could signal the service or a sibling, including descriptor owners
and asynchronous notification, on every admitted Landlock ABI (see "Launcher
isolation from other processes" in [build-compiler.md](build-compiler.md)).

The managed heap is bounded to 512 MiB, address space to 3 GiB, CPU to 30 seconds,
open files to 128 and file size to 1 MiB. The file-size bound also caps each memory
file, which no address-space limit counts, so the child runs with
`DOTNET_EnableWriteXorExecute=0`: the runtime's W^X mode double-maps generated
code through a memory file that outgrows that bound. The sandbox, not W^X, is the
boundary for the artifact's code.

The 30-second child deadline includes startup, input transfer and execution; the
service's bounded ingress, queueing, transport settlement and cleanup rules still
apply. There is no claim of a separate per-child cgroup memory quota: size the service's
outer memory bound for its configured concurrency and request capacity. The
capability reports both managed heap and address-space bounds. Threads are not
counted per child either: without a per-child cgroup there is no equivalent of the
Docker profile's 64-task limit, so a child's threads are bounded only by its
address space, CPU time and deadline. Bound the replica's tasks (for example the
container's pids limit) for its configured concurrency.

Every child and private operation directory settles before a successful answer.
Unconfirmed process or pipe cleanup closes the runner and fails readiness. Stop
joins the original work instead of detaching it.

## Machine endpoint selection

The endpoint defaults to its existing Docker compiled-runtime profile. For a
service or dynamic-session runtime, select `WEFT_FERRULE_RUNNER=service` or
`azureContainerAppsSessions` and supply the corresponding connector settings.
`WEFT_COMPILED_RUNTIME_IMAGE` is then the exact execution identity, not the native
worker digest. It must match the registered release target and qualification.

`WEFT_FERRULE_WORKER_IMAGE` plus `WEFT_FERRULE_COMPILE_TOOLCHAIN` independently bind
the artifact's original compiler identity. The endpoint still performs all
current grant, release-pointer, approval, artifact and output checks. It never
changes a release's runtime or compiler identity to fit the service, and never
loads the assembly in-process. Another runner's settings beside the selection are
refused rather than ignored: the service URL, certificate pin and client
certificate and key files, the dynamic-sessions pool endpoint and managed identity,
and `WEFT_DOCKER_EXECUTABLE`. An empty value counts as unset, as for every endpoint
setting.

A service runtime keeps the endpoint's call bounds: work stops at 85 seconds and
the call, including its slot, ends by 90. Of the five seconds between them, one
releases the canceled attempt (a lost release expires with its lease) and half a
second writes the response. The rest settles a canceled execution, that is, the
cancellation request and the wait for the original answer, and for dynamic sessions
also the session's stop request. An execution the service has not settled by then
has an unknown outcome. Its late answer is discarded and the runner closes, so this
process sends no further frame and readiness fails. Because the attempt is released
rather than recorded, a retry with the same request ID runs the deterministic
execution again on a ready replica, and the ledger records one result.

Endpoint readiness also reflects the runner. A runner that closed after
unconfirmed cleanup or an uncertain answer stays closed until restart, so readiness
fails and the platform can replace the replica; a retry of a recorded call still
replays without running anything.

To execute a previously qualified artifact whose compiler identity is not the
service's native/toolchain combination, supply its exact original
`WEFT_ENDPOINT_COMPILER_BUILD` instead of `WEFT_FERRULE_COMPILE_TOOLCHAIN`. The
two forms cannot be configured together. This explicit pin does not change the
artifact or infer compatibility; the usual artifact and generated-ABI checks
still apply.

Local protocol and sandbox qualification does not establish live Azure behavior.
