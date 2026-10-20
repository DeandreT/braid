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
executed. A fixed server-owned package-free .NET 10 project, empty package-source
configuration and exact SDK selection control compilation.

Child environments are cleared, then populated with only the required tool,
home and bounded cache paths. Every isolated `DOTNET_CLI_HOME` explicitly sets
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`. Compilation disables shared compiler
servers and node reuse. Mapping code is compiled but never executed by this
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

Build the native image first from `engines/ferrule-host`, then use
`tools/build-worker/build_image.py --native-image sha256:<id> --tag <tag>
--work-root <existing-work-directory>`. The launcher binds a temporary local
reference to that exact image ID and independently checks the native binary's
hash in the Docker build. Registry builds may use a native manifest digest with
the corresponding verified binary hash. Local metadata/protocol tests
do not establish actual Linux compilation/isolation. Qualify the exact combined
image with real generation, compilation, preview/regression equivalence,
cancellation and cleanup before enabling it in a deployment workflow.
