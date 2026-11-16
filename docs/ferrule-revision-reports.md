# Trusted Ferrule revision reports

The restricted Ferrule worker supplies braid's revision-report contracts through
the [stored report codec](revision-report-codec.md).

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

Stored-format checks bind the report to its own key and provenance.
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

## Observations and links

Normalization preserves actual status, confirmed provenance, typed native
owners, exact paths, and full versus emitted diagnostic counts. False validity
with `InvalidMapping` is a usable validation report; null validity is not.
Cleanup failure retains `IsolationFailure` even when cancellation also occurs.
The report adapter categorizes native decode refusal as `UnsupportedArtifact`
and digest mismatch as `ProtocolFailure`.

`ProjectionOriginLinks` builds an owned, cancellation-aware index from actual
emitted origins. Duplicate evidence for the same owner is harmless; origins
shared by distinct owners produce no row link. Broad document evidence and
empty pointers also produce no row link. Nested unsupported details retain
their evidence. Persisted callers use a verified report.

## Verification

Synthetic protocol tests exercise exact admission boundaries, identity
mismatches, malformed replies, lowered limits, and cancellation. Pure tests
exercise stored-format validation, ownership, and ambiguous origins. Actual
pinned-worker tests exercise projection, true and false validation, reduced
policies, refusals, cancellation, and subsequent requests through the same
runner.
