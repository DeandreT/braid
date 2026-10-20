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

## Limits

`RevisionComparisonLimits` bounds input items, charged work units, emitted items,
candidates per side and output UTF-8 bytes. Ambiguous candidates are grouped, not
cross-multiplied. Exceeding a limit fails with `RevisionComparisonException`
(`ResourceLimit`); invalid or mismatched sides fail with `InvalidInput`. Omitted
candidates or items are flagged in the result. Cancellation is cooperative
through the supplied token.
