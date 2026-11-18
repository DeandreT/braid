# Build intent and capability preflight

`Braid.Deployment` defines immutable build intent, a pure capability check and
intent-bound artifact references.

## Intent identity

A `BuildIntent` binds one build ID to the full mapping revision and native
digest, exact backend/engine/build/adapter identity, runtime profile and version,
runtime and artifact formats, all requested boundary formats, target environment and its version,
and approval/regression evidence IDs. The environment must belong to the same
organization and project as the revision.

The JSON compiler profile is `braid.ferrule-csharp-json.v1`. The separate
`braid.ferrule-csharp-document.v1` profile resolves every native boundary's explicit
format before lowering or emission. It never selects a codec from a design-time
path. JSON-only document builds retain Ferrule's JSON byte entrypoints; X12 and
EDIFACT builds use its static format-bearing document companions. The pinned
compiler's schema and envelope-version requirements still apply. Missing or
unresolved EDI configuration and unsupported schemas are refused by preflight,
not advertised as executable artifacts.

`braid.ferrule-csharp-x12-interchanges.v1` is a separate, explicit environment
profile for Ferrule's MapForce-interchange grammar. Every X12 boundary in that
profile uses repeating interchange/group/message owners, optional repeating
ISA/GS/GE/IEA segments, singular ST/SE message bounds and a retained supported
`x12_interchange_version`. It is never inferred from a schema or filename. The
ordinary document profile keeps its single-transaction grammar. The selected
profile is part of the environment, approval, build intent and artifact identity;
incompatible schemas or configured input syntax are refused without fallback.

Administrators choose between server-offered compiler profiles when registering
an environment version. Approval and submission retain that exact profile and
its matching backend adapter identity.
Document regression runs may use ordered segment comparison, but compiled build
qualification still requires a complete all-target exact-byte baseline. Output
media must also match the declared build boundaries. A segment-equivalent but
byte-different result is not compiler parity evidence.

Compiled document requests use `braid.ferrule-compiled-document.v1`, binding each
input's declared media into the replay digest. Replies retain and verify each
output's media against the intent.

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
limits apply to the document profile.
Only a complete, valid fixed-width ISA can override the mapping's declared
segment terminator. Partial or malformed ISA-like output remains subject to
that declared syntax when enforcing segment limits.

The `braid.build-intent.v1` fingerprint hashes a fixed-order UTF-8 JSON object.
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
`ConditionsRequired`; this module has no condition-bypass mechanism.

Capability snapshots come from a trusted backend adapter, never a browser or
model. The application reloads approval and regression records to check ownership,
exact revision and current status. It checks fresh project/environment authority
before dispatch and publication.

## Artifact references

`BuildArtifactReference.Create` requires supported matching capability facts,
a nonempty content digest and a positive declared byte count no larger than the
profile ceiling. The maximum configurable ceiling is 1 GiB; constructing a
reference does not allocate that size. `MatchesIntent` prevents reusing the
reference for a changed build, revision, runtime, environment, approval or
regression selection.

The build/store workflow verifies exact bytes, rejects conflicting publication
under the same build ID, and retains immutable provenance. Deployment verifies
the reference and current authority again.

## Mapping package manifest

`BuildPublication.Manifest` derives a canonical `braid.mapping-package.v1`
manifest from the retained artifact reference. It binds the fixed
`Ferrule.Generated.dll` filename, exact SHA-256 and byte count, and the complete
canonical build intent. The manifest fingerprint therefore includes source,
compiler, runtime, environment, approval and I/O identities. Reopening a
publication reproduces the same manifest without another mutable catalog row.

`MappingPackageManifest.Decode` refuses alternate serialization, duplicate or
unknown fields, unknown versions, invalid intent and out-of-range lengths.
`Matches` compares against independently loaded publication facts and exact DLL
bytes. Compilation qualification and release execution share that check through
`CompiledBuildExecution` before dispatch to the bounded child.

A manifest is not a signature or an authorization token. Parsing it, or matching
its digest, does not establish its publisher. The existing authenticated catalog,
current build admission and release checks remain mandatory. This metadata
contract does not add an external package-import endpoint or replica-readiness
protocol.
