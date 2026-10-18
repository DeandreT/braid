# Local development

The PostgreSQL ownership library and its separate local setup are described in
[Project ownership](project-ownership.md). Explicit local identity, persistent
catalog reads and owner provisioning are described in [Catalog reads](catalog-reads.md);
that increment's qualification status is recorded there.
Current database setup also requires the separate maintenance login before
migration 0007; its explicit upgrade and metadata-only scope are described in
[Maintenance metadata](private-object-maintenance-metadata.md).
The [project payload permission foundation](project-payload-permissions.md) is in
progress, with a separate reader credential and explicit migration-owner command.
Its selected grant contract adds no HTTP/auth/payload or preview activation;
integration and qualification remain pending as described there.
Configured catalog storage uses the v2 filesystem profile. Provision new roots
or activate existing v1 roots explicitly using the
[offline storage procedure](private-storage-activation.md); the server does
neither during startup or requests.

The Angular/ASP.NET Core workspace is local-only. It supplies a live API health
check and a read-only mapping viewer over development native examples, with
generated API contracts. A separate Ferrule adapter supplies native inspection,
validation, and projection. The first [catalog browser](catalog-browser.md)
increment adds read-only saved metadata and immutable history; its qualification
status is recorded there. The [saved reports](saved-reports.md) increment connects
the revision page to explanations and validation with explicit generation and
repair controls; its qualification is tracked separately. The catalog API
supports immutable native uploads. Browser writes, hosted authentication, chat
and Wolverine-backed execution follow separate increments. The separate
[project creation HTTP](project-creation-http.md) increment adds explicit
organization actions and its dedicated creator configuration.

The [native mapping upload page](native-mapping-upload-ui.md) creates a mapping
and its first immutable revision from the original file. Its separate
[browser fixture](../tools/native-mapping-upload-browser/README.md) exercises
explicit replay, source-free recovery and permission changes with private fixture
bindings. Run `npm run test:native-mapping-upload-browser` through the alternate
display; controlled frontend checks and actual packaged-host results retain their
separate qualification scopes. Appending a revision follows separately.

## Prerequisites

- .NET SDK 10.0.112, pinned in the root `global.json`.
- Node 22.23.3 and npm 12.1.0, pinned in `.node-version` and `package.json`.
- For Linux browser tests: Xvfb, curl, unzip, and the pinned Chrome for Testing
  build described below.
- For Ferrule worker checks: Rust nightly `2026-07-21` with rustfmt/clippy,
  Linux Docker with cgroup v2, and BuildKit. See [Ferrule worker](ferrule-worker.md)
  for the image build and live-test configuration.

From the repository root:

```sh
npm ci
npm ci --prefix tools/contracts-generator
npm run build
dotnet restore dotnet/Weft.slnx --locked-mode
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
npm run contracts:check
```

Start the server in one terminal:

```sh
dotnet run --no-build --configuration Release --project dotnet/apps/Weft.Server
```

Open http://127.0.0.1:4100 for the built application. The server packages the
Angular assets with its build. For frontend development, run `npm run dev` in
another terminal and open http://127.0.0.1:5173; Angular forwards `/api` requests
to the local service. `WEFT_DEV_LISTEN_ADDR` changes the API's loopback address.
The unauthenticated shell rejects public interfaces.

Examples are at `/examples`, and `/` redirects there. Use `/catalog` at the
packaged API origin for configured catalog reads. Its strict Development Host
boundary is preserved; the separate Angular development proxy is not the
catalog qualification origin.

The [project creation form](project-creation-ui.md) uses the separate organization
creation capability. Its [browser fixture](../tools/project-creation-browser/README.md)
uses only catalog and restricted creator credentials, without native workers or
private-object storage. Run its browser checks through
`npm run test:project-creation-browser`; it requires the explicit private bindings
documented there and uses an alternate display.

The separate [mapping metadata editor](mapping-metadata-ui.md) changes a saved
mapping's name and description using its actual metadata ETag. Its
[browser fixture](../tools/mapping-metadata-browser/README.md) exercises restricted
metadata writes independently of native ingestion and report generation. Run
`npm run test:mapping-metadata-browser` with the explicit private fixture bindings;
its qualification remains separate from project creation and read-only viewing.

To browse real Ferrule examples, build the restricted worker image as described
in [Ferrule worker](ferrule-worker.md). Set `WEFT_FERRULE_WORKER_IMAGE` to its
immutable `sha256:...` image ID and start the server with
`DOTNET_ENVIRONMENT=Development`. The same singleton worker runner serves
projection and validation requests; its local Linux/Docker requirements apply.
The example list remains available without a configured worker and reports that
the explanation and validation are unavailable. The browser does not substitute
synthetic data for a failed native operation.

