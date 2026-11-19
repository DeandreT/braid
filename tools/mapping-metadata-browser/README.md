# Mapping metadata browser fixture

This Bun fixture tests the mapping metadata editor against the packaged
host. It imports the shared browser-fixture runtime. All metadata PATCH requests
originate in the browser.

Build Angular and publish Server, DatabaseTool and StorageTool from the same
committed source. Bind every source body/mode and published file/asset by SHA256.
Private configuration/release/manifest/ledger stay0600 in an owned0700 directory
outside repositories and publications. Run the browser on an isolated display.

Reuse `../catalog-browser/observe-host.py` unchanged, in an exclusive worker
window. Open its real GET200 event admission BEFORE the operator; bind that file
and fixture UUID into private configuration. Send its `finish` only after operator
children, automatic worker cleanup and owned PG/network/volume teardown settle.
It must capture worker profiles/destruction and exact404 evidence.

Use the catalog fixture's documented configuration and release fields, with
schema prefixes `braid.mapping-metadata-browser-operator-config.v1` and
`braid.mapping-metadata-browser-release.v1`. Require the same immutable worker/PG
pins and exact daemon. Its source manifest must include this operator and shared
runtime; `operatorSha256` binds this entrypoint. All three ordinary publication
maps are required. Choose two distinct concrete loopback origins once; no fallback
or retarget. Host-side variables are `BRAID_MAPPING_METADATA_BROWSER_OPERATOR`,
`BRAID_MAPPING_METADATA_BROWSER_OPERATOR_CONFIG`,
`BRAID_MAPPING_METADATA_BROWSER_MANIFEST`, `BRAID_MAPPING_METADATA_BASE_URL`.
The last two equal private config. The browser owner spawns
`bun <absoluteOperator> --config <privateConfig>` with private pipes.

Setup provisions Administrator/Publisher/Runner/Viewer project roles,
initializes strict v2 storage and uploads a primary basic mapping and
an unrelated versioned-exact mapping through product HTTP/native workers. It
creates one actual saved projection and validation via ordinary Ensure, verifies
usable payloads and Bound publications, and settles the private setup host and
workers before the browser begins. Source fixture checksums are fixed in the
operator.

The read/edit API receives ONLY catalog-reader and restricted metadata-editor
credentials. Metadata editing is independently enabled; creator/ingestion/reports/
derivation/maintenance, private-root and worker/daemon environment are absent.
Thus real PATCH does not require native writer/storage configuration. FeatureOff
mode has catalog only, no editor credential. Publisher/Admin are real authorized
actors; Viewer/Runner can read while EditMetadata remains denied. Native setup
and the one explicit append use a separate private mutation host.

The public atomic immutable manifest is
`braid.mapping-metadata-browser-fixture.v1`, with fixture/source/origin/organization,
initial profile `publisher`, four profiles (actorPrincipalId,
allowedOrganizationId,primaryRole), project{scope}, primary{scope,initialRevision},
unrelated{scope}, reports{projectionDerivationId,validationDerivationId}.
Primary/unrelated scope is the three-field CatalogMappingScope; initialRevision
is the complete actual CatalogRevision. Result IDs are public report identities,
not private object/lease references. Metadata and its actual strong tag are read
through ordinary GET, never synthesized from manifest or ordinary mapping state.
Ready is `braid.mapping-metadata-browser-ready.v1`, fixtureId,manifestSha256,profile.

Every control is a serialized closed JSON envelope
`{requestNonce,fixtureId,action,...}`. Canonical UUID nonces replay identical ACKs;
changed content refuses. Limits remain1024-byte commands,4096-byte ACKs,64 nonces.
ACK base is schemaVersion `braid.mapping-metadata-browser-control.v1`, requestNonce,
fixtureId,action. Exact actions/additional fields:

- api-stop: outcome stopped|alreadyStopped.
- api-start/restart-profile(profile,mode): four manifest roles, mode edit|featureOff;
  outcome ready, same profile/mode, contextStatus200.
