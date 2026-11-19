# Native revision append browser fixture

`native-revision-append-persistence.spec.ts` uses the automatic
`nativeRevisionAppendOwner` to test append persistence against packaged hosts and
the native worker. Controlled-response tests use a separate owner-free fixture.

## Setup

The packaged same-origin browser calls the C# API, restricted PostgreSQL catalog/ingestion roles, owner-initialized v2 private storage, and pinned Ferrule validation worker. A separate fixture-only mutation origin creates the project, seeds two immutable native mappings, and derives one projection and one validation report for the original target revision. That host stops before the public manifest becomes ready. The sibling revision and original target revision/report rows/files stay protected throughout every accepted delta.

The browser origin has catalog-reader and ingestion authority only when append mode is enabled. It receives no project-creator, metadata-editor, report-writer, derivation, or maintenance credential. The server ingestion flag enables both global `context.features.createMappings` and `uploadRevisions`. The mapping action has `createMappings=false` and the appropriate `uploadRevisions` action. Browser mutations are restricted to the exact append path.

A fixed metadata-edit control starts a separate fixture-only administrator host with the metadata-edit flag and restricted editor role, without ingestion or Docker configuration. It reads the metadata ETag and sends one PATCH with the strong full-scope ETag. The editable metadata version advances independently while native head review remains unchanged. All append captures compare the complete metadata representation and its ETag to the accepted post-edit representation. Appends may advance `updated_at`; metadata name/description/version/ETag and all other mapping fields remain intact.

## Scenario

The serial scenario checks:

1. Viewer, Runner, and feature-off direct routes refuse native mutation and work.
2. Publisher saves exact versioned bytes after the metadata edit; Administrator saves decoded-invalid bytes at the next exact sequence.
3. Two actual browser contexts review the same head and allocate distinct real keys. The second request is held before forwarding, the first commits, and the unchanged held request receives a real terminal409 `headConflict`. It creates no revision or saved link. Explicit current-head review plus `Start a new append` and a fresh file selection create a different key and can commit. A separately labelled API GET confirms the original conflict receipt remains immutable.
4. The fixture observes an actual completed200 and independently captures its persisted delta before aborting browser delivery. The UI stays unknown. A separate genuine append advances the head, a foreign actor cannot read the original own receipt, and a normal API/same-PostgreSQL restart preserves the original exact key/query/Blob recovery. The unchanged deliberate original resend acknowledges the historical saved revision and adds no revision or native work. Explicit browser own-status GET and separately labelled bodyless terminal API resume preserve the same receipt. This is HTTP delivery loss, never a database COMMIT-ack fault.
5. Same-actor Viewer demotion retains ordinary saved-revision reading. Own receipt GET remains opaque404; access retry is GET-only. Restoring authority never sends an automatic mutation.
6. Fresh advisory revocation dispatches zero native POSTs; a separate held-before-forward final revocation receives real opaque404 with no ingestion/native work. Restored authority deliberately sends the unchanged original request once.
7. Full state, old immutable revisions, saved reports and monotonic workers survive final ordinary restarts; current mobile editor/acknowledgement/revision layouts remain bounded.

Requests retain their original key, raw bytes, query and headers while held. There is no automatic resend or network retry. Original source/key/query/token/digest comparisons stay inside the host-side fixture; failing assertions use fixed safe categories. Browser media and automatic page snapshots are disabled. File-input actions receive only the selected fixed sample, with value-bearing action errors translated to closed categories.

## Expected native work and requests

| Work                                                            | New ingestion attempts | Native revisions | Actual workers |
| --------------------------------------------------------------- | ---------------------: | ---------------: | -------------: |
| Two genuine seed uploads                                        |                      2 |                2 |              2 |
| Old projection and validation report ensures                    |                      0 |                0 |              2 |
| Browser valid, decodedInvalid, winner, renewed, lost, restored  |                      6 |                6 |              6 |
| Browser losing equal-head contender                             |                      1 |                0 |              1 |
| Separate later-head control append                              |                      1 |                1 |              1 |
| Reads, metadata edit, replay, terminal resume and revoked calls |                      0 |                0 |              0 |
| Total final state / observed worker creates                     |                     10 |                9 |             12 |

