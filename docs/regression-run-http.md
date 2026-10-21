# Regression run HTTP API

The run API operates on explicit saved-case fixtures. It never captures a
production payload and never executes the mapping in the request process.
The separate regression worker discovers committed run rows.

## Routes and identity

The mapping prefix is
`/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/regression-runs`.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | none | Current submitter's bounded metadata history |
| GET | `/{runId}` | Status and retained exact outputs/comparison |
| PUT | `/{runId}` | Queue one saved-case version, or replay its original request |
| POST | `/{runId}/cancel` | Cancel without replacing a terminal result |

All routes use the existing catalog session/development admission boundary.
Mutations require the existing CSRF token and admitted origin. Current
Runner-or-higher access is required; saved results remain submitter-only.
All responses are `Cache-Control: no-store` and omit ETags.

IDs are canonical lowercase UUIDs. Versions are positive decimal strings, not
JavaScript numbers. PUT accepts only `caseId` and `version`, using
`application/vnd.weft.regression-run.v1+json` and at most 2 KiB. The server
loads the immutable case and derives its complete reference; the caller cannot
substitute a digest, creator, runtime or native revision in the run request.

New runs must match the configured exact runtime. An existing identical run
can still be recovered after the active runtime changes. PUT returns 201 for a
new run, 200 for an identical replay, 409 for a conflicting use of the run ID,
412 for an unavailable runtime selection, or 429 when active admission is full.
A lost response does not imply failure: keep the original ID and body for an
explicit retry, or read that ID. Do not automatically create a second run.

History accepts `limit` (1–100, default 50) and `afterRunId`. It is ordered by
creation time descending with a run-ID tie breaker. The cursor refers to a run
in the same mapping and submitter's history. Metadata pages contain no sample
or output content. Unknown or duplicate query parameters are rejected.

Exact-result reads recompute comparison from the retained bytes and original
case, then recheck current access before delivery. Binary outputs use canonical
unpadded Base64url. A queued, running, canceled or exhausted run has no invented
result; a completed unsuccessful execution never becomes a passing comparison.

## Server composition

`WEFT_REGRESSIONS_ENABLED=true` explicitly enables case and run APIs. It requires
an enabled catalog, the canonical `WEFT_FERRULE_WORKER_IMAGE` runtime selection,
and four separate database profiles:

- `WEFT_REGRESSION_CASE_READER_CONNECTION_STRING`
- `WEFT_REGRESSION_CASE_WRITER_CONNECTION_STRING`
- `WEFT_REGRESSION_RUN_READER_CONNECTION_STRING`
- `WEFT_REGRESSION_RUN_WRITER_CONNECTION_STRING`

The Session host uses each variable's `_FILE` form and the existing protected
connection-delivery checks. Development uses the explicit connection-string
inputs. Disabled mode reads none of those inputs. The host does not install
schema, provision credentials, or start regression execution implicitly.

Case routes are mapped by `MapWeftRegressionCases`; run routes are mapped by
`MapWeftRegressionRuns`. The ordinary and Session server entrypoints compose
both. Worker enablement remains independent; see
[durable regression runs](regression-runs.md).
