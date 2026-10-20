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
