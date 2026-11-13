# Local development

The PostgreSQL ownership library and its separate local setup are described in
[Project ownership](project-ownership.md). Explicit local identity, persistent
catalog reads and owner provisioning are described in [Catalog reads](catalog-reads.md).
Current database setup also requires the separate maintenance login before
migration 0007; its explicit upgrade and metadata-only scope are described in
[Maintenance metadata](private-object-maintenance-metadata.md).
The [project payload permission](project-payload-permissions.md) has a separate
reader credential and explicit migration-owner command. Preparing a grant does
not activate an HTTP route, authentication or a preview worker.
Catalog storage uses an explicitly selected filesystem or Azure Blob store.
For the filesystem profile, provision new roots or activate existing v1 roots
using the [offline storage procedure](private-storage-activation.md); the server
does neither during startup or requests. Blob setup has its own
[initialization and host settings](private-storage-blob.md).

The Angular/ASP.NET Core workspace supplies a compact mapping workbench, generated
API contracts and explicit Disabled, Development and Session host profiles.
A separate Ferrule adapter supplies native inspection, validation and projection.
The [catalog browser](catalog-browser.md) reads saved metadata and immutable
history. [Saved reports](saved-reports.md) connect the revision page to explanations
and validation, with explicit generation and repair controls. The catalog API
supports project creation, metadata edits and immutable native uploads through
their separately configured write capabilities. [Session host operations](session-host.md)
describes hosted authentication and private configuration; a local test does not
establish a working cloud deployment.

Revision-pinned chat is off unless configured; its server-owned model profiles,
credentials and routes are described in [Chat turn HTTP API](chat-http.md). A
local OpenAI-compatible or Ollama endpoint can be configured as a profile.
Sample previews, saved regression runs and build execution use their own worker
and persistence settings; see [Preview runs](preview-runs.md),
[Regression runs](regression-runs.md) and [Build workflow](build-workflow.md).

The [native mapping upload page](native-mapping-upload-ui.md) creates a mapping
and its first immutable revision from the original file. Its separate
[browser fixture](../tools/native-mapping-upload-browser/README.md) exercises
explicit replay, source-free recovery and permission changes with private fixture
bindings. Run `npm run test:native-mapping-upload-browser` through the alternate
display; controlled frontend checks and actual packaged-host results retain their
separate qualification scopes. The [revision append page](native-revision-append-ui.md)
checks the reviewed head before sending and retains the original request for
explicit recovery.

The packaged container images, their private configuration delivery and a local
stack in the hosted shape are described in [Runtime packaging](runtime-packaging.md).

## Prerequisites

- .NET SDK 10.0.112, pinned in the root `global.json`.
- Node 22.23.3 and npm 12.1.0, pinned in `.node-version` and `package.json`.
- For Linux browser tests: Xvfb, curl, unzip, and the pinned Chrome for Testing
  build described below.