The final mapping count is2 and saved report count is2. Each native attempt contributes one genuine native publication; the loser retains staged native/evidence facts even though it has no revision. The two old report publications are additional to the ten native publications. Backend final publication checks the head only after eligible validated evidence exists. Its private SQL phase is `head_conflict`, with retained eligible attempt/admission; public phase is `headConflict` with null committed sequence/admission/URL.

The browser sends exactly9 POSTs: seven first native attempts, one unchanged lost-delivery original replay, and one final-boundary revoked404. Its explicit own receipt GET count is1. Separately labelled direct API evidence makes four operation calls, each with one fresh API context: own terminal conflict GET200, foreign actor GET404, same-actor demoted GET404, and terminal source-free resume POST200. No terminal resume/replay UI is claimed or rendered.

The setup helper's fixed seed/replay and later-head/replay requests are fixture-only and independently bounded by full state/worker subtraction. No replay/GET/read/metadata interval may create another worker. Counts never reset when a new browser context, host, role, restart or acknowledgement is observed.

## Fixed private owner protocol

Supply absolute owned0600 configuration and manifest paths in an owned0700 private directory, the packaged loopback origin, committed source/tree, immutable publications, pins, release receipt and observer resources. Use the catalog fixture's configuration/release contract with kind `native-revision-append`:

- `BRAID_NATIVE_REVISION_APPEND_BROWSER_OPERATOR`
- `BRAID_NATIVE_REVISION_APPEND_BROWSER_OPERATOR_CONFIG`
- `BRAID_NATIVE_REVISION_APPEND_BROWSER_MANIFEST`
- `BRAID_NATIVE_REVISION_APPEND_BASE_URL`
- `PLAYWRIGHT_NO_COPY_PROMPT=1` before any page

Schemas are `braid.native-revision-append-browser-{ready,fixture,control}.v1`. The public manifest contains fixed actor profiles, ordinary mapping/project scopes, the original safe head scope/sequence/admission, protected sibling scope, saved report IDs, source identity and origin. It contains no file body, raw query, key, request token or native digest. The later-head acknowledgement contains only safe head facts rather than the complete native metadata DTO.

Closed actions are `api-start` / `restart-profile` with fixed administrator/publisher/runner/viewer and append/featureOff enums; `api-stop`; `checkpoint`; `capture-append` with fixed valid/decodedInvalid/winner/loser/renewed/lost/restored sample and learned operation ID; `metadata-edit`; `advance-head`; `revoke-publisher`; `restore-publisher`; `pg-normal-restart`; and `shutdown`. No action accepts arbitrary source bytes, target/head, filename, metadata, query, key, storage path, SQL, role identity, token or host executable. Control envelopes are at most1024 bytes, acknowledgement lines at most4096 bytes, and at most64 distinct sequential commands. The operator's existing nonce receipts preserve exactly one response per nonce.

Capture/checkpoint counts have exactly eight nonnegative safe-integer properties: mappings, revisions, ingestions, ingestionAttempts, nativePublications, workersCreated, reportAttempts and reportResults. A capture is accepted only in the fixed scenario order, with one exact persisted native operation/attempt/publication and, for commits, one exact revision plus the permitted head change. PostgreSQL hashes full-precision rows before the fixture sees them, then fixed row subtraction and private publication/file/inode/device/mount identities prove that no unrelated prior state changed. Accepted full-precision hashes are captured before subtraction, checked again against the current state, and only then replace the prior trusted baseline. A checkpoint never adopts fresh state.

## Metadata control and cleanup

The private request primitive's optional `ifMatch=null` argument accepts a
non-null value only for this scenario's fixture-only metadata host: PATCH to the
canonical `/api/v1/organizations/{org}/projects/{project}/mappings/{mapping}/metadata`
path without query, with one quoted strong metadata-v1 ETag for that full scope
and a bounded positive Int64 version. It sets only If-Match. Native append
requests do not use this parameter.

Run the browser on an isolated display. Retries are zero, with one owner and one
Playwright worker. A timeout fails the run. Cleanup checks process/resource
ownership, removes only owned resources and verifies their absence through the
catalog observer. Keep first-failure and cleanup records for investigation.
