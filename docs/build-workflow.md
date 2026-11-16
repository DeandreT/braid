# Verified build workflow

`BuildWorkflow` consumes an immutable build intent through trusted admission,
compiler, publication-catalog and private-object ports. It produces actual
artifact bytes and verifies them before and after publication.

## Admission and immutable publication

An `IBuildAdmissionReader` must reload current project permission, the exact
environment version, approval and regression evidence for every call. A valid
result binds the requesting principal, complete intent fingerprint and trusted
native object. IDs supplied in a request never establish approval. Regression
evidence must be reopened and compared against the saved baseline rather than
trusting a stored pass flag.

Compiler capability is independently checked against the exact backend,
runtime/profile and complete physical boundary set. Unsupported or unqualified
capabilities do not reach artifact production. Native bytes are verified before
the compiler receives an owned copy.

The workflow admits at most four concurrent builds per instance and refuses
excess work rather than maintaining an unbounded in-process waiting queue. It
rechecks admission after input verification, before artifact publication and
before returning a verified result. Requested cancellation propagates; an
ordinary failure returns a closed outcome without exposing dependency text or
preventing a later independent build.

Before object publication, the catalog stages the first artifact's exact bytes,
identity and retention reservation. Same-intent races use that selected stage;
they never replace it with another compiler output. This is required even when
the compiler is nondeterministic. After a lost staging, object or catalog
acknowledgment, retry recovers the stage or committed result without recompiling.

Artifact bytes are published create-only through `IPrivateObjectStore`, under
the exact revision and build identity. Their size and digest are independently
verified. The catalog must atomically recheck current authority and record the
publication, including retention ownership. Original request replay reloads and
verifies the existing artifact without invoking the compiler again. A changed
actor, environment, approval, regression selection, source or profile cannot
adopt it.

The [PostgreSQL adapters](build-persistence.md) make approvals and first-writer
staging durable. Their private database artifact storage is independent of
filesystem orphan maintenance. [Durable build runs](build-runs.md) compose the
workflow with bounded queueing, current-claim publication fencing and explicit
worker configuration.
