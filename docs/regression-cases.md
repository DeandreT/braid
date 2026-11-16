# Immutable regression case versions

`RegressionCaseVersion` and `RegressionCaseEnvelopeCodec` define the private
stored content of one explicit case version.

## Identity and exact replay

A version binds its case ID, positive version, owner, display name, complete
mapping revision, native digest, and exact backend/engine/adapter/profile
identity. It includes all requested target, input, parameter, evaluation-clock,
execution-limit and expected-output facts. The initial profile is
`braid.ferrule-json-preview.v1` and its explicit comparison policy is
`braid.regression.exact-bytes.v1`; unknown policies/profiles are rejected, not
silently replaced.

Input and expected bytes are preserved exactly. This layer does not parse,
repair or reserialize payload JSON. Invalid payload or parameter values can be
retained for reproducible failures; the runtime still validates them and the
comparison policy never passes an unsuccessful execution. Parameter kinds are
closed and lexical values remain strings, including integers too large for
JavaScript's exact numeric range. Primary and named identities are distinct.
Names are case-sensitive, are not normalized, and are never paths.

The clock is a required fixed ISO date-time with explicit offset and up to nine
fractional digits. Its original spelling is retained, not converted to the
machine's local timezone or current time. It affects mapping evaluation only:
authorization, leases, cancellation and retention use their independent live
clocks. A fixed clock does not seed random or GUID-generating mapping functions;
such outputs can still fail exact comparison. `CreatePreviewRequest` requires an exact native scope/digest and runtime match.

## Private storage format

`braid.regression-case.v1` contains a four-byte big-endian header length, a strict
UTF-8 JSON header, then four-byte-length-prefixed raw frames: primary input,
ordinally sorted named inputs, then primary-first/ordinal expected outputs.
Parameters are also sorted ordinally. Equivalent caller list ordering produces
identical stored bytes. Every header object has a closed required field set;
duplicate/unknown/missing fields and noncanonical numeric or identity tokens
are rejected. Input/output descriptors include exact length and SHA-256.

The complete envelope digest, byte count, owner, case/version, revision and
native digest must match the independently loaded `RegressionCaseReference`.
The decoder owns the bounded input before digest verification, verifies every
frame and rejects trailing bytes. Limits are 256 KiB for the header, the preview
profile's 8 MiB each for combined inputs and outputs, 16 inputs and 64 outputs.
Caller-selected execution limits can be lower. There is no Base64 expansion.

The envelope and returned byte copies are private payload material. Do not log
them or put them in model context. Diagnostics omit content and inner parser
exceptions. Header limits are checked independently, so heavily escaped
parameter text can reach the header ceiling before its per-value limit.

## Persistence and execution boundary

A store must assign/check versions atomically, bind each private object
reference to the same owner/revision, authorize fresh access before reads and
writes, and retain immutable successful versions. A matching digest is not an
access token. Upload/admission layers must check request sizes before allocating
the preview document objects consumed by this module.

Expected outputs are an explicit user-selected baseline, not the latest preview
result. An empty baseline remains `NotEvaluated` under the comparison policy.
Case storage and durable runs retain these exact identities and failure boundaries.
