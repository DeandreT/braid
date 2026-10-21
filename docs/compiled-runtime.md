# Restricted compiled mapping invocation

The compiled runtime is an internal byte boundary around a private approved build.
It does not accept an HTTP assembly upload, select a user-supplied assembly path,
compile source or activate an endpoint. The ordinary server must never load a
mapping assembly into its own process.

## Admission

`CompiledBuildExecution` reads an own run in the complete mapping scope and
requires confirmed `Succeeded` state. An `Uncertain` run is not executable evidence.
The retained publication, artifact reference and freshly verified artifact bytes
must identify the same build, intent, digest and length. Metadata objects are
compared by their complete identity values, not reference equality.

Current approval, environment, native-source and saved-regression authority is
checked before publication reads, before private byte reads, before dispatch and
before result delivery. A fresh successful-run read fences delivery. A refused
invocation returns no outputs. No invocation mutates the build's publication or
resets its durable attempt count.

The runtime adapter has separate configured identities for the original compiler
and the executing runtime image. It requires the exact admitted compiler and
artifact profile; a different runtime image does not rewrite the original build
provenance. Runtime-image qualification is separate from compiler capability.

## Byte contract

`weft.ferrule-compiled-json.v1` uses positive, four-byte big-endian frame lengths.
The request contains a closed JSON header, canonical build intent, artifact bytes,
primary document and then named documents. The header binds the build/intent and
artifact digest, exact input hashes and lengths, fixed clock, typed lexical
parameters, target selection and effective limits. A replay digest binds that
entire invocation selection. Unknown or duplicate JSON fields, mismatched frames,
extra bytes, incomplete input sets and limit violations are refused.

The header limit is 1 MiB, including worst-case JSON escaping of all allowed
parameter strings. Artifact and canonical-intent limits remain 32 MiB and 1 MiB;
sample input/output and parameter limits reuse the bounded preview/replay contract.
Outputs retain exact bytes and logical primary/named identities. A successful
reply must contain exactly the selected output set within count and aggregate byte
limits. Every other status has no output frames; partial results never become a
passing comparison.

## Separate process

`Weft.CompiledRuntime` runs at the fixed `/weft-compiled-runtime` entrypoint. The
compiled Docker profile is non-root, network-disabled and read-only, with no host
mounts or writable tmpfs. It retains the existing runner's ownership checks,
memory/CPU/process/wall limits and automatic cleanup failure handling.
Its file-descriptor ceiling is 128: the native worker's 64-descriptor budget is
insufficient for .NET assembly loading. This does not add filesystem mounts or
network access.

Only the child loads the artifact, from verified bytes in memory. It resolves the
exact pinned generated C# ABI before interpreting runtime parameters. Primary,
named and all-target calls use their distinct generated JSON byte APIs so a
selected target does not accidentally execute unrelated targets. Runtime ABI
absence is an invalid artifact, not an invalid user parameter. Fixed mapping-path
metadata and the exact saved clock/parameters are supplied through the generated
execution context.

The generated JSON API requires the complete declared static named-input set even
for a selected output. Native preview instead admits only the named inputs needed
by that selection and refuses unused ones. Output-equivalence cases must satisfy
both admission contracts explicitly; a passing preview alone does not establish
that a saved replay is admissible for the compiled API. Missing compiled inputs
remain a failed invocation, never a fabricated pass or injected dummy document.

Generated diagnostics are not forwarded. The response carries a closed status and
verified output bytes, never raw exception messages, stack traces or source paths.
Timeout, process failure and failed cleanup remain failed invocations. Every
invocation has its own process boundary; malformed input or mapping failure must
not prevent independent valid calls from continuing.

The admission service allows four concurrent calls without an unbounded waiting
queue and bounds the full operation to 60 seconds. Capacity refusal, transport or
timeout failure returns no outputs. Requested cancellation remains cancellation;
it cannot become a successful execution result.

Successful compilation is not output equivalence. Qualification must execute
actual generated artifacts and compare complete output bytes against independently
authored baselines and the interpreted path, including named inputs/outputs,
parameters, fixed clocks, selected targets, failure isolation and corrected retry.
Azure endpoint authorization, deployment audit and rollback remain separate target
responsibilities.
