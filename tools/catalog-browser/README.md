# Catalog browser fixture

`operator.mjs` is the disposable integration fixture used by
`npm run test:catalog-browser`. It uses Node core modules, ordinary published
braid tools, the pinned native worker and a new PostgreSQL database. It creates
projects and native revision history through product HTTP, then serves the
packaged Angular app with only catalog-reader credentials. There is no product
test-control endpoint.

Run the source checks with `npm run check`. Build Angular and publish
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
  "workerImage": "sha256:439291fb4d665e3ba9c086e88214a73fd44552af80932a9b40c97f5816c7047e",
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

After `npm ci`, install the browser using the locked local Playwright package.
Resolve its matching executable rather than selecting an arbitrary system
Chromium. Use the same optional absolute `PLAYWRIGHT_BROWSERS_PATH` for both
commands and the browser run:

```sh
node node_modules/playwright/cli.js install chromium --no-shell
BRAID_CHROMIUM_EXECUTABLE="$(node --input-type=module -e 'import { chromium } from "playwright"; console.log(chromium.executablePath())')"
export BRAID_CHROMIUM_EXECUTABLE
```

Repeat the browser installation after a Playwright dependency update. Linux
browser libraries and Xvfb are prerequisites; see
[local browser setup](../../docs/development.md#verification).

Set these Node-only environment values and run the browser command:

```sh
BRAID_CATALOG_BROWSER_OPERATOR=/absolute/checkout/tools/catalog-browser/operator.mjs
BRAID_CATALOG_BROWSER_OPERATOR_CONFIG=/absolute/private/configuration.json
BRAID_CATALOG_BROWSER_MANIFEST=/absolute/private/manifest.json
BRAID_CATALOG_BASE_URL=http://127.0.0.1:45921
export BRAID_CATALOG_BROWSER_OPERATOR BRAID_CATALOG_BROWSER_OPERATOR_CONFIG
export BRAID_CATALOG_BROWSER_MANIFEST BRAID_CATALOG_BASE_URL BRAID_CHROMIUM_EXECUTABLE
npm run test:catalog-browser
```

Use the actual configured read origin; the example port is not a fallback.
The root command allocates an alternate virtual display and unsets Wayland.
Do not invoke headed Playwright on an active user display. The automatic worker
fixture starts one owned operator and validates its bounded ready record and
immutable manifest before any browser test runs. Controls use the Node child's
private pipes; they are never exposed to Page JavaScript or browser storage.
Raw network traces and video are disabled to avoid retaining context tokens.

The scenario covers roles, pagination, saved history, append,
application/database restart, outages and one-way revocations. Controlled cases
check rendering and stale-response defenses in `npm run test:browser`.

The fixture stops and reaps its own API children, verifies worker removal,
then removes only its exact nonce-labelled PostgreSQL, network and volume.
The outer observer must separately confirm actual resource profiles, automatic
worker destruction, exact absence and unchanged preexisting resources. Retain
failures and partial ledgers; never replace cleanup with global pruning or
remove an unrelated resource by name prefix.
