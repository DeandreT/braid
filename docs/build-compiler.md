# Restricted Ferrule C# compiler worker

The `weft.ferrule-build.v1` operation accepts the exact immutable native mapping
and full request/revision/native/intent identity. It does not accept C# source,
project files, build scripts, package sources or host paths from callers.

## Generation and capability

The pinned Ferrule libraries decode the native bytes and admit static plain-JSON
physical boundaries before lowering. Dynamic sources/output documents, foreign
format options and unsupported lowering/emission constructs are explicit
refusals. All declared primary/named input and output identities are returned;
the host compares the complete set against the build intent.

Generation uses upstream in-memory `codegen::lower` and
`codegen_csharp::emit`. It returns at most 128 generated files and 32 MiB total,
with explicit portable paths, lengths and SHA-256 values. No design-time path is
opened. The generator's source response is a private compiler transport, not an
API/browser source download.

## Offline .NET compilation

`Weft.BuildWorker` invokes the fixed native generator, checks every source frame,
and writes only the known generated mapping/target-builder and Runtime C# files
under a new private temporary directory. The emitted project file is never
executed. The installed .NET 10 C# compiler is invoked directly with fixed
arguments, the installed reference assemblies and trusted generated C# files.
No project evaluation, restore or package-feed discovery occurs. The admitted
source set, directory layout, compiler arguments and framed reply are one shared
implementation (`BuildCompilation` in `Weft.Deployment`), which the
[Ferrule service](#ferrule-service-compilation) uses as well.

Child environments are cleared, then populated with only the required tool,
home and bounded cache paths. Every isolated `DOTNET_CLI_HOME` explicitly sets
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`. Compilation disables shared compiler
servers and node reuse by invoking the compiler directly. Mapping code is compiled but never executed by this
worker. The response contains one managed assembly plus its exact provenance;
the application verifies all identities and digest before publication.

The combined worker image contains both the pinned native host and compiler.
Preview uses its fixed native entrypoint; the build profile selects the fixed
compiler entrypoint. The same immutable image identity can therefore be bound
to preview regression evidence and compilation without treating preview success
as code-generation capability.

## Sandbox and qualification

The build profile keeps networking disabled, capabilities dropped, non-root UID,
read-only root and private namespaces. Only a private noexec/nosuid/nodev tmpfs
is writable. CPU, memory, process/file count, frame and wall-time limits are
fixed by trusted host composition, not request input. The existing owned
container lifecycle still verifies its identity and removes it after completion
or cancellation.

## Ferrule service compilation

Hosted platforms that cannot run Docker compile through the
[Ferrule service](ferrule-service.md#compile-operations) `compile` profile, with the
same `weft.ferrule-build.v1` frames, bounds and wall deadline. Each operation runs
the generator in the worker's own service sandbox and the pinned compiler in a fresh
`weft-compile-sandbox` launcher whose controls the service verifies before the
compiler starts: fixed limits with a 2 GiB managed heap, no network or new
processes, and Landlock confinement to the read-only toolchain, the private
directory and its one pre-created output file. Its replies, including the artifact
bytes, are the Docker compiler's for the same frame and the same worker. The
service reports the native worker and complete toolchain identities separately.
Compile calls require an explicit toolchain pin before dispatch and on the answer;
their build identity binds both digests. Build-related inspection and preview use
that same pin so saved evidence matches the build intent. The toolchain digest
includes the .NET host/runtime, compiler, reference assemblies, launcher and shared
compilation implementation, rather than only a version label. It also covers the
host's ELF interpreter and every system library the host, runtime and launcher can
load, because the image installs those libraries from unpinned packages. The service
finds them without running the loader: it follows each `DT_NEEDED` entry and the
libraries the runtime opens by name (OpenSSL, LTTng and `libgcc_s`), transitively.
Each name is looked up where glibc's loader searches for it: an object with
`DT_RUNPATH` searches that run path for its own direct dependencies only and ignores
every `DT_RPATH`; any other object searches its own `DT_RPATH` and the `DT_RPATH`
inherited from each object above it in the loading chain and from the executable.
Every directory the loader may then search beneath the granted system library
directories counts too (each directory, its `x86_64-linux-gnu` directory and their
`glibc-hwcaps` subdirectories). Every match counts, so a changed, added or
alternative copy changes the identity.
Docker compilation remains the default runner for local development and tests.

The compiler stays inside the already hardened native launcher. It loads only
`host/fxr/10.0.12/libhostfxr.so`, initializes the fixed offline
`exec sdk/10.0.112/Roslyn/bincore/csc.dll -noconfig @compiler.rsp` invocation through
the hostfxr command-line API and runs it once, without another exec, SDK discovery,
compiler server or fallback. The runtime and dependency properties must resolve
within the admitted compiler directory and pinned CoreCLR installation. The
launcher refuses later exec, dumpability/ptracer changes, credential changes and
capability mutation before loading any hosting code. Its own ELF interpreter and
dependencies are part of the toolchain identity, not just the dotnet metadata path.
The trusted-origin prerequisite and post-hardening scope are the same as the
[compiled runtime boundary](compiled-service-runtime.md#child-boundary).

### Launcher isolation from other processes

The service and every compiler run as the same user, so the launcher's syscall
filter, not file permissions, keeps a compiler from other processes. Landlock scopes
signals and abstract unix sockets only from ABI 6, while the launcher admits ABI 3,
so on ABI 3 to 5 the filter alone must refuse every route. It refuses with `EPERM`:

- signals to any other process or thread (`kill`, `tkill` and `tgkill` may address
  only the compiler's own process), queued signals, and every process-descriptor
  call (`pidfd_open`, `pidfd_send_signal`, `pidfd_getfd`);
- descriptor owners and asynchronous notification, which deliver a signal chosen by
  the owner's controller: `fcntl` `F_SETOWN`, `F_SETOWN_EX`, `F_SETSIG`, `F_SETLEASE`
  and `F_SETFL` with `O_ASYNC`, and the `ioctl` requests `FIOSETOWN`, `SIOCSPGRP`,
  `FIOASYNC`, `TIOCSPGRP` and `TIOCSTI`;
- tracing and memory access (`ptrace`, `process_vm_readv`/`writev`, `kcmp`,
  `process_madvise`, `process_mrelease`, another process's robust futex list);
- scheduling, priority, I/O priority, limits, CPU affinity and memory placement of
  any other process;
- `perf_event_open`, `io_uring`, `userfaultfd`, the key management calls, System V
  IPC and message queues, and every socket, which also excludes abstract unix sockets.

Landlock grants procfs only beneath the compiler's own `/proc/<pid>`. The runtime's
own signals keep working: the process may signal itself and its threads. The
launcher qualification (`engines/compile-sandbox/tests/sandbox_qualification.rs`)
tries each route against an ordinary process of the same user and against a sibling
in its own sandbox, at the kernel's Landlock ABI and at simulated ABIs 3, 4 and 5: a
seccomp user notification answers the launcher's version query with the older ABI,
so the production sandbox library applies exactly that ABI's controls on any kernel.
The low-level native probes enter the production sandbox library and continue
in-process. The service tests additionally run the real launcher with pinned
Roslyn and managed runtime workloads, including an asynchronous managed probe
that attempts privacy mutations from its main thread and another thread. The
supplementary `tools/compile-sandbox/privacy_negative_probe.py` observes actual
post-managed-start dumpability and parent-owned attach/memory denial. Its dumpable
positive control must work, otherwise the result is inconclusive. It is not proof
of a production runner call or XML canonical-comparison invocation.

Build the native image first from `engines/ferrule-host`, then use
`tools/build-worker/build_image.py --native-image sha256:<id> --tag <tag>
--work-root <existing-work-directory>`. The launcher binds a temporary local
reference to that exact image ID and independently checks the native binary's
hash in the Docker build. Registry builds may use a native manifest digest with
the corresponding verified binary hash. Local metadata/protocol tests
do not establish actual Linux compilation/isolation. Qualify the exact combined
image with real generation, compilation, preview/regression equivalence,
cancellation and cleanup before enabling it in a deployment workflow. The service
compile tests compare every fixture's compile and inspection reply with this image
byte for byte (see [Ferrule service qualification](ferrule-service.md#qualification)).
