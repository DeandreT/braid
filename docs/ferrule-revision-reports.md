# Trusted Ferrule revision reports

Issue [#54](https://github.com/DeandreT/weft/issues/54) connects the existing
restricted Ferrule worker to braid's owned revision-report contracts. It builds
on the [stored report codec](revision-report-codec.md). Private-object
publication and metadata orchestration follow in #55; persisted-report routes
follow in #51. This adapter does not establish those later capabilities.

## Trusted registration and dispatch

The host selects an immutable worker image and backend profile. Registration
and stored-format validation are pure: neither starts Docker nor resolves the
shared runner. Actual derivation resolves that runner lazily and checks its
build identity against the selected profile before dispatch. A quarantined
runner remains quarantined rather than being replaced to retry a request.

The adapter checks the exact artifact scope, digest, byte count, source backend,
logical document, operation, and supported format against the complete report
key. A worker response cannot choose the trusted profile. Dependency identity
remains a fact from the revision source; native bytes alone do not prove it.

Historical stored-format checks bind the report to its own key and provenance.
They use neutral report validation and pure Ferrule-specific checks, without
requiring the current worker image. Ferrule retains its finite-only binary64
admission while the provider-independent contract can represent every binary64
payload.

## Effective limits

Report invocation carries an immutable policy explicitly through native/header
admission, raw response preflight, typed provider checks, and report ownership.
Reduced limits are enforced at these boundaries; they are not merely included
in a cache fingerprint. Native and serialized-header sizes are checked before
dispatch. Response bytes, depth, and value counts are bounded before typed
decoding. Aggregate items, text, detail depth, diagnostics, and validation issue
counts remain bounded after decoding.

The existing worker protocol still carries only diagnostic, item, and text
limits. Its image, protocol, isolation settings, fixed output capture, and
timeout/cleanup limits remain pinned. A resource refusal returns no usable
partial graph. Unsupported behavior within the budget can still produce an
honest projection with explicit coverage gaps.

The ordinary inspection, projection, validation, ingestion, and development
example paths retain their existing behavior. Named example-normalization
methods preserve the development wrapper's legacy diagnostic/path handling;
the report-normalization methods always enforce the supplied report policy.
The complete example response retains its 16-MiB boundary.

## Observations and links

Normalization preserves actual status, confirmed provenance, typed native
owners, exact paths, and full versus emitted diagnostic counts. False validity
with `InvalidMapping` is a usable validation report; null validity is not.
Cleanup failure retains `IsolationFailure` even when cancellation also occurs.
The report adapter categorizes native decode refusal as `UnsupportedArtifact`
and digest mismatch as `ProtocolFailure`, without rewriting the established
default-path classifications or claiming a stronger failure diagnosis.

`ProjectionOriginLinks` builds an owned, cancellation-aware index from actual
emitted origins. Duplicate evidence for the same owner is harmless; origins
shared by distinct owners produce no row link. Broad document evidence and
empty pointers also produce no row link. Nested unsupported details retain
their evidence. The pure index does not establish provenance: persisted callers
use a verified report, and development examples retain actual artifact and
cross-operation provenance checks without manufactured persistent metadata.

## Qualification scope

Synthetic protocol tests exercise exact admission boundaries, identity
mismatches, malformed replies, lowered limits, and cancellation. Pure tests
exercise historical format validation, ownership, and ambiguous origins. Actual
pinned-worker tests exercise projection, true and false validation, reduced
policies, refusals, cancellation, and subsequent requests through the same
runner. These checks qualify the adapter rather than durable report publication,
HTTP delivery, preview execution, authentication, or cloud hosting.