- checkpoint: outcome observed, state{version,headRevisionId,headSequence},
  counts{revisions,reportAttempts,reportResults,workersCreated}.
- capture-edit(previousVersion,version,nameSha256,descriptionSha256): outcome
  captured, version, changed boolean. Versions are canonical positive decimal
  strings; hashes are lowercase SHA256 of the browser-verified accepted name and
  exact description UTF8 bytes. Never send raw draft, body, tag or token.
- append-primary-revision: outcome appended, appendedRevision actual public DTO.
- revoke-publisher/restore-publisher: outcome revoked|restored; only the captured
  publisher membership role/version changes, active remains true.
- pg-normal-restart: outcome restarted; stop both hosts/settle workers, normally
  restart the same exact PG and reopen the same read profile/mode.
- shutdown: outcome closed only after strict owned settlement/cleanup.

Failures add outcome failed|refused and only failure from invalidControl,
ownershipChanged,deadline,productFailure,stateMismatch,cleanupFailure.
Setup300s; API stop/start/restart15/45/60s; checkpoint/capture15s; role changes10s;
append/PG restart90s; shutdown120s. Browser PATCH uses the transport's30s wait;
backend and worker deadlines are unchanged. No hidden retries or broad cleanup.

The complete trusted ready baseline includes both mappings, native/revision/
report/publication facts, every other row, roles/password digests, database/schema/
relation+column/routine/default ACL evidence, migration journal and private inode/
mode/byte identities. Checkpoints, pre-control admission and cleanup require exact
equality; they do not adopt unacknowledged edits or compare counts alone.

After EACH actual200 PATCH, the browser fixture privately validates actual
scope/body/strong ETag and accepted strings, then awaits capture-edit BEFORE any
next owner action/checkpoint. The operator checks DB text hashes and exact version,
then permits only primary name/description/version/updated_at. Changed text must
advance once, preserve every other target field including head, and have an actual
PG-precision nondecreasing timestamp. The full snapshot with only that exact
mapping excluded must equal the prior baseline. A no-op requires the ENTIRE
snapshot/version/timestamp unchanged. Only validated actual state advances trust;
no generic normalization hides row reordering or unrelated changes. All412/404
refusals and GET/focus reads have no capture and no durable delta.

The one explicit append is independently admitted: strict trusted-before equality,
real equal-head HTTP native append with different pinned bytes, one actual worker,
exact new revision/ingestion/attempt/Bound publication, and only head/sequence/time
on the primary mapping. Actual metadata body+ETag/name/description/version remain
identical. Every old row/report/file remains exact; new files are restricted to
the one returned native blob, object gate and that publication's temp gate/four
complete seals. No extra stage, fence, unrelated private file or row is admitted.
Subsequent PATCH must preserve this new head too.

The fixed publisher-role owner seam accepts no arbitrary scope/principal/SQL.
It requires the originally captured active membership and exact prior role/version,
applies Publisher↔Viewer/version+1, and compares all unrelated rows. The scenario
also compares the entire full snapshot minus only that expected membership row,
including credentials/ACLs/journal/files, before advancing.

Two browser contexts loaded at one actual tag prove the second CAS412, retained
draft and explicit Review latest/resubmit; no automatic PATCH. For final authority
recheck, the fixture holds the actual request before forwarding after fresh admission,
commits the fixed role change, then observes the real opaque404/no mutation. For
response loss, the fixture forwards the PATCH once, validates its completed200, captures
that exact delta, THEN aborts delivery to the page. The page stays uncertain and
reconciles through deliberate GET only; matching current values are not an edit
acknowledgement or receipt. This is browser delivery loss, not DB COMMIT-ACK loss.

Normal API/same-volume PG restart must preserve the full baseline and original
revision link. Controlled boundary cases use mocked responses and no owner
controls.
