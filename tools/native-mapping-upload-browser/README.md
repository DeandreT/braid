# Native mapping upload browser fixture

This is the E fixture source. Root binds and runs it only against the accepted
committed checkout and its ordinary Server, DatabaseTool and StorageTool
publications. This source handoff is outside Git and has not been imported,
parsed, syntax checked or executed by its author. It is not qualification.

The operator imports only the shared runtime's exported function. Its fixed
`native-mapping-upload` scenario has no dynamic scenario/module/config hooks.
The existing positive catalog observer is reused unchanged. All A/B/C/D
entrypoints, protocols, default hosts, observers and scenario assertions remain
unchanged. The proposed common patch is based on frozen D runtime1118bd83; root
alone applies it after D qualifies/merges. Existing packets stay immutable.

## Private binding and host

Use the existing common private config shape with schema
`weft.native-mapping-upload-browser-operator-config.v1`, and release schema
`weft.native-mapping-upload-browser-release.v1`. Config has `runtimeReleased`
and release has `released`; both remain false until root binds/releases.
There are no default source/publication paths, IDs, origins or credentials.
The exact existing config/source/manifest/admission/three-publication closure
maps, hash-bound outer observer admission, fixed worker/PG images, owned0700
private directory and0600 config/release requirements apply unchanged.

Spawn with Node `[operator, '--config', configuration]`. Root/browser env is:

- `WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_OPERATOR`: absolute reviewed operator.
- `WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_OPERATOR_CONFIG`: private0600 config.
- `WEFT_NATIVE_MAPPING_UPLOAD_BROWSER_MANIFEST`: exact private manifest path.
- `WEFT_NATIVE_MAPPING_UPLOAD_BASE_URL`: exact concrete read origin.

The source-only candidate has no actual config/release/client/runtime binding.
The unchanged worker image is
`sha256:247f7e82f965ced7f6dcde5970d3dc3c584c6f648c9d4f359a9bb5b6ed3985a3`;
the PostgreSQL16.15 reference is
`postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc`.
The runtime pins the actual images, daemon, profiles, loopback origin/PG port,
owned network/volume identities and all automatic native-worker cleanup.

Setup genuinely creates two projects and one protected sibling native revision
through ordinary HTTP, with owner/bootstrap tools and an initialized strict v2
root. The setup creator host stops before browser readiness. The browser upload
host holds only restricted catalog-reader and ingest credentials plus private
storage and pinned native worker. Creator, metadata editor, report reader,
deriver and maintenance credentials/flags are absent/off. `featureOff` holds
only the catalog reader: no ingestion/private-root/worker/Docker settings.
The project endpoint exposes createMappings only when enabled and role permits;
its mapping-specific uploadRevisions action remains false as the product maps
it, even though context reports the ingestion feature. E sends no append.

## Protocol

Public manifest `weft.native-mapping-upload-browser-fixture.v1` contains exactly
fixtureId, source{commit,tree}, readOrigin, initialReadProfile publisher,
organizationId, profiles for administrator/publisher/runner/viewer with
actorPrincipalId/allowedOrganizationId/primaryRole, project{scope}, and
protected{project,revision}. It contains no input path, native bytes/digest,
query/draft/key/token, private object reference or database configuration.
The ready line `weft.native-mapping-upload-browser-ready.v1` contains only
fixtureId, manifestSha256 and profile publisher. Manifest is bounded65536bytes.

Controls are serialized, <=1024bytes and64 canonical nonzero UUID nonces, with
fixtureId/requestNonce/action. ACK schema is
`weft.native-mapping-upload-browser-control.v1`, <=4096bytes with exact keys.
The accepted typed seam07bbdf10 remains authoritative:

| Action | Additional input | Successful ACK fields after base |
| --- | --- | --- |
| api-stop | none | outcome stopped/alreadyStopped |
| api-start, restart-profile | profile, mode upload/featureOff | outcome ready, same profile/mode, contextStatus200 |
| checkpoint | none | outcome observed, counts |
| capture-upload | closed sample, operationId | outcome captured, same sample/operationId, phase committed/rejected, exactNativeBytes true, counts |
| revoke-publisher, restore-publisher | none | outcome revoked/restored |
| pg-normal-restart | none | outcome restarted |
| shutdown | none | outcome closed |

Failure ACK uses failed/refused plus only a closed failure category:
invalidControl, ownershipChanged, deadline, productFailure, stateMismatch or
cleanupFailure. No arbitrary path/SQL/actor/project/query/body/key/token/native
digest is accepted. The fixed captured Publisher membership is the only owner
mutation seam. API profile/mode controls do not perform uploads or recovery.
Setup300s; stop15s; start45s; profile60s; checkpoint/capture15s; role10s;
PG90s; shutdown120s; browser adapter adds5s pipe margin. Existing server/worker
limits stay unchanged. There are no retries, pending/expiry rewrites or faults.

`counts` are GLOBAL owned-database totals: mappings, revisions, ingestions,
ingestionAttempts, nativePublications, workersCreated. The protected real seed
is included; callers capture initial baseline rather than assume zero. Counts
summarize full comparisons; they are never the sole preservation proof.

