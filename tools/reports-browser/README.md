# Saved-report browser fixture

`operator.mjs` provides the actual persisted-report scenario used by the report
browser tests. It uses the shared, source-admitted Node-core mechanics in
`../browser-fixture/runtime.mjs`. The catalog operator remains a separate scenario
with its original public protocol. Neither executable imports the other.

Build Angular and publish the ordinary Server, DatabaseTool and StorageTool from
the exact committed source first. Run this fixture in an exclusive worker window,
with an already admitted `../catalog-browser/observe-host.py` event/resource
observer. That observer is unchanged. Root owns execution, alternate-display
browser setup, private release records and all actual qualification decisions.

Use the catalog fixture's documented private configuration shape, with these
scenario-specific values:

- `schemaVersion`: `weft.reports-browser-operator-config.v1`.
- Release `schemaVersion`: `weft.reports-browser-release.v1`.
- Release `operatorSha256`: the exact reports entrypoint bytes.
- `source.manifest`: every committed source file, including both scenario
  entrypoints and the shared runtime, with exact body hashes and 644/755 modes.
- `publications`: the actual complete Server/DatabaseTool/StorageTool file maps,
  including the packaged Angular assets; no copied historical/current substitute.
- `outerObserverAdmission`: this fixture UUID's actual open observer admission.
- Two distinct, fixed, available concrete HTTP loopback origins for read and
  mutation APIs; no during-run fallback, proxy or port retarget.
- A fresh owned private directory, configuration and release with modes0700/0600.
  `runtimeReleased` and the separate release booleans remain false until root
  accepts the exact source, publication and outer-observer inputs.

The immutable images, fixed Docker daemon, PostgreSQL loopback binding, dedicated
credentials, one-volume normal restart and resource bounds are the catalog
fixture's existing values. Credentials and request tokens stay in private memory
and child pipes/environment; no credential, private object identifier or lease
record is included in the public manifest or control acknowledgements.

The browser's Node fixture spawns the absolute operator path using
`node [operator, '--config', configuration]`. Its environment uses
`WEFT_REPORTS_BROWSER_OPERATOR`, `WEFT_REPORTS_BROWSER_OPERATOR_CONFIG`,
`WEFT_REPORTS_BROWSER_MANIFEST` and `WEFT_REPORTS_BASE_URL`. The last two must match
the private configuration exactly. These are Node-only values; no product control
endpoint or browser-accessible private configuration is added.

Setup provisions four real principals and project roles, initializes strict v2
storage with the ordinary tool, then makes seven separate native mapping uploads
through product HTTP and the pinned native worker. Five checksum-pinned artifacts
cover basic, exact numeric, partial, ambiguous and invalid-cycle sources; two more
basic uploads are independently uncached and pending samples. Setup asserts zero
report cache keys/attempts/results. Browser Ensure requests create saved reports;
no successful report, payload, claim or receipt is seeded through SQL.

The ready record is `weft.reports-browser-ready.v1`, with fixture UUID, immutable
manifest digest and initial viewer profile. The bounded public manifest contains
only source identity, origin, four role identities, project scope and seven
actual revision metadata records nested under `samples.*.revision`.

Controls are serialized private JSON lines with canonical UUID request nonces,
maximum1024 bytes/64 distinct nonces, and closed acknowledgements maximum4096
bytes. Identical nonce replay returns the original acknowledgement; changed
content refuses. Supported actions are:

- `restart-profile` / `api-start`: one of the four roles and `full`,
  `readOnlyNoWorker` or `profileUnavailable`; `api-stop` stops the read API.
- `checkpoint`: actual durable report-attempt/result counts and observed worker
  create count. Private full snapshots must remain identical across uninterrupted
  read windows with unchanged effects. Explicit fault/restore/revocation controls
  delimit those windows; their own assertions retain all unrelated state.
- `append-primary-revision`: one actual equal-head HTTP append using different
  checksum-pinned native bytes. The original concrete revision and all existing
  saved results/bytes remain retained.
- `pg-normal-restart`: stop both APIs and settle workers, normally stop/start the
  same exact PostgreSQL container, prove unchanged volume/port, rows, journal,
  credentials and private-file identities, then reopen the same read profile.
- `fault-report`: exact sample, operation, currently selected successful derivation
  UUID and `missing` or `integrity`. It verifies the actual Bound publication and
  payload digest first, then renames only that exact final to an owned quarantine
  or flips one byte in that same inode. It never changes report metadata, clocks,
  selection pointers or lifecycle facts. `restore-report` restores original
  bytes/mode/inode, including retained results after a genuine repair.
- `derive-by-http`: an ordinary private-host Ensure, optionally with the captured
  exact repair UUID, returning only the actual small result receipt. This creates
  the real competing selection used to test stale UI repair409 behavior.
- `revoke-project`, `revoke-org`: the catalog fixture's one-way captured-membership
  owner writes, preserving every unrelated row. They run last.
- `shutdown`: resume/settle any pending owned operation, reap API children, verify
  automatic worker destruction and remove only the exact labelled owned PG,
  network and volume. The outer observer separately verifies actual teardown.

`readOnlyNoWorker` genuinely has only catalog/report reader credentials and the
private root: no worker image or report writer. Known retained reports remain
readable while current-profile access is unavailable. `profileUnavailable`
keeps report derivation enabled but omits its lazy worker/writer dependencies;
ordinary metadata reads remain usable, and explicit current derivation fails
without creating a claim. These are configuration effects, not mocked responses.

The dedicated `pending-start` control begins one actual private HTTP Ensure,
observes its committed pending attempt, pauses only its original API PID/start
identity, checks Linux stateT and rechecks the actual pending row. A separately
owned Node-core watchdog is armed first with a2.5-second resume timer. The browser
must immediately observe its own genuine POST202 and call `pending-release`;
that resumes the same process and returns the first operation's actual result.
Actual elapsed resume evidence must be within3 seconds; scheduler timing is not
guaranteed, and a race where Finish wins or automatic release fires fails without
retry. Cleanup always attempts resume and reaps the watchdog/API before resource
removal. The worker's10-second and service's original deadlines are unchanged.
This short physical timing fixture proves real pending reconciliation. Controlled
response cases separately prove the long polling budgets; no long hold is used.

Retain every failed attempt and categorical private ledger. Source preparation is
not runtime acceptance. This increment adds no Rust/image build, schema upgrade,
production test hook or physical maintenance/deletion command.
