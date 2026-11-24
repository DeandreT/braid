# Catalog browser fixture

`operator.mjs` is the disposable integration fixture used by
`bun run test:catalog-browser`. It runs under Bun and uses ordinary published
braid tools, the pinned native worker and a new PostgreSQL database. It creates
projects and native revision history through product HTTP, then serves the
packaged Angular app with only catalog-reader credentials. There is no product
test-control endpoint.

Run the source checks with `bun run check`. Build Angular and publish
`Braid.Server`, `Braid.DatabaseTool` and `Braid.StorageTool` from the same exact
committed source. Include every published file and Angular asset in each
publication's SHA-256 map. The source manifest is a flat array of
`{ "path": "relative/path", "mode": "100644", "sha256": "..." }` entries
for all committed files; executable source uses mode `100755`.

Create a private directory with mode 0700 outside the checkout, publications and
storage root. The configuration, release record, ledger and manifest stay there;
configuration and release files must have mode 0600. No password, request token,
cookie or native object reference belongs in the public fixture manifest. The
operator generates its disposable credentials in memory.

Start the read-only Docker event/resource observer before starting the fixture:

```sh
python3 tools/catalog-browser/observe-host.py --fixture-id "$FIXTURE_ID" --output /absolute/private/observer
```

The output directory must already exist, be empty and have mode 0700. Keep the
observer's stdin open. Its first JSON line and `observer-admission.json` establish
the actual Docker event subscription; bind that file in the operator configuration.
After the browser fixture has settled its cleanup, send the observer one line,
`finish`. It writes `receipt.json`, checks captured profiles and exact absence,
and returns nonzero if reconciliation fails. Run this in an exclusive native
worker window; it attributes worker events to that window, not a runtime client.

Record the observer and source/publication admissions as hash-bound files.
Choose two distinct available loopback origins. The operator fails if their fixed
ports are occupied and does not retarget them.
PostgreSQL gets one explicitly pinned loopback-only port on its owned bridge.

Copy this configuration into the private directory. Fill every null from the
source, publications and observer records. Keep the immutable image pins.

```json
{
  "schemaVersion": "braid.catalog-browser-operator-config.v1",
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
  "workerImage": "sha256:a63f57a009717d2171e48c02c068e9dcb19974561b91cf8484345d02d5c05439",
  "postgresImage": "postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc",
  "outerObserverAdmission": { "path": null, "sha256": null },
  "source": {
    "commit": null,
    "tree": null,
    "workspace": null,
    "manifest": { "path": null, "sha256": null },
    "admission": { "path": null, "sha256": null }
  },
  "publications": {
    "admission": { "path": null, "sha256": null },
    "server": { "directory": null, "hashes": null },
    "databaseTool": { "directory": null, "hashes": null },
    "storageTool": { "directory": null, "hashes": null }
  }
}
```

Set `runtimeReleased` in the configuration after verifying its inputs. The
separate release record binds the final configuration and operator bytes with
SHA-256. Fill its hashes and set its booleans after the corresponding checks succeed.

```json
{
  "schemaVersion": "braid.catalog-browser-release.v1",
  "released": false,
  "configurationSha256": null,
  "operatorSha256": null,
  "sourceAndPublicationsAccepted": false,
  "outerObserversAccepted": false
}
```

After `bun install --frozen-lockfile` at the repository root, install the browser using the locked local Playwright package.
Resolve its matching executable rather than selecting an arbitrary system
Chromium. Use the same optional absolute `PLAYWRIGHT_BROWSERS_PATH` for both
commands and the browser run:

```sh
bun node_modules/playwright/cli.js install chromium --no-shell
BRAID_CHROMIUM_EXECUTABLE="$(bun -e 'import { chromium } from "playwright"; console.log(chromium.executablePath())')"
export BRAID_CHROMIUM_EXECUTABLE
```