## Exact five samples and capture

Corrected accepted matrix samples22876da6/captureb28ba504 is mirrored in source:
legacy2191bytes is Administrator; versioned1566bytes, decodedInvalid988bytes,
malformed16bytes and unsupported1568bytes are Publisher. All principals/project
IDs derive from the current admitted manifest. Metadata/filenames are fixed
source literals; legacy description uses LF, and accepted name uses .NET NEL
trim. Original fixtures are read as raw bytes. The authored malformed literal
and exact single ASCII version2->999 replacement are pinned by length/SHA;
there is no native JSON parse/stringify/normalization. Versioned owns the real
completed200 delivery loss, original resend and API/PG durability scenario.

The browser does every original POST and deliberate resend with its private
frozen key/query/Blob. The operator never receives or submits those wire facts.
It accepts capture-upload once per sample, with an operationId learned from the
actual browser response, including capture BEFORE simulated response delivery
loss. A replay uses checkpoint and cannot advance the baseline.

Post-HTTP capture subtracts exactly one actual scoped ingestion header, completed
attempt, Bound native publication and seven v2 files. Committed samples also
add exactly one mapping/revision at first head/sequence/version1; rejected
samples add none and honestly RETAIN their actual staged native artifact and
complete seals. Every actor, exact accepted metadata/display_filename, digest,
count, source scope, reserved profile/provenance and terminal phase is checked.
The header uses display_filename; the public receipt uses originalFilename.
Publication owner generation/instance/token matches its actual attempt;
owner_lease_expires_at equals attempt.started_at + interval '120 seconds' in a
fixed scoped read-only PostgreSQL comparison at full timestamp precision.
No nonexistent attempt expiry or cleared terminal header lease is substituted.

The exact new physical set is blob0400, object.gate0600, temp.gate0600 and
absence/reservation/bound/prepared0400, derived only from the captured actual
root/control namespace/scope/nativeObject/temp IDs. Immediately after Initialize,
the operator captures the owned0400/single-link90-byte v2 marker and its file
identity, requiring exact ASCII bytes (latin1 read plus reconstructed raw-byte
equality), canonical nonzero root/control IDs. Publication root_id and
control_namespace_id must match those captured IDs; the exact control path is
`.weft-private-control/{controlIdN}/objects/native/...`, not a directory scan or
a namespace inferred from a later publication. This is the separately recorded
root-authorized oracle refinement41084a2f; accepted samples22876 remain exact. Every file is owned, regular, nonsymlink,
single-link; gates are empty. Original native bytes are compared directly as
well as by digest/length. New native inode is queried as text, with actual
mount/device/data-parent identity checks; no unsafe inode Number conversion.
Absence and Bound seal hashes match durable registry facts. Full seal validity
is also provided by the unchanged checked v2 publication path before its
actual terminal response; no new Node seal decoder claims independent parsing.
No additional stage/temp/fence/path is silently excluded.

Current minus only the identified additions must equal the retained trusted
snapshot (all other rows, heads, reports, journal, roles/passwords/memberships,
object ACLs and every old file hash/inode/mode/link). A separate PostgreSQL
ordered full-row digest preserves full numeric/timestamp precision before Node
JSON conversion. Full-state hash is fixed BEFORE exclusion comparison and
rechecked before advance, so later unexplained state is not adopted. Every
other owner control/checkpoint and final cleanup starts with full equality,
including worker-create count. Fixed Publisher<->Viewer CAS advances only that
member's role and version, never facts. API and same-PG restart preserve all
state. Every terminal resend/own-receiptGET/bodylessresume/read/refusal is zero
rows/files/worker delta. Unexpected attempts/workers fail; they are not retried.

Operator cleanup PASS is resource/state settlement, not a full browser PASS or
proof all five sample cases executed. Root reviews actual raw browser outcomes,
step/case census, capture records and outer lifecycle evidence separately.
All automatic worker destruction/exact404 stays product-owned; neither the
operator nor observer removes native workers. Only exactly owned API/PG/
network/volume identities are normally settled by the inherited runtime.

## Browser ownership and privacy

One shared automatic NativeMappingUploadOwner spans controlled and actual test
files with global trace/video/screenshot off and no per-file worker options.
Node holds bytes, keys, query, cookies/tokens and original response facts only
in private memory. No raw native/request values are printed or attached.
E owner HTTP observations record URL.pathname only. The public manifest and
closed pipe never expose credentials or private storage references.

Terminal own-receipt GET and source-free resume may be verified as ordinary
Node/browser-owned product HTTP if the page has no terminal Resume. Label that
API-only, not a clicked UI. Pending/needsSource remain controlled UI responses
composed with the existing qualified ingestion API. There is no manufactured
pending state or new interrupt/clock/worker/storage hook. Real browser evidence
must still prove same-original lost-response recovery, exact-byte saves,
permission changes, durability and fresh immutable revision navigation.
