# Build control HTTP API

The default-off build module exposes retained metadata and approved queue
operations. It does not inspect or compile a mapping, derive reports, discover
machine work, publish artifacts or deploy an endpoint from an HTTP request.

## Routes

Project routes use `/api/v1/organizations/{organizationId}/projects/{projectId}`.
Mapping routes append `/mappings/{mappingId}`.

| Method and suffix | Operation |
| --- | --- |
| `GET /build-environments` on a project | Bounded retained environment versions, including inactive versions. |
| `GET /build-environments/{environmentId}/versions/{version}` on a project | One exact retained version. |
| `GET /build-capabilities` on a mapping | Trusted runtime selection and current publication rights, not compilation capability evidence. |
| `GET /build-approvals` on a mapping | Bounded own approval metadata. |
| `GET /build-approvals/{approvalId}` on a mapping | Recover the original own approval, including revoked or inactive status. |
| `PUT /build-approvals/{approvalId}` on a mapping | Create or recover an immutable approval. |
| `GET /build-runs` on a mapping | Bounded own run history. |
| `GET /build-runs/{buildId}` on a mapping | Exact retained state, attempts and artifact metadata. |
| `PUT /build-runs/{buildId}` on a mapping | Submit the stored approval intent or recover the existing run. |
| `POST /build-runs/{buildId}/cancel` on a mapping | Empty-body cancellation; returns the actual retained state. |

All UUIDs are canonical nonzero lowercase `D` strings. Environment and profile
versions are positive canonical decimal strings. Lists default to 50 and permit
at most 100 records. Environment cursors include `afterEnvironmentId` and
`afterVersion` together. Approval/run cursors use `afterApprovalId`/`afterBuildId`
and must belong to the same actor and mapping. Byte-budgeted pages may be shorter
than the requested count and retain a continuation cursor.

## Immutable requests and recovery

Approval PUT uses `application/vnd.weft.build-approval.v1+json`:

```json
{
  "buildId": "<uuid>",
  "revisionId": "<uuid>",
  "environmentId": "<uuid>",
  "environmentVersion": "1",
  "regressionEvidenceId": "<uuid>"
}
```

The server supplies the native digest, backend/image/runtime identity, artifact
profile and physical boundary requirements. It uses exact authorized revision
metadata, the selected environment version, recomputed saved regression evidence
and a complete verified stored projection. Missing, partial or incompatible
facts fail the precondition; no hidden worker or report derivation fills a gap.
The build worker still performs its own actual compiler capability/preflight.

Recovery reads a retained own approval before consulting current configuration.
The same selectors return its original intent even after an image change,
approval revocation or environment replacement. Different selectors conflict.
Recovery never renews approval or selects a replacement environment/runtime.

Run PUT uses `application/vnd.weft.build-run.v1+json`:

```json
{ "approvalId": "<uuid>" }
```

The route's build ID must match that approval's stored intent. Existing own runs
are recovered before active-admission checks. New submissions use the original
stored canonical intent and require fresh current authority, approval, evidence
and runtime compatibility. Exact retries do not reset attempts or terminal state.

Clients retain the original URL, IDs, media type and body through an uncertain
mutation. Explicit GET or identical PUT recovers it; an unconfirmed operation must
not generate another ID/body automatically. Identity changes clear private
recovery state. An `uncertain` run is not success, even when artifact metadata is
retained. No response includes native bytes, artifact bodies or worker tokens.

## Admission and host composition

Read and write routes require current publisher/administrator authority and own
approval/run ownership, including for administrators. Session/origin/CSRF framing
is admitted before body reads or build-service resolution. Authority and full
scope are checked again before response delivery. Responses are `no-store` with
no ETag and closed error categories.

`WEFT_BUILDS_ENABLED=true` requires enabled catalog, the exact immutable
`WEFT_FERRULE_WORKER_IMAGE` and the [private storage selection](private-storage-blob.md#host-selection)
(a canonical `WEFT_PRIVATE_STORAGE_ROOT`, or the Azure Blob settings) for
authorized stored-report reads. Session startup snapshots those scalars before
opening protected resources. Disabled mode delivers no build credentials or
services. Enabled composition uses these independent connection inputs:

| Input | Database role |
| --- | --- |
| `WEFT_BUILD_METADATA_CONNECTION_STRING` | `weft_build_metadata_reader` |
| `WEFT_BUILD_CONTROL_CONNECTION_STRING` | `weft_build_writer` |
| `WEFT_BUILD_ACCESS_CONNECTION_STRING` | `weft_runtime` |
| `WEFT_BUILD_REPORT_CONNECTION_STRING` | `weft_report_reader` |
| `WEFT_BUILD_CASE_READER_CONNECTION_STRING` | `weft_case_reader` |
| `WEFT_BUILD_EVIDENCE_READER_CONNECTION_STRING` | `weft_run_reader` |
| `WEFT_BUILD_RUN_READER_CONNECTION_STRING` | `weft_build_run_reader` |
| `WEFT_BUILD_RUN_WRITER_CONNECTION_STRING` | `weft_build_run_writer` |

Session mode reads the corresponding `_FILE` deliveries through the protected
connection loader. The control role retains its existing database publication
privileges; HTTP exposes only guarded approval operations, not publication
services. It receives no machine dispatch credential or claim capability.

Migration 0020 adds the dedicated read-only metadata role, narrow query function
and history indexes. Exact function/schema guards admit the new contract without
changing earlier migration bodies. Existing local installations explicitly run
`deploy/local/postgres-add-build-metadata-role.sh` as an administrator before
migrating. Applications never install schema or credentials themselves.

## Environment administration and approval revocation

`GET /build-environment-capabilities` on a project returns the server's selectable
runtime profile and current administrator capability. It does not test compilation
or authorize a later mutation.

Administrators create the next sequential version with
`PUT /build-environments/{environmentId}/versions/{version}` and
`application/vnd.weft.build-environment.v1+json`:

```json
{ "profileId": "<server-offered-profile-id>", "profileVersion": "1" }
```

The server supplies the runtime, runtime version, format and artifact limit. New
version creation atomically supersedes the old version. An identical retained
version request recovers its original full profile even when the configured
profile changes. Recovery never reactivates a superseded version. Changed
selectors conflict; an unavailable new profile selection fails the precondition.

Publishers can explicitly revoke their own approvals using empty-body
`POST /build-approvals/{approvalId}/revoke` on the mapping. Repeating the request
returns retained revoked metadata; it cannot renew execution admission. The
underlying administrative database privilege does not expose another actor's
approval through this own-record HTTP route. Neither administration operation
starts a worker, deploys a target or downloads an artifact.
