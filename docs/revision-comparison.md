# Revision comparison

`RevisionComparisonService.Compare` compares the explanations of two exact
revisions of the same mapping. It is pure: callers supply both sides, built with
`RevisionComparisonSide.Create` from a catalog revision summary and its verified
projection and validation reports, and receive a versioned `RevisionComparisonV1`.
It performs no I/O, authorization, derivation or execution.

## Correspondence

Native and projected node IDs are local to one report, so equal IDs never pair
facts across revisions. Items pair only through deterministic, unique structural
anchors such as typed field paths and parameter names. Duplicate anchors and
multiple producers yield an `Ambiguous` item listing bounded candidates from both
sides. A renamed field or parameter appears as `OnlyLeft` and `OnlyRight`. List
position, fuzzy text and greedy matching are never used. Case, spaces, Unicode,
typed path distinctions, value kinds and numeric representations are compared
exactly.

## Results

Each item names its entity (boundary, field, parameter, rule, control,
relationship or validation), its match and per-aspect facets. Aspects separate
value and default dependencies, conditions, controls, type, cardinality,
requirement and nullability from presentation-only changes, so a moved label is
not reported as a behavior change. Unsupported operations or profile
combinations stay unknown.

Every result cites both exact revisions and their report derivations, with
independent coverage for each side. Partial, truncated or unknown evidence is
reported as uncertainty: it never implies absence, equality, resolved
diagnostics or a complete "no changes" result. A comparison of explanations is
not evidence that the two revisions execute equivalently.

## HTTP read

`GET /api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/comparisons`
compares two exact retained reports. `leftRevision`, `leftProjection`,
`rightRevision` and `rightProjection` are required; `leftValidation` and
`rightValidation` are optional and are linked through their side's projection.
Every value is a canonical lowercase UUID naming an immutable successful
derivation. Each name may appear once and no other query is accepted.

The route uses the persisted-report read feature, admission and request framing
(see [Persisted revision-report API](persisted-report-api.md)). It requires fresh
`ReadMetadata` authority for both revisions and never derives, repairs or starts a
worker. Missing revisions, foreign scopes and unavailable derivations are opaque
404s; busy report reads return 429 and unavailable report storage returns 503. A
comparison that exceeds its limits returns the categorical 503 resource-limit
error. Both revisions are checked again immediately before the response, which is
`no-store` and has no ETag.

## Limits

`RevisionComparisonLimits` bounds input items, charged work units, emitted items,
candidates per side and output UTF-8 bytes. Ambiguous candidates are grouped, not
cross-multiplied. Exceeding a limit fails with `RevisionComparisonException`
(`ResourceLimit`); invalid or mismatched sides fail with `InvalidInput`. Omitted
candidates or items are flagged in the result. Cancellation is cooperative
through the supplied token.