Development routes are `GET /api/v1/examples/mappings` and
`GET /api/v1/examples/mappings/{exampleId}`. The fixed manifest includes `basic`,
`exact`, `conditions`, `controls`, `functions`, `partial`, `invalid`, `generated`,
and `ambiguous`. Each response keeps the immutable example identity, separate
operation reports, nullable validation result, and native evidence. Unknown IDs
and non-development requests return empty 404s. See
[Mapping viewer](mapping-viewer.md) for navigation and explanation coverage.

To inspect the self-authored fixture contract, start with
`DOTNET_ENVIRONMENT=Development` and request
`/api/v1/fixtures/projections/rich` or `/openapi/v1.json`. Fixtures return 404 in
other environments. See [Projection v1](projection-contract.md) for their scope.

## Updating API contracts

Edit the C# contract or typed endpoint, then build the server and regenerate:

```sh
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
npm run contracts:generate
npm run contracts:check
```

Commit both `contracts/weft-v1.json` and `apps/web/src/api/generated.ts`. The
server build exports OpenAPI 3.1 using the same JSON settings as HTTP responses.
The check regenerates Angular types in memory, compares them without rewriting,
and compiles narrowing/precision examples with Angular's TypeScript version.
The generator has its own locked dependency tree: its supported TypeScript 5
compiler cannot replace Angular's pinned TypeScript 6 compiler.

## Verification

```sh
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
npm run check
npm run build
dotnet restore dotnet/Weft.slnx --locked-mode
dotnet format dotnet/Weft.slnx --verify-no-changes --no-restore
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
npm run contracts:check
git diff --exit-code -- contracts/weft-v1.json apps/web/src/api/generated.ts
weft_host_qualification="$(mktemp -d "${TMPDIR:-/tmp}/weft-host-qualification.XXXXXXXX")"
dotnet publish dotnet/apps/Weft.Server/Weft.Server.csproj --configuration Release --no-build --no-restore --output "$weft_host_qualification/server"
dotnet publish dotnet/tools/Weft.HostPrivacyProbe/Weft.HostPrivacyProbe.csproj --configuration Release --no-build --no-restore --output "$weft_host_qualification/probe"
export WEFT_HOST_TEST_SERVER_PATH="$weft_host_qualification/server/Weft.Server.dll"
export WEFT_HOST_PRIVACY_PROBE_PATH="$weft_host_qualification/probe/Weft.HostPrivacyProbe.dll"
dotnet test dotnet/Weft.slnx --configuration Release --no-build --no-restore
npm run test:browser
```

Host process tests require these explicit absolute paths to fresh publications of
the same Release build. The privacy probe is a qualification tool, not a deployed
Server dependency; keep its publication separate from the shipped host.

The browser harness owns API and Angular development processes on ports 4101
and 5174, fails on occupied ports, and stops them when it exits. Build the Release
server before invoking it. The root browser-test command always uses a fresh
alternate virtual display and unsets Wayland. Never run headed Playwright on an
active user display.

After `npm ci`, use Chrome for Testing **157.0.8090.0** for Linux browser checks.
The Chromium bundled with the locked Playwright package lacks the
[streaming-response fix](https://chromium.googlesource.com/chromium/src/+/62473ad0f747e245e514c93e741bb8e168a2a207)
reported in [Playwright #42742](https://github.com/microsoft/playwright/issues/42742).
That defect can report `ERR_ABORTED` for a `no-store` response after the page has
read it to EOF. Keep the application headers and strict failure assertions.
Verify the archive checksum before extracting it:

```sh
npx playwright install-deps chromium
weft_browser_directory="$(mktemp -d "${TMPDIR:-/tmp}/weft-chromium.XXXXXXXX")"
curl --fail --location --retry 3 --output "$weft_browser_directory/chrome.zip" \
  https://storage.googleapis.com/chrome-for-testing-public/157.0.8090.0/linux64/chrome-linux64.zip
printf '%s  %s\n' 3ec877f440c1ea393c4f164ce5339dc0fd6b5384184a84b38550a78721c2086c "$weft_browser_directory/chrome.zip" | sha256sum --check --status && \
  unzip -q "$weft_browser_directory/chrome.zip" -d "$weft_browser_directory"
WEFT_CHROMIUM_EXECUTABLE="$weft_browser_directory/chrome-linux64/chrome"
export WEFT_CHROMIUM_EXECUTABLE
npm run test:browser
```

CI uses this same version, checksum and executable override. Playwright does not
guarantee compatibility with arbitrary external executables; keep this tested
pin when reproducing results and verify any future browser update with the
existing suite. The lockfile continues to select the Playwright API version.
Linux system libraries are still required; see
[Playwright's browser setup](https://playwright.dev/docs/browsers).

The harness runs the API in Development. Supply the configured immutable worker
image for actual native viewer flows; missing configuration does not qualify
those flows. Display-only fixtures are explicitly labelled synthetic and exercise
generic contract states independently of native backend qualification.
NuGet and npm lockfiles are committed. Domain/application assemblies will stay
independent of ASP.NET Core and provider SDK types, following the Wareboxes-style
modular layout. Wolverine will enter with the first durable job workflow.
