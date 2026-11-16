# Build control HTTP API

The default-off build module exposes retained metadata and approved queue
operations. A separate worker compiles queued builds. Optional CSV build
approval performs read-only native boundary inspection as described below.

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

Approval PUT uses `application/vnd.braid.build-approval.v1+json`:

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
and a complete verified stored projection. CSV approval additionally obtains its
exact saved input/output dialects from the explicit native CSV build inspector;
projection format recognition does not admit the compiler. Missing, partial or
incompatible facts fail the precondition; no report derivation fills a gap.
The build worker still performs its own actual compiler capability/preflight.

Recovery reads a retained own approval before consulting current configuration.
The same selectors return its original intent even after an image change,
approval revocation or environment replacement. Different selectors conflict.
Recovery never renews approval or selects a replacement environment/runtime.

Run PUT uses `application/vnd.braid.build-run.v1+json`:

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

`BRAID_BUILDS_ENABLED=true` requires enabled catalog, the exact immutable
`BRAID_FERRULE_WORKER_IMAGE` and the [private storage selection](private-storage-blob.md#host-selection)
(a canonical `BRAID_PRIVATE_STORAGE_ROOT`, or the Azure Blob settings) for
authorized stored-report reads. Session startup snapshots those scalars before
opening protected resources. Disabled mode delivers no build credentials or
services. Enabled composition uses these independent connection inputs:

| Input | Database role |
| --- | --- |
| `BRAID_BUILD_METADATA_CONNECTION_STRING` | `braid_build_metadata_reader` |
| `BRAID_BUILD_CONTROL_CONNECTION_STRING` | `braid_build_writer` |
| `BRAID_BUILD_ACCESS_CONNECTION_STRING` | `braid_runtime` |
| `BRAID_BUILD_REPORT_CONNECTION_STRING` | `braid_report_reader` |
| `BRAID_BUILD_CASE_READER_CONNECTION_STRING` | `braid_case_reader` |
| `BRAID_BUILD_EVIDENCE_READER_CONNECTION_STRING` | `braid_run_reader` |
| `BRAID_BUILD_RUN_READER_CONNECTION_STRING` | `braid_build_run_reader` |
| `BRAID_BUILD_RUN_WRITER_CONNECTION_STRING` | `braid_build_run_writer` |

Session mode reads the corresponding `_FILE` deliveries through the protected
connection loader. The control role retains its existing database publication
privileges; HTTP exposes only guarded approval operations, not publication
services. It receives no machine dispatch credential or claim capability.

### Optional CSV build registration

`BRAID_BUILDS_CSV_ENABLED=true` separately enables the
`braid.ferrule-csharp-csv.v1` profile and `FerruleCsvBuildBoundaryReader`. It is off
when absent or `false`; an unsupported value refuses configuration. It does not
change the existing JSON/EDI profiles or turn on the build module itself.

CSV inspection uses the selected `BRAID_FERRULE_RUNNER`: Docker in development,
or the Ferrule service or Azure dynamic sessions. The inspection-worker-only
transport does not serve compiler frames and is refused for CSV. Session hosts
remain remote-only. Service/Sessions selection requires the exact
`BRAID_FERRULE_COMPILE_TOOLCHAIN` pin as well as the native worker identity; CSV
approval, regression provenance and queued builds must share their combined
compiler identity.

The server registers a lazy compiler-profile reader, not a compiler execution
queue. Reading capabilities or recovering retained approvals does not open a
certificate, connect storage or start a child. An authorized new CSV approval
reads the exact private native revision and runs only the build protocol's
`inspect` operation. It validates both primary boundaries, including dialects,
before forming the immutable intent. Named and mixed plans refuse admission.
New CSV run submission still requires the configured CSV backend/profile and
fresh approval, evidence and project authority.

If native inspection already owns the selected service/Sessions connector, the
CSV reader borrows it. Otherwise it owns a connector with the same protected
certificate delivery or managed-identity settings. Shutdown joins its runner
before releasing any owned connector or certificate; stopped readers do not
start new work.

## Environment administration and approval revocation

`GET /build-environment-capabilities` on a project returns the server's selectable
runtime profile and current administrator capability. Mutations recheck current
authority.

Administrators create the next sequential version with
`PUT /build-environments/{environmentId}/versions/{version}` and
`application/vnd.braid.build-environment.v1+json`:

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
approval through this own-record HTTP route.
