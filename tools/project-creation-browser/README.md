# Project-creation browser fixture

This fixture tests the project-creation UI against published
braid APIs and a new PostgreSQL database. It never uploads native data, creates a
private storage root, enables ingestion/reports or starts native workers. Its
separate read-only observer requires **zero native worker events**.

Build Angular and ordinarily publish Server and DatabaseTool from the exact
committed source. Include all actual files/assets in their SHA-256 maps. The
source manifest includes every committed source body and canonical644/755 mode,
including the shared runtime, operator and observer.

Use an exclusive Docker window and an isolated display.
Start `observe-host.py --fixture-id <UUID> --output <private empty directory>`
first. Its actual GET200 event admission is bound in configuration. Keep its
stdin open, then send `finish` only after operator/API/PG cleanup has settled.
The observer captures the owned PG's image, limits, loopback port, labels, named
bridge/volume and exact identities; it verifies normal automatic destruction and
container/network/volume404 without removing resources. Any worker-like event
(by native worker name or immutable worker image) fails this scenario.

The operator uses the shared runtime's bootstrap and migration, source and
publication admission, fixed origins, private owner commands, strict child
PID/start identity and bounded cleanup. It requires only restricted catalog and
project-creator credentials in the browser API. The API has no ingestion,
derivation, report, maintenance or migrator connection, no storage-root value and
no worker-image/daemon environment. The owner operator's distinct private Docker
connection does not become a browser control endpoint.

Private configuration and release must be owned0600 in an owned0700 directory
outside the checkout/publications. Fill the nulls below from
source/publication/observer inputs:

```json
{
  "schemaVersion": "braid.project-creation-browser-operator-config.v1",
  "runtimeReleased": false,
  "fixtureId": null,
  "privateDirectory": null,
  "manifestPath": null,
  "releasePath": null,
  "readOrigin": null,
  "mutationOrigin": null,
  "dotnet": null,
  "docker": "/usr/bin/docker",
  "daemonSocket": "/var/run/docker.sock",
  "workerImage": null,
  "nativeWorkers": "forbidden",
  "postgresImage": "postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc",
  "outerObserverAdmission": {"path": null, "sha256": null},
  "source": {
    "commit": null, "tree": null, "workspace": null,
    "manifest": {"path": null, "sha256": null},
    "admission": {"path": null, "sha256": null}
  },
  "publications": {
    "admission": {"path": null, "sha256": null},
    "server": {"directory": null, "hashes": null},
    "databaseTool": {"directory": null, "hashes": null}
  }
}
```

The release has schema `braid.project-creation-browser-release.v1` and the existing
fields `released`, `configurationSha256`, `operatorSha256`,
`sourceAndPublicationsAccepted`, `outerObserversAccepted`. Bind the configuration
and operator hashes and set the booleans after the corresponding checks succeed.
The complete source manifest also binds the shared runtime.

Node-only browser settings are `BRAID_PROJECT_CREATION_BROWSER_OPERATOR`,
`BRAID_PROJECT_CREATION_BROWSER_OPERATOR_CONFIG`,
`BRAID_PROJECT_CREATION_BROWSER_MANIFEST`, `BRAID_PROJECT_CREATION_BASE_URL`.
The manifest/base values must equal the private configuration. The worker fixture
spawns `node [absoluteOperator, '--config', privateConfig]`. No configuration,
password, token, key, fingerprint or raw body belongs in browser storage, tracing,
attachments or the public manifest. Product host output is drained categorically.

Setup ordinarily provisions three admitted identities. Only setupAdministrator
and creator receive organization creation capability. A private creation-only
host genuinely creates a seed project through product HTTP; ordinary provisioning
makes projectAdministrator its Administrator, with no organization capability.
The browser starts as capable creator with zero project memberships/projects.
No successful project or receipt is seeded with SQL.

The atomic immutable public manifest (`braid.project-creation-browser-fixture.v1`)
contains fixture/source/origin/organization identities, initial profile `creator`,
`profiles.creator` and `profiles.projectAdministrator` with only actorPrincipalId
and allowedOrganizationId, and `seedProject.scope`. Ready contains schema
`braid.project-creation-browser-ready.v1`, fixtureId, manifestSha256 and profile.

