# Regression case HTTP boundary

The case API stores explicit sample fixtures; it does not execute a mapping or
capture production payloads. It uses the catalog's existing authenticated
identity and mutation-token/CSRF boundary. `AddWeftRegressionCases` registers
default-off options and `MapWeftRegressionCases` maps the routes explicitly.
Enabling the module requires a server-owned exact runtime selection and the
case reader/writer services. It never provisions database roles or schema.

## Routes

The mapping prefix is
`/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-cases`.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/capabilities` | Active runtime, limits, comparison policy and current sample actions |
| GET | none | Metadata page; optional `limit` and `afterCaseId` |
| GET | `/{caseId}/versions/{version}` | Exact private sample version |
| PUT | `/{caseId}/versions/{version}` | Create a version or replay the identical original request |

IDs are canonical lowercase UUIDs. Versions are positive decimal strings with
no leading zeros, so JavaScript does not need to represent a 64-bit version as
a number. Pages contain at most 100 entries and seek after an exact case UUID.
Unknown or duplicate query parameters are rejected.

Viewers may list metadata. Reading sample content requires Runner or above;
publishing requires Publisher or Administrator. These actions do not grant
production-payload access. Disabled routes return 404 without reading the body
or resolving private stores. Every response is `Cache-Control: no-store`.

## Publication body

PUT requires `Content-Type: application/vnd.weft.regression-case.v1+json`, with
an optional UTF-8 charset. Content encodings are not accepted. The complete body
is bounded to 24 MiB, with an independent body-read deadline. The raw sample
limits are 8 MiB combined input and 8 MiB combined expected output; a request
may select lower execution limits.

The exact required fields are:

- `name`, `revisionId` and `currentDateTime`.
- `runtime`: the complete runtime selection returned by capabilities. This is
  an expected selector, not authority supplied by the browser. Stale selections
  return 412; the server constructs the case with its own matching runtime.
- `target`: `{ "kind": "all" | "primary" | "named", "name": null | "exact name" }`.
- `limits`: `maximumInputBytes`, `maximumOutputBytes`, `maximumArtifacts` and
  `maximumTextBytes`.
- `primaryBase64Url`, plus `namedInputs` containing `{ "name", "bytesBase64Url" }`.
- `parameters` containing `{ "name", "kind", "value" }`. Kinds are `string`,
  `integer`, `float` or `boolean`; every value is lexical text, not a JSON number.
- `comparisonPolicy`: explicitly `weft.regression.exact-bytes.v1`, or
  `weft.regression.edi-segments.v1` for the document runtime.
- `expected`: output `{ "name", "bytesBase64Url" }` entries; null names identify
  the primary output. An empty baseline remains unevaluated, not passed.

When `documentRuntime` is advertised by capabilities, callers may select that
complete runtime instead of the legacy `runtime`. Document expected outputs
require `mediaType`: `application/json`, `application/edi-x12` or
`application/edifact`. Named input descriptors remain unchanged: the native
mapping declares their formats. The EDI segment policy requires
`comparisonOptions` with the three booleans `compareSeparators`,
`ignoreControlNumbers` and `ignoreDates`; exact-byte comparison omits options.
Legacy runtime requests retain their original grammar without media or options.
Capabilities advertise the available `comparisonPolicies` alongside the
document runtime. No case is silently switched to a newer runtime profile.

Byte strings use canonical unpadded Base64url (`A-Z`, `a-z`, `0-9`, `-`, `_`).
Padding, whitespace and alternate spellings are rejected. This retains exact
binary inputs without JSON reserialization and avoids HTML-sensitive Base64
characters expanding during JSON response encoding. Invalid input documents or
parameter values may be stored for reproducible execution failures; storage does
not repair them or treat them as successful runs.

Unknown, duplicate and missing fields, invalid shapes and malformed Unicode are
rejected. The caller cannot supply a creator, native digest or permission grant.
The server reads the exact revision and derives its digest from trusted catalog
metadata. Version one uses the current creator; appends reload the previous
version's creator rather than accepting an ownership substitution.

## Delivery and recovery

Publication is admitted before the body is read, and authority is checked again
after parsing. The immutable version and expected output must fit the admitted
profile before the writer is called. A successful receipt is checked against
the complete submitted envelope. A new version returns 201; an exact replay
returns 200; a different payload for an existing version returns 409.

An error or lost response is not permission to invent another case/version or
silently resubmit a different body. Retain the original request for explicit
retry, or read the exact version. Publication may already have committed even
if final delivery fails or current access is subsequently revoked.

Private reads bind the case, version, mapping, native digest and revision before
serialization, then recheck current sample authority before delivering the
bounded response. Failures return generic catalog errors rather than SQL text,
credentials, parser details or sample excerpts. All worker execution, regression
run history, and actual host/migration activation remain separate boundaries.