- For Ferrule worker checks: Rust `1.98.1` (stable, Ferrule's pinned toolchain) with rustfmt/clippy,
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

### Generated OpenAPI and incremental builds

Building `Weft.Server`, `Weft.MachineEndpoint` or `Weft.FerruleService` exports its
OpenAPI document into `contracts/` from the built implementation. The shared
`dotnet/Directory.Build.targets` includes resolved copy-local implementation assemblies
in the export's incremental dependencies. A referenced implementation can change
without changing its public reference assembly or recompiling the host; that still
invalidates the SDK's document cache. An unchanged build leaves the export cached.
Generation disabled explicitly with `OpenApiGenerateDocuments=false` stays disabled.

After an API change, build the affected host and run `npm run contracts:generate`
when the application contract changed. Check `npm run contracts:check` and
`git diff --exit-code -- contracts apps/web/src/api/generated.ts` against the
committed generated files, as the existing CI job does. A successful incremental
compile alone does not establish that generated files have been committed.

The build regression runs in the existing CI test step and can be enabled locally
with the same opt-in flag. It exercises all three actual hosts in a disposable source
copy, with two build jobs and no listeners, databases, workers or browsers:

```sh
WEFT_OPENAPI_INCREMENTAL_TESTS=1 dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~OpenApiIncrementalBuildTests
```

It demonstrates the pinned SDK's stale-cache behavior without the shared target,
then checks reference-implementation-only invalidation without host recompilation,
stale-contract diff failure, unchanged-build reuse and clean-build parity. The
test changes only its private copy and removes it afterward. Choose a disk-backed
temporary directory for this build-heavy check on hosts with RAM-backed `/tmp`.

Start the server in one terminal:

```sh
dotnet run --no-build --configuration Release --project dotnet/apps/Weft.Server
```

Open http://127.0.0.1:4100 for the built application. The server packages the
Angular assets with its build. For frontend development, run `npm run dev` in
another terminal and open http://127.0.0.1:5173; Angular forwards `/api` requests
to the local service. `WEFT_DEV_LISTEN_ADDR` changes the API's loopback address.
The unauthenticated shell rejects public interfaces.

The application root opens `/catalog`. Use the packaged API origin for
configured catalog reads. Its strict Development Host
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

To inspect mappings, upload them into a configured catalog and open a saved
revision in the workbench. Build the restricted worker image as described in
[Ferrule worker](ferrule-worker.md) and set `WEFT_FERRULE_WORKER_IMAGE` to its
immutable `sha256:...` image ID. The singleton worker runner serves projection
and validation requests under the documented Linux/Docker restrictions.
Alternatively, `WEFT_FERRULE_RUNNER=service` and the
[service runner settings](ferrule-service.md#weft-service-runner) select a running
Ferrule service. Saved reports remain readable without a worker; deriving new
reports requires the configured backend. Failed native operations never become
synthetic mapping data. See [Saved mapping reports](saved-reports.md).

To inspect the self-authored fixture contract, start with
`DOTNET_ENVIRONMENT=Development` and request
`/api/v1/fixtures/projections/rich` or `/openapi/v1.json`. Fixtures return 404 in
other environments. See [Projection v1](projection-contract.md) for their scope.

## Run a complete local instance

`deploy/local/dev-up.sh` starts every feature of the Development profile on
loopback in one command: catalog reads, metadata edits and project creation,
native upload and report derivation, retained reports and revision comparison,
chat through a local Ollama model, previews, regression cases and runs, builds, and
the local deployment sandbox.
It needs Linux with user systemd, Docker with BuildKit, Node and the pinned .NET
SDK on `PATH` (or `DOTNET_ROOT`) with its ASP.NET Core runtime.

```sh
deploy/local/dev-up.sh
```

It then:

1. Generates every database password once into a private settings directory
   (`~/.config/weft-dev`, mode 0700, files 0600). Nothing secret is printed or
   logged, and build and container processes do not inherit the passwords.
2. Publishes the Release server, `Weft.Worker`, `Weft.DatabaseTool` and
   `Weft.StorageTool` from the checkout into `~/.local/share/weft-dev/app/<commit>`,
   and builds the Ferrule native image and the combined compiler image from its
   exact image ID, tagged `weft-dev/ferrule-host:<commit>` and
   `weft-dev/build-worker:<commit>`. The combined image's immutable ID is the one
   `WEFT_FERRULE_WORKER_IMAGE` for inspection, previews, regression runs and builds.
   Without a global buildx plugin, set `WEFT_DEV_BUILD_DOCKER_CONFIG` to a Docker
   configuration directory that has it.
3. Starts the `weft-dev-postgres` container with the `weft-dev-postgres-data`
   volume on `127.0.0.1:55432`. On its first start, `postgres-init.sh` creates every
   restricted login. It applies the migrations, provisions the Development
   organization and principal, and grants that principal project creation.
4. Provisions a v2 filesystem store under `~/.local/share/weft-dev/storage`.
5. Starts the `weft-ollama` container (created with the `weft-ollama-models`
   volume if absent), pulls missing models, and writes a chat profile for each
   model with `loopbackFixture` against `http://127.0.0.1:11434/api/chat`. The
   first model is the default.
6. Starts the server on `http://127.0.0.1:4100` and the worker as the user
   systemd units `weft-dev-server` and `weft-dev-worker`, which keep running after
   the shell exits. Each reads its own logins from a private environment file and
   logs to `~/.local/share/weft-dev/logs`.
7. Creates the default project "Local project" through the product API and grants
   the principal its Administrator role.
8. Enables the [local deployment sandbox](local-sandbox.md), with its history under
   `~/.local/share/weft-dev/sandbox`. Each mapping's **Local sandbox** page deploys a
   completed build to a sandbox target and sends sample messages to its active release,
   using the combined compiler image as the Docker compiled runtime.

Open `http://127.0.0.1:4100/catalog`. `~/.local/share/weft-dev/status.sh` reports
the units, containers and endpoints, and `~/.local/share/weft-dev/stop.sh` stops
the units and the PostgreSQL and Ollama containers, keeping all data. Both work
without the checkout; the repository's `deploy/local/dev-status.sh` and
`dev-down.sh` are the same commands. `dev-down.sh --keep-ollama` leaves Ollama
running; `dev-down.sh --purge` also removes the instance's database container and
volume, image tags, state and settings, but never the Ollama container or models.

A rerun keeps the secrets, database, store, identity and project. It rebuilds only
for a new commit (or on every run from a modified checkout), and restarts a unit
only when its build or settings changed. The passwords follow the logins that
`postgres-init.sh` creates, so a rerun also generates one for a login added there;
an existing volume still needs that login created with its `postgres-add-*.sh`
script, as for any existing installation.

| Variable | Default | Purpose |
| --- | --- | --- |
| `WEFT_DEV_NAME` | `weft-dev` | Prefix of the containers, volume, units, image tags and directories |
| `WEFT_DEV_PORT` / `WEFT_DEV_POSTGRES_PORT` | `4100` / `55432` | Loopback ports of the server and PostgreSQL |
| `WEFT_DEV_HOME` / `WEFT_DEV_CONFIG` | `~/.local/share/<name>` / `~/.config/<name>` | State, logs and control scripts / private settings |
| `WEFT_DEV_SOURCE` | this checkout | Checkout to build and run |
| `WEFT_DEV_CHAT_MODELS` | `qwen2.5:3b qwen2.5:1.5b` | Ollama models offered in chat, default first |
| `WEFT_DEV_OLLAMA_CONTAINER` / `WEFT_DEV_OLLAMA_URL` | `weft-ollama` / `http://127.0.0.1:11434` | Ollama container to start (empty for an externally managed one) and its loopback origin |
| `WEFT_DEV_BUILD_DOCKER_CONFIG` | none | Docker configuration with the buildx plugin, used only for image builds |
| `WEFT_DEV_SEED` | `0` | `1` runs `dev-seed.sh` after startup ([Demo data](#demo-data)) |

Pass the same variables to `dev-down.sh` and `dev-status.sh`. A second instance with
its own name and ports leaves the first untouched; give it `--keep-ollama` when
stopping it so a shared Ollama keeps serving the other. Chat on CPU can exceed the
provider deadlines described in [Local model check](chat-http.md#local-model-check);
the smaller model answers faster.

### Busy Docker hosts

The local Docker runner gives `docker container create` the operation's wall
budget (10 s for inspection) and its ownership check and removal two seconds. On a
loaded machine either can take longer. A removal it cannot confirm pauses that
runner: operations answer "backend unavailable" while a background reaper retries
the exact container, and the runner reopens by itself once the removal is confirmed
([Ferrule worker](ferrule-worker.md#reaping-unconfirmed-containers)). To avoid the
pauses, raise the budgets before `dev-up.sh`, which passes them to the server and
worker units:

| Variable | Default | Bound | Purpose |
| --- | --- | --- | --- |
| `WEFT_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS` | the wall budget | 1–120 | Separate budget for creating each container; the wall budget then starts after creation |
| `WEFT_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS` | `2` | 1–120 | Ownership check and removal of each container, and each reaper attempt |

```sh
WEFT_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS=30 WEFT_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS=20 deploy/local/dev-up.sh
```

Local server and worker units have a separate 300-second graceful-stop allowance:
up to 120 seconds of create settlement plus 120 seconds of independent cleanup,
with 60 seconds for host cancellation/draining and bounded CLI-exit overhead.
This does not raise worker execution or cleanup limits. The unit stamp includes
the complete generated invocation, including this stop grace, plus hashes of the
private environment file and host binary. Policy changes are detected even when
the environment and binary are unchanged; identical reruns leave the unit running.
Before stopping a changed active unit, the launcher reads its live numeric
`TimeoutStopUSec` property through the user bus. It refuses automatic stop or
replacement if the active grace is below 300 seconds or cannot be confirmed.
It does not assume a loaded transient service supports changing its timeout at
runtime. For an older unit, its owner must let original work and container recovery
settle and arrange an explicitly safe manual stop before rerunning the launcher.
If settlement cannot be confirmed, leave the unit in place rather than force-stop
it. Private environment values are never logged.

The maintained unit-composition tests use stub services and synthetic private
settings, without Docker, systemd or waits. Run them with an existing Bash (Git
Bash on Windows; `WEFT_TEST_BASH` can select another executable):

```powershell
py -3.12 -m unittest discover -s deploy/local -p test_dev_units.py -v
```

An opt-in Linux fixture verifies live property support, refusal before stopping an
under-budget active unit, settled replacement of an adequately budgeted unit and
identical-rerun behavior. It uses uniquely named transient user services, synthetic
Python hosts and short cleanup delays, never Docker or existing instance units.
It requires the existing user manager and verified Btrfs mount; failures retain
their private fixture directory rather than force-stop unconfirmed work:

```sh
python3 deploy/local/test_dev_units_systemd.py --run-owned-user-units \
  --work-root "$WEFT_TEST_WORK_ROOT" --expected-filesystem-uuid "$WEFT_TEST_FILESYSTEM_UUID"
```

Set those variables to the owned, verified Btrfs work directory and its expected
filesystem UUID. There is no fallback to an unspecified temporary filesystem.

### Demo data

`deploy/local/dev-seed.sh` fills a running instance with invented demo data, or
`WEFT_DEV_SEED=1 deploy/local/dev-up.sh` runs it after startup. Pass the same
`WEFT_DEV_*` variables as for `dev-up.sh` to seed a second instance.

```sh
WEFT_DEV_FERRULE_EXAMPLES=../ferrule/examples deploy/local/dev-seed.sh
```

It works through the product API as the Development user, like the browser does,
and never writes to the database directly. It creates five projects (order
fulfilment, invoicing, shipping notices, inventory sync and partner onboarding)
with 21 self-authored Ferrule mappings across JSON, CSV, XML, fixed-width text,
X12 and EDIFACT. Each mapping has two or three revisions, so version history and
comparison have something to show, and one revision is deliberately
decoded-invalid. The seed saves each project's notification and automation
settings, then saves projection and validation reports, runs previews, records
regression cases (some with outdated baselines that fail), runs them, rechecks
suites, and approves and builds JSON mappings whose cases pass. Invoicing requires
passing regressions, so its failing cases keep its builds refused. With
`WEFT_DEV_FERRULE_EXAMPLES` set to a Ferrule checkout's `examples` directory, it
also uploads Ferrule's examples to "Local project". Unless `WEFT_DEV_SEED_CHAT=0`,
it asks a few chat questions and waits a bounded time for the local model's answers.
Release targets are registered only by the operator's `Weft.ReleaseTool`
([Local release activation](local-releases.md#operator-commands)), so the seed
leaves them out.

Every create uses an idempotency key or resource ID derived from
`WEFT_DEV_SEED_NAMESPACE` (default `weft-demo-v1`) and the item's name, so a rerun
replays instead of duplicating. A step that fails is listed at the end, the rest
still runs, and the script exits non-zero. On a busy host the server's Docker
runner can pause while it retries an unconfirmed container cleanup; the seed then
stops early, since each upload has a limited number of attempts. Run it again once
the runner has reopened ([Busy Docker hosts](#busy-docker-hosts)). The catalog
lives in `deploy/local/demo-data/catalog.mjs`;
`node --test deploy/local/demo-data/seed.test.mjs` checks it without an instance.

## Updating API contracts

Every HTTP API that Weft serves has an OpenAPI 3.1 document in `contracts/`, which
the host's build writes from its own route registrations:

| Document | Host | API |
| --- | --- | --- |
| `contracts/weft-v1.json` | `Weft.Server` | The browser and session API under `/api` |
| `contracts/weft-endpoint-v1.json` | `Weft.MachineEndpoint` | The [mapping endpoint](mapping-endpoint.md) |
| `contracts/ferrule-service-v1.json` | `Weft.FerruleService` | The [Ferrule service](ferrule-service.md#api-weftferrule-servicev1) |

Edit the C# contract or route, then build and regenerate:

```sh
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
npm run contracts:generate
npm run contracts:check
```

Commit the rewritten documents and `apps/web/src/api/generated.ts`. Each build
exports OpenAPI 3.1 using the same JSON settings as the host's responses. The
endpoint and the Ferrule service generate theirs from a document-only host that
maps the same routes without settings, credentials, a database or a listener; only
the build-time generator (`GetDocument.Insider`) selects it. Image builds turn
generation off with `-p:OpenApiGenerateDocuments=false`.

The check regenerates Angular types in memory, compares them without rewriting,
and compiles narrowing/precision examples with Angular's TypeScript version. It
also validates the endpoint and service documents: references, unique operation
IDs, path parameters, response descriptions and security schemes. The generator
has its own locked dependency tree: its supported TypeScript 5 compiler cannot
replace Angular's pinned TypeScript 6 compiler.

.NET tests compare each document with the document its host generates and with
the host's live routes: every mapped route and method with its operation ID and
statuses, and no other route. After a build, a route change without its committed
document fails `git diff --exit-code -- contracts`; with generation off, it fails
these tests.

## Verification

```sh
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
npm run check
npm run build
dotnet restore dotnet/Weft.slnx --locked-mode
dotnet format dotnet/Weft.slnx --verify-no-changes --no-restore
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
npm run contracts:check
git diff --exit-code -- contracts apps/web/src/api/generated.ts
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

### Documentation screenshots

The README and docs embed screenshots from `docs/images`. A frontend change that
affects a captured screen must rerun `npm run docs:screenshots`, with
`WEFT_CHROMIUM_EXECUTABLE` set as for the browser checks, and commit the updated
images in the same pull request. `npm run check` fails when a document references
a missing image or the manifest disagrees with the images, the capture spec or
the documents. See [docs/images](images/README.md).

### Docker test networks

A Docker network created without a subnet takes a whole /16 or /20 from the
daemon's default address pools, and a few concurrent test runs exhaust them. Test
fixtures that create their own networks (the PostgreSQL test fixture and the
browser fixture) therefore give each one an explicit /28 from
`WEFT_TEST_SUBNET_RANGE`, by default `198.19.0.0/16`. That default is the upper
half of the RFC 2544 benchmarking block, outside Docker's default pools
(`172.17.0.0/16`–`172.31.0.0/16` and `192.168.0.0/16`), private LAN ranges, CGNAT
and Tailscale (`100.64.0.0/10`) and the fake-IP range of common proxies
(`198.18.0.0/16`). Set the variable to another free IPv4 range of /28 or larger,
in canonical form, when the default collides with a local network.

A fixture tries distinct random /28 candidates and moves to another one when
Docker refuses a candidate as overlapping an existing network. Other Docker
failures are not retried. After 32 refused candidates, or once every candidate
of a smaller range is refused, setup fails with an exhaustion error that names
the range. Fixture networks keep their owner labels, so cleanup by owner label
removes them with the fixture's other resources.

The Compose test scripts (`deploy/compose/smoke-test.sh` and
`deploy/compose/acceptance-test.sh`) give every network of their project a
distinct /27 of the same range through a generated override in the run's private
directory; the deployment Compose files keep daemon-assigned subnets. A candidate
that overlaps an existing Docker network is skipped, with the same bound and
exhaustion error. If another network takes a chosen subnet before Compose creates
it, the run removes its networks and starts again on new subnets, up to three
starts. Later Compose commands of the run, including the endpoint stage and the
browser flows, include the same override.

### Isolated test builds

Keep native Windows and Linux outputs separate. Pass the same `--artifacts-path`
and configuration to restore, build and test commands. For example, in PowerShell:

```powershell
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
dotnet restore dotnet/tests/Weft.Tests/Weft.Tests.csproj --locked-mode --artifacts-path artifacts/windows-tests
dotnet test dotnet/tests/Weft.Tests/Weft.Tests.csproj --no-restore --configuration Release --artifacts-path artifacts/windows-tests --filter FullyQualifiedName~ChildProgramBuildOutputTests
```

The test project resolves each child program through MSBuild's `GetTargetPath`
and copies that program's complete output into its existing test subdirectory.
This honors isolated artifact directories and explicit output overrides rather
than looking for another build's conventional `bin` directory. A missing built
child fails the build; do not repair it by borrowing binaries from another source
revision. `ChildProgramBuildOutputTests` checks entrypoints, runtime configuration
and managed runtime dependencies. Platform-specific execution tests retain their
own Linux, Docker, database and host-publication requirements.

Azure Blob storage tests run against an owned Azurite emulator when
`WEFT_AZURITE_IMAGE` names its pinned image
(`mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5`);
otherwise they are skipped. See [Azure Blob private storage](private-storage-blob.md).

The owned HTTPS sign-in qualification drives the pinned Chrome for Testing through
real TLS, a mock OIDC issuer and the actual session modules hosted by the probe
(see [transport admission](browser-https-network-admission.md)). It also needs
`certutil`, `xvfb-run`, `Xvfb`, `xauth` and an explicit admission record that hashes
the publications, Node, npm, Chromium and `certutil`. Prepare a fresh record after
every publication, in a private directory short enough for its Unix sockets:

```sh
weft_admission="$(mktemp -d "${TMPDIR:-/tmp}/weft-ah.XXXXXXXX")"
export WEFT_AUTH_BROWSER_ADMISSION_ROOT="$weft_admission"
export WEFT_AUTH_BROWSER_ADMISSION_FILE="$weft_admission/admission.json"
node tools/auth-browser-https/admit.mjs --approved
```

Without a record the real-browser cases are skipped locally. CI always prepares
one, so they fail rather than skip there. The browser runs on the first free
virtual display unless `WEFT_XVFB_SERVER_NUM` selects one.

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
NuGet and npm lockfiles are committed. Domain/application assemblies remain
independent of ASP.NET Core and provider SDK types, following the Wareboxes-style
modular layout. Wolverine carries durable execution commands; its persistence and
worker settings are separate from the browser-facing API.