The fixed read-only grant snapshot covers the current database ACL/owner,
all schema ACLs/owners, relation and column ACLs/owners, routine ACLs/identities/
owners, and default ACLs. Complete ordered catalog arrays are hashed inside PG
to keep private pipe sizes bounded. These actual grant digests participate in
all full-baseline and normal-restart comparisons. Role and password digests are
captured separately.

The closed private command envelope is `{requestNonce,fixtureId,action,...}`.
Canonical UUID nonces replay identical receipts; changed content refuses. At most
64 nonces,1024-byte commands,4096-byte ACKs are accepted, serialized one at a time.
ACK base is `{schemaVersion:'braid.project-creation-browser-control.v1',requestNonce,
fixtureId,action}`. Additional exact fields:

- `api-stop`: outcome `stopped` or `alreadyStopped`.
- `api-start(profile)` / `restart-profile(profile)`: profile `creator` or
  `projectAdministrator`; outcome `ready`, same profile, contextStatus200.
- `checkpoint`: outcome `observed`, counts `{projects,projectMemberships,
  creationFacts,creatorProjects,workersCreated}`. CreatorProjects counts projects
  originally created by the fixture creator, independent of current access;
  workersCreated must0. Uninterrupted reads with equal counts must also preserve
  the complete private snapshot against the trusted ready/capture baseline. A
  single not-yet-captured creation is allowed only when removing exactly its
  verified initial project/member/fact restores that entire baseline; checkpoint
  never advances trust. Deliberate capability/member changes apply only their
  fixed expected row delta before advancing the trusted baseline.
- `capture-created(projectId)`: once per genuinely new fixture-creator project;
  outcome `captured`, project scope, counts, initialFactsMatch true. It requires
  +1 project/+1 membership/+1 fact, exact initial active Administrator/version1
  and captured immutable creation facts. The entire actual snapshot minus those
  three exact new rows must equal the trusted baseline, including every old/seed
  row, credentials, migration journal, object grants and physical-root absence.
  Only then does capture advance the full baseline. Checkpoint never advances it;
  membership-delete updates it only after its exact expected delta. Use checkpoint
  for repeated recovery/replay. No key or raw receipt is emitted by this command.
- `revoke-capability` / `restore-capability`: outcome `revoked` / `restored`;
  ordinary CLI changes exactly the creator's capability, no identity repair.
- `membership-viewer(projectId)` / `membership-delete(projectId)`: require a prior
  captured creation; outcome `changed`, project scope, membership `viewer` /
  `deleted`. Only fixed owner statements for that original creator/project are
  available; immutable creation facts and every unrelated row remain unchanged.
  Both capability and membership controls first require exact trusted-baseline
  equality. They then compare only the fixed changed row to its explicit expected
  value and compare the full snapshots with that one row excluded; legitimate
  PostgreSQL row-text reordering cannot hide any unrelated change.
- `pg-normal-restart`: outcome `restarted`; both APIs stop, the same exact PG is
  normally stopped/restarted with rows/credentials/grants/volume/port unchanged,
  then the same read profile returns. SPA/tab memory remains alive.
- `shutdown`: outcome `closed` only after owned APIs, zero-worker proof and owned
  PG/network/volume teardown settle. The separate observer proves actual absence.

Failure/refusal adds only outcome `failed` or `refused` and `failure` from
invalidControl/ownershipChanged/deadline/productFailure/stateMismatch/cleanupFailure.
Setup has300s; API stop/start/restart15/45/60s; checkpoint/capture15s;
capability/membership10s; PG restart90s; shutdown120s. Existing backend deadlines
and pool/SQL guards are unchanged. No unbounded or hidden retry is added.

For response loss, the browser's Node route forwards the original real POST,
validates its actual201 and receipt privately, then aborts delivery to the page.
The UI becomes uncertain. Deliberate Recover refreshes admission/token and sends
the same original body/key; actual200 must return the identical historical receipt
with no additional project/member/fact. This proves browser response loss after
actual HTTP commit, not a PostgreSQL COMMIT-ack fault. API/PG restart, capability
revocation/restoration and membership demotion/deletion happen through the closed
owner controls. Historical Administrator receipt never grants current authority:
fresh GET determines Viewer or404. Once confirmed, access retry is GET-only.

Controlled-response UI cases use mocked responses and no owner controls. Retain
failures and private ledgers. Memory-only recovery survives SPA navigation and
server restart while the tab remains alive; reload/closed tab loses its original
key/body.
