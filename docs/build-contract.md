# Build intent and capability preflight

`Weft.Deployment` defines immutable build intent, a pure capability check and
intent-bound artifact references. It performs no build, deployment, storage,
network or authorization work.

## Intent identity

A `BuildIntent` binds one build ID to the full mapping revision and native
digest, exact backend/engine/build/adapter identity, runtime profile and version,
runtime and artifact formats, all requested boundary formats, target environment and its version,
and approval/regression evidence IDs. The environment must belong to the same
organization and project as the revision.

The legacy compiler profile is `weft.ferrule-csharp-json.v1`. The separate
`weft.ferrule-csharp-document.v1` profile resolves every native boundary's explicit
format before lowering or emission. It never selects a codec from a design-time
path. JSON-only document builds retain Ferrule's JSON byte entrypoints; X12 and
EDIFACT builds use its static format-bearing document companions. The pinned
compiler's schema and envelope-version requirements still apply. Missing or
unresolved EDI configuration and unsupported schemas are refused by preflight,
not advertised as executable artifacts.

`weft.ferrule-csharp-x12-interchanges.v1` is a separate, explicit environment
profile for Ferrule's MapForce-interchange grammar. Every X12 boundary in that
profile uses repeating interchange/group/message owners, optional repeating
ISA/GS/GE/IEA segments, singular ST/SE message bounds and a retained supported
`x12_interchange_version`. It is never inferred from a schema or filename. The
ordinary document profile keeps its single-transaction grammar. The selected
profile is part of the environment, approval, build intent and artifact identity;
incompatible schemas or configured input syntax are refused without fallback.
The additive `0035_x12_interchange_builds.sql` migration admits this profile's
exact document qualification evidence without rewriting earlier migrations.

Administrators choose between server-offered compiler profiles when registering
an environment version. Approval and submission retain that exact profile and
its matching backend adapter identity; they do not reinterpret a legacy approval.
Document regression runs may use ordered segment comparison, but compiled build
qualification still requires a complete all-target exact-byte baseline. Output
media must also match the declared build boundaries. A segment-equivalent but
byte-different result is not compiler parity evidence.

Compiled document requests use `weft.ferrule-compiled-document.v1`, binding each
input's declared media into the replay digest. Replies retain and verify each
output's media against the intent. Legacy compiled request/reply grammar and
replay digest omit media extensions and remain unchanged.

The restricted compiled child validates EDI input envelope counts and control
references before loading or invoking the generated assembly. Invalid envelopes
return `invalidInput` without outputs; regression comparison uses the same
bounded lexical validator. Valid EDI is still read by the pinned generated
document adapter, not converted through a separate business mapping.

Compiled document outputs have the same publication bounds as interpreted
previews: EDI is limited to 2 MiB, 32,768 segments and 16 KiB per segment; JSON
is limited to 64 nesting levels. The generated assembly retains the admitted
X12 output terminator for headerless documents. Every selected output is checked
before success is published; one over-limit output returns `resourceLimit` with
no partial outputs for all-target, primary or named execution. These document
limits do not change the legacy JSON profile.
Headerless X12 artifacts without retained syntax metadata must be rebuilt;
self-describing ISA outputs do not require that metadata.
Only a complete, valid fixed-width ISA can override the mapping's declared
segment terminator. Partial or malformed ISA-like output remains subject to
that declared syntax when enforcing segment limits.

The `weft.build-intent.v1` fingerprint hashes a fixed-order UTF-8 JSON object.
GUIDs use lowercase D format. Boundaries are sorted by kind and ordinal name;
their caller-supplied order is not an execution input. Primary input/output
identities have null names; named identities preserve exact case, Unicode and
spacing. A format is an explicit identifier, not a guessed extension. No names
are interpreted as paths.

Strings and collection sizes are bounded before serialization. Lists are owned
snapshots. All execution-affecting fields, including the profile artifact-size
ceiling, participate in the fingerprint.

## Capability versus authority

`BuildPreflightService.Evaluate` accepts only an exactly matching capability
snapshot for the same revision, digest, backend, profile and complete boundary
set. `Unsupported`, `NotQualified` and `Conditional` capabilities do not become
supported through caller flags. Conditional capability remains
`ConditionsRequired`; this module has no condition-bypass mechanism. Successful
inspection, validation or preview is not build qualification.

**Supported is not authorized.** Capability snapshots must come from a trusted
backend adapter, never from a browser or model. Approval and regression IDs in
an intent are references, not proof: the application workflow must reload their
ownership, exact revision and current status, and check fresh project/environment
authority before dispatch and publication. This pure module grants no permission.

## Artifact references

`BuildArtifactReference.Create` requires supported matching capability facts,
a nonempty content digest and a positive declared byte count no larger than the
profile ceiling. The maximum configurable ceiling is 1 GiB; constructing a
reference does not allocate that size. `MatchesIntent` prevents reusing the
reference for a changed build, revision, runtime, environment, approval or
regression selection.

A reference is **not evidence that bytes exist or are verified**, and constructing
two references is not an idempotent publication operation. The later build/store
workflow must verify exact bytes, reject conflicting publication under the same
build ID, and retain immutable provenance. A deployment workflow must verify the
reference and current authority again. Runtime execution, durable publication,
fixture target adapters, Azure targets and rollback remain separate increments.