Repeat the browser installation after a Playwright dependency update. Linux
browser libraries and Xvfb are prerequisites; see
[local browser setup](../../docs/development.md#verification).

Set these fixture environment values on the host and run the browser command:

```sh
BRAID_CATALOG_BROWSER_OPERATOR=/absolute/checkout/tools/catalog-browser/operator.mjs
BRAID_CATALOG_BROWSER_OPERATOR_CONFIG=/absolute/private/configuration.json
BRAID_CATALOG_BROWSER_MANIFEST=/absolute/private/manifest.json
BRAID_CATALOG_BASE_URL=http://127.0.0.1:45921
export BRAID_CATALOG_BROWSER_OPERATOR BRAID_CATALOG_BROWSER_OPERATOR_CONFIG
export BRAID_CATALOG_BROWSER_MANIFEST BRAID_CATALOG_BASE_URL BRAID_CHROMIUM_EXECUTABLE
bun run test:catalog-browser
```

Use the actual configured read origin; the example port is not a fallback.
The root command allocates an alternate virtual display and unsets Wayland.
Do not invoke headed Playwright on an active user display. The automatic worker
fixture starts one owned operator and validates its bounded ready record and
immutable manifest before any browser test runs. Controls use the operator's
private pipes; they are never exposed to Page JavaScript or browser storage.
Raw network traces and video are disabled to avoid retaining context tokens.

The scenario covers roles, pagination, saved history, append,
application/database restart, outages and one-way revocations. Controlled cases
check rendering and stale-response defenses in `bun run test:browser`.

The fixture stops and reaps its own API children, verifies worker removal,
then removes only its exact nonce-labelled PostgreSQL, network and volume.
The outer observer must separately confirm actual resource profiles, automatic
worker destruction, exact absence and unchanged preexisting resources. Retain
failures and partial ledgers; never replace cleanup with global pruning or
remove an unrelated resource by name prefix.

## Native CSP qualification preflight

The normal catalog command does not enable the collector. The separate
`test:catalog-native-csp` entry point requires `--qualify`, `--pair`,
`--pair-sha256` and `--origin`. It checks launch inputs only and then refuses:
there is no admitted packaged-suite native cutoff adapter. It never launches
Chromium, starts the operator, or substitutes the installed Playwright driver.

```sh
bun run test:catalog-native-csp --qualify \
  --pair /inputs/pair/pair.json --pair-sha256 "$ADMITTED_PAIR_SHA256" \
  --origin http://127.0.0.1:45921
```

Use an offline Linux x64 guest, with only `lo` present and up so the packaged
HTTP service can listen on `127.0.0.1`. This is not the network setup used by
the standalone intercepted-page control. Stage the pair on a root-owned,
read-only ext4 snapshot and invoke the entry point as an unprivileged user.
Give the exact read origin from the existing catalog operator configuration,
not a development proxy. The future adapter must verify this origin against
the released operator and retain the product owner's independent cleanup.

The record is JSON with `schemaVersion: "braid.catalog-native-csp-pair.v1"`,
`chromeVersion: "157.0.8090.0"`, `driverVersion: "1.64.0"` and `files`, a
relative-path-to-SHA-256 map. Put the complete self-contained Chrome and
Playwright Core distributions in sibling `chrome/` and `driver/` directories
next to the record. The inventory includes the executable, resources, package
entry, driver bundle, and every other ordinary file beneath both directories.
The pinned Playwright Core manifest has no `main` or `type` override. Its
ordered root conditions are `types` (`./index.d.ts`), `import` (`./index.mjs`),
`require` and `default` (both `./index.js`). The record must pin all those
files and the fixed exported subpaths. Admission selects `driver/index.js`
explicitly; it does not look up an installed driver by package name.
Pass the independently admitted SHA-256 of the record with `--pair-sha256`.
This proves byte identity against that pin, not the provenance or behavior of
an arbitrary pair. Do not substitute stock components or use a writable
installation. The future runner must use the verified entry and exact module
instance for the entire browser lane, explicitly request the Chromium
sandbox, and use new private home and temporary directories.

The generation guard is an **unwired, runner-local contract**. Its fields
are not CDP fields, and its structural controls do not prove native behavior.
Keep the original driver root session (not the child returned by
`newBrowserCDPSession()`), epoch, and global journal through
ordinary per-test context closes and final browser verification:

1. `enrollContext(contextId)` binds the exact native ID before any descendant
   is admitted. The provider must prove the context is new, owned, non-default,
   and has never had a producer. `enroll(generation)` binds each native page,
   frame, or worker to that context before it can produce work. Parents and
   replacements must belong to the same context. Retired context IDs cannot
   be reused in the epoch. A second page owns a separate generation; an
   enrolled replacement never acknowledges the original.
2. `close` and `replace` prepare the affected subtree before deliberate
   disposal. `closeContext(contextId, ordinaryClose)` prepares all generations
   in exactly that context, then invokes the ordinary close once. Even an
   empty context needs its own retirement proof. `prepare(epoch, request,
   signal)` returns an opaque token bound to the epoch, inventory revision,
   disposal scope, selected context, original generations, open context set,
   and durable receipt checkpoint. The expected inventory and previous
   tickets are checks against native evidence, not its source.
   Preparation freezes the native producer population and durable prefix,
   not event production. Dispatches or receipts may be pending for those
   origins, and enrolled originals may legitimately produce more events
   during normal disposal. Unknown origins, dropped handoffs and late work
   outside the authorized disposal window are never acceptable.
3. After disposal, `verify(epoch, ticket, signal)` uses that same ticket
   and browser session. The current inventory revision may advance only
   for authorized disposal of the ticket's generations and, for a context
   close, retirement of the selected context. The provider must
   authenticate a gapless journal extension rooted at the ticket's exact
   checkpoint and prove retirement and full settlement for every frozen
   origin, even a zero-event one. The next preparation starts from the
   last authenticated **final** checkpoint. If the journal advances between
   fences, the new checkpoint must descend from that exact checkpoint.
   A scoped close refuses if a surviving origin writes to the same global
   journal during disposal: the adapter must not filter those entries.
   Verification must reject unacknowledged records, early disappearance,
   lost receipts, unrelated births and post-retirement work. A replacement
   remains unadmitted until the old fence is verified and is enrolled before
   it can produce. This ordering is a qualification requirement, not
   permission to delay or serialize an ordinary reload. If native proof
   cannot account for overlapping document lifetimes, refuse. If
   preparation refuses, still close owned targets or contexts; do not
   navigate a replacement or continue qualification.
4. `prepareBrowserEnd()` refuses while any context remains unretired. It
   freezes a separate browser-wide ticket on the original live root and
   returns `{ session, ticket }` without verifying retirement. Neither an
   empty roster nor successful context retirements proves browser-wide
   settlement. Unrepresented producers refuse.
5. The final retirement control is **failure-only**. The future owner must
   perform one ordinary browser close and require both the expected unforced
   exit of the original child and retained evidence authenticating the actual
   terminal head after the debugger pipe closes. Chromium closes that pipe
   before destroying profiles; a close reply, EOF, or Playwright's resolved
   close promise is not proof. Authenticate the same frozen ticket against
   retained evidence of the original session and epoch, never a replacement
   session or a shorter valid prefix. No retained transport or
   browser-only producer representation is supplied here. Timeout, missing,
   partial, lost, foreign, or forced outcomes refuse. Product-owner
   reconciliation remains independent and without retries: an enclosing
   owner must revoke a hung browser lane and reach product shutdown without
   waiting for the browser close promise.

For the local adapter, `session` is the same browser-level object on each
reply. Context enrollment echoes `epoch` and `contextId`; generation enrollment
echoes `epoch` and `{kind,contextId,targetId,generationId,parentGenerationId}`.
Prepare echoes `epoch`, `scope`, and `contextId` (`null` for browser end), and
returns `contextIds` (the exact open context set), `browserWide`, `token`,
`revision`, `inventory`, `receiptCheckpoint`
(`sequence` and `digest`), and covered `origins` (`originId`, `generationId`,
`producerCovered`, `produced`, `dispatched`, `acknowledged`). At prepare,
`remaining.dispatches` and `remaining.receipts` account for the gaps
between those counters. Verify returns the original `ticket`, current
`inventoryRevision`, `browserWide`, `retiredContextId` (non-null only for a
context close), the remaining `contextIds`, authorized `transitions`
(`kind: "disposed"`, `contextId`, and `generationId`), a journal `extension`, and the
final `settlements` of those exact origins, with `retired: true` and equal
produced, dispatched and acknowledged counts. The extension contains the
`base` checkpoint, contiguous `receipts` and the final `checkpoint`. Verify
also carries the independently finalized `finalizedHead`; it must match
the extension end, not just a valid prefix. Each
receipt binds `sequence`, `previousDigest`, `digest`, `originId`,
`generationId`, `recordId` and `commitment`. `remaining.producers`,
`dispatches` and `receipts` must be zero after disposal. Both phases
require zero `lateWork`, `unrelatedWork` and `droppedHandoffs`.

The local `authenticateJournal(epoch, frozenClaim, signal)` seam uses the
same admitted session to authenticate the **whole** statement against
independently retained native evidence:

- `prepare`: `ticket` (including selected context, exact open context set,
  browser-wide coverage, and `remaining`), `afterRevision`, `previousTickets`,
  and `previousCheckpoint`.
- `verify`: the original `ticket`, normalized `extension` (including
  origin settlements), and `terminal` (`scope`, selected and retired context
  IDs, remaining context IDs, `browserWide`, `inventoryRevision`, exact
  authorized `transitions`, `remaining`, and `finalizedHead`).

Return the same claim object with `authenticated: true` and
`epochRecordIdsUnique: true`, plus `checkpointAncestryVerified: true` at
prepare or `finalizedHeadVerified: true` at verify. These attestations
require actual native evidence, not just checks of the submitted claim.
Epoch-wide `recordId` uniqueness includes the retained prefix
and earlier fences: a receipt already counted cannot count again at a
new sequence even if it carries a fresh, valid MAC. The guard advances its
revision and checkpoint only from the authenticated terminal statement.
These names are not a proposed CDP schema.

Run the launch and lifecycle controls with `bun run check:catalog-native-csp`.
