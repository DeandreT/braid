# Exact saved-case execution

`RegressionCaseExecutionService` replays one immutable saved-case reference
through the preview backend.

## Admission and replay

The caller supplies a trusted actor and the complete reference captured when
the run was requested. The workflow reloads that case version and checks its
entire envelope reference, including scope, creator, digest and byte count.
It does not silently use the newest version or substitute a new runtime.

The configured runtime must match the case's backend, engine revision, backend
build, adapter build and profile. The initial execution profile is Ferrule JSON
preview. Configuration is an expected identity, not proof of actual execution;
the returned preview provenance is verified independently.

The workflow checks current Runner-or-higher authority and the exact committed
native source. It verifies the native bytes against the saved case and reloads
authority/source after private storage I/O, immediately before dispatch. The
mapping request retains the saved target, exact primary/named bytes, typed
lexical parameters, selected limits and fixed evaluation clock.

## Result identity

A successful preview must confirm the same revision, native digest and complete
runtime. It must provide bounded JSON outputs with unambiguous identities:

- `primary-output` identifies the primary result, independently of its display
  name.
- `named-output:<canonical index>` identifies a named boundary. The full exact
  target name is required; a truncated name cannot establish a regression match.
- Duplicate boundaries or names, unknown identities, missing primary output,
  wrong selected targets and over-limit output sets are rejected.

Named output order does not affect comparison. Case-sensitive full names and
exact bytes do. Successful execution is not itself a passing regression:
`braid.regression.exact-bytes.v1` still requires an explicit baseline and exact
output equality. Failed preview statuses remain separate from comparison status
and never pass, including when they happen to carry no output.

Authority and source are checked again after comparison, before any private
result is returned. Revocation or source changes discard the output and
comparison details. Storage and backend exceptions produce a bounded failure
category rather than leaking payload text, credentials or paths.

## Failure and durability boundaries

Each invocation is independent. One malformed case or backend exception cannot
poison a later invocation. Requested cancellation propagates through storage
and the preview backend and is checked even when a dependency returns normally.
The preview backend still owns process termination and isolation cleanup.

There is no automatic retry here. The durable caller must persist each case's
request/reference and terminal result independently, retain successful results,
and retry only failed/retryable work. Lease fencing, crash recovery, queue
delivery and run-result retention belong to the durable caller.
