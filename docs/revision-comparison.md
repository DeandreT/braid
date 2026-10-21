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

## Browser view

The catalog route
`/catalog/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/comparisons`
compares two revisions of one mapping. The mapping page opens it with the
current head on the right, and each revision page opens it with that revision
on the right; the user chooses the other side from the mapping's paged
revision list. Both links appear only when report reading is available for the
mapping.

The browser query is closed: `left` and `right` name revisions, the optional
`leftProjection`, `leftValidation`, `rightProjection` and `rightValidation` pin
exact saved reports, and `cursor` pages the revision list. Each name may appear
once, a pin requires its side's revision, and any other query is an invalid
link. These names are browser selectors, never passed through to the API.

Each side reads its saved projection and validation through the revision
report client and state, exactly as its revision page would, including the
explicit link of validation to the displayed projection. The view shows each
side's report derivation, generation and save time before any result. A side
without a readable saved explanation (not generated, in progress, failed,
unreadable payload or refused) states that and links to that revision's report
controls; the view never generates, repairs or polls a report. A side without a
saved validation is compared without one. The comparison request starts only
after both sides settle with an exact saved explanation, cites exactly those
derivation IDs, and is replaced and cancelled whenever the route, sides,
session or catalog admission changes. Responses are verified strictly against
the requested revisions and derivations before anything is shown.

Results are grouped by entity. Each item shows its match state, the candidates
of ambiguous items from both sides with any omitted count, and per-aspect
facets that separate observed differences from uncertainty and its reasons.
Matched items with no observed difference or uncertainty are hidden by default
and counted; nothing else is hidden. Report-local node IDs are resolved only
against the saved explanation of their own side, and each candidate opens that
side's explanation in the mapping viewer. Coverage, omitted items or
candidates, uncertainty and missing validation are shown as banners. A
statement that no differences were observed appears only when the result has
no uncertainty, no truncation and complete coverage on both sides; otherwise
the view says the evidence is incomplete. The view always states that matching
explanations are not evidence of equivalent execution.

Every result cites both revisions, their native digests, report derivations,
generations and projection provenance, with links to each revision pinned to
the cited reports and a link to the same exact comparison. Comparison failures
are categorical: missing or refused reports ask for a catalog refresh; busy,
unavailable storage, timeouts and unverifiable responses offer a retry; and the
resource limit is final for those reports. Arrow keys, Home and End move between items, group
buttons move focus to each group, and the side panels stack on narrow
viewports.

## Limits

`RevisionComparisonLimits` bounds input items, charged work units, emitted items,
candidates per side and output UTF-8 bytes. Ambiguous candidates are grouped, not
cross-multiplied. Exceeding a limit fails with `RevisionComparisonException`
(`ResourceLimit`); invalid or mismatched sides fail with `InvalidInput`. Omitted
candidates or items are flagged in the result. Cancellation is cooperative
through the supplied token.
