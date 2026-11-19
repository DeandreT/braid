# Local development

Use `deploy/local/dev-up.sh` for the full Linux development environment.
It builds software, prepares images and local models, provisions data and starts
services; it is not a lightweight frontend launcher. For packaged Session
evaluation instead, see [Runtime packaging](runtime-packaging.md).

Already running? Use `deploy/local/dev-status.sh` to inspect it and
`deploy/local/dev-down.sh` to stop it without deleting its data.
For code checks, go to [Verification](#verification); for frontend-only work,
see [Manual hosting and frontend development](#manual-hosting-and-frontend-development).

## Prerequisites

- Linux with a working user systemd manager and user bus (`systemd-run`,
  `systemctl`, `busctl`). The launcher starts persistent user services.
- A usable Docker daemon with cgroup v2 and BuildKit/buildx. It does not install
  Docker or repair its daemon. `BRAID_DEV_BUILD_DOCKER_CONFIG` can select a private
  Docker configuration containing buildx.
- .NET SDK 10.0.112 (`global.json`), including ASP.NET Core 10, on `PATH` or selected
  by `DOTNET_ROOT`; Bun 1.4.5 (`.bun-version`) on `PATH`. Verify with `bun --version`.
- Git, curl, Python 3.11 or later and standard Linux tools, including `ss`,
  `findmnt` and `sha256sum`. The compiler helper uses `hashlib.file_digest`.
- Free loopback ports 4100, 55432 and 11434 by default, writable build/state
  storage, and network access for dependency/image downloads and missing models.
  First startup builds native/compiler images and can download large model files;
  allow disk space, memory and time for that work.

Compiler-image preparation enforces the platform's storage policy in
[`tools/build-worker/build_image.py`](../tools/build-worker/build_image.py).
Before the first build, set `BRAID_DEV_HOME` so its `tmp` directory is on an
admitted storage volume. Native Linux currently requires writable Btrfs at
`/mnt/example-build`, backed by `/dev/disk/by-label/example-build`. The original WSL route retains its separate
volume-identity, compression and Windows free-space checks. The default under
your home directory is not guaranteed to qualify. A rejected work root stops
preparation; the launcher does not move it or fall back to another disk.

The Docker builds supply their own native toolchains. Direct native or edit-WASM
development additionally needs Rust 1.98.1 with rustfmt/clippy and the matching
WASM setup; see [Mapping editing](mapping-editing.md) and
[Ferrule worker](ferrule-worker.md). Browser tests need their own prerequisites
under [Verification](#verification), not just a running development instance.

## Run a complete local instance

`deploy/local/dev-up.sh` starts its configured Development features on
loopback: catalog reads, metadata edits and project creation,
native upload and report derivation, retained reports and revision comparison,
chat through a local Ollama model, previews, regression cases and runs, builds, and
the local deployment sandbox.
It does not enable every optional capability. Session authentication, specification
intake/document workers and specification-driven creation have separate host
configuration.

From the repository root:

```sh
deploy/local/dev-up.sh
```

It then:

1. Generates every database password once into a private settings directory
   (`~/.config/braid-dev`, mode 0700, files 0600). Nothing secret is printed or
   logged, and build and container processes do not inherit the passwords.
2. Publishes the Release server, `Braid.Worker`, `Braid.DatabaseTool` and
   `Braid.StorageTool` from the checkout into `~/.local/share/braid-dev/app/<commit>`,
   and builds the Ferrule native image and the combined compiler image from its
   exact image ID, tagged `braid-dev/ferrule-host:<commit>` and
   `braid-dev/build-worker:<commit>`. The combined image's immutable ID is the one
   `BRAID_FERRULE_WORKER_IMAGE` for inspection, previews, regression runs and builds.
   Without a global buildx plugin, set `BRAID_DEV_BUILD_DOCKER_CONFIG` to a Docker
   configuration directory that has it.
3. Starts the `braid-dev-postgres` container with the `braid-dev-postgres-data`
   volume on `127.0.0.1:55432`. On its first start, `postgres-init.sh` creates every
   restricted login. It applies the migrations, provisions the Development
   organization and principal, and grants that principal project creation.
4. Provisions a v2 filesystem store under `~/.local/share/braid-dev/storage`.
5. Starts the `braid-ollama` container (created with the `braid-ollama-models`
   volume if absent), pulls missing models, and writes a chat profile for each
   model with `loopbackFixture` against `http://127.0.0.1:11434/api/chat`. The
   first model is the default.
6. Starts the server on `http://127.0.0.1:4100` and the worker as the user
   systemd units `braid-dev-server` and `braid-dev-worker`, which keep running after
   the shell exits. Each reads its own logins from a private environment file and
   logs to `~/.local/share/braid-dev/logs`.
7. Creates the default project "Local project" through the product API and grants
   the principal its Administrator role.
8. Enables the [local deployment sandbox](local-sandbox.md), with its history under
   `~/.local/share/braid-dev/sandbox`. Each mapping's **Local sandbox** page deploys a
   completed build to a sandbox target and sends sample messages to its active release,
   using the combined compiler image as the Docker compiled runtime.

Open `http://127.0.0.1:4100/catalog` after startup succeeds:

```sh
deploy/local/dev-status.sh
deploy/local/dev-down.sh
```

`~/.local/share/braid-dev/status.sh` reports
the units, containers and endpoints, and `~/.local/share/braid-dev/stop.sh` stops
the units and the PostgreSQL and Ollama containers, keeping all data. Both work
without the checkout; the repository's `deploy/local/dev-status.sh` and
`dev-down.sh` are the same commands. `dev-down.sh --keep-ollama` leaves Ollama
running. **Destructive reset:** `dev-down.sh --purge` also removes the instance's
database container and volume, image tags, private store, state and settings.
It never removes the Ollama container or models. Do not purge an instance whose
data or private settings you need to retain.

A rerun keeps the secrets, database, store, identity and project. It rebuilds only
for a new commit (or on every run from a modified checkout), and restarts a unit
only when its build or settings changed. The passwords follow the logins that
`postgres-init.sh` creates, so a rerun also generates one for a login added there;
an existing volume still needs that login created with its `postgres-add-*.sh`
script, as for any existing installation.

| Variable | Default | Purpose |
| --- | --- | --- |
| `BRAID_DEV_NAME` | `braid-dev` | Prefix of the containers, volume, units, image tags and directories |
| `BRAID_DEV_PORT` / `BRAID_DEV_POSTGRES_PORT` | `4100` / `55432` | Loopback ports of the server and PostgreSQL |
| `BRAID_DEV_HOME` / `BRAID_DEV_CONFIG` | `~/.local/share/<name>` / `~/.config/<name>` | State, logs and control scripts / private settings |
| `BRAID_DEV_SOURCE` | this checkout | Checkout to build and run |
| `BRAID_DEV_CHAT_MODELS` | `qwen2.5:3b qwen2.5:1.5b` | Ollama models offered in chat, default first |
| `BRAID_DEV_OLLAMA_CONTAINER` / `BRAID_DEV_OLLAMA_URL` | `braid-ollama` / `http://127.0.0.1:11434` | Ollama container to start (empty for an externally managed one) and its loopback origin |
| `BRAID_DEV_BUILD_DOCKER_CONFIG` | none | Docker configuration with the buildx plugin, used only for image builds |

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
| `BRAID_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS` | the wall budget | 1–120 | Separate budget for creating each container; the wall budget then starts after creation |
| `BRAID_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS` | `2` | 1–120 | Ownership check and removal of each container, and each reaper attempt |

```sh
BRAID_FERRULE_DOCKER_CREATE_TIMEOUT_SECONDS=30 BRAID_FERRULE_DOCKER_CLEANUP_TIMEOUT_SECONDS=20 deploy/local/dev-up.sh
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
Bash on Windows; `BRAID_TEST_BASH` can select another executable):

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
  --work-root "$BRAID_TEST_WORK_ROOT" --expected-filesystem-uuid "$BRAID_TEST_FILESYSTEM_UUID"
```

Set those variables to the owned, verified Btrfs work directory and its expected
filesystem UUID. There is no fallback to an unspecified temporary filesystem.

## Manual hosting and frontend development

Use this advanced route when you need to control host settings yourself, not as
a second full setup recipe. `Disabled`, `Development` and `Session` are explicit
host profiles; a bare server start does not provision a catalog, identity,
credentials, storage or workers. See [Catalog reads](catalog-reads.md) and
[Session host operations](session-host.md) for those settings.

From the repository root, install the locked JavaScript dependencies, then build
the Angular shell before building the host that serves it:

```sh
bun install --frozen-lockfile
bun install --cwd tools/contracts-generator --frozen-lockfile
bun run build
dotnet restore dotnet/Braid.slnx --locked-mode
dotnet build dotnet/Braid.slnx --configuration Release --no-restore
dotnet run --no-build --configuration Release --project dotnet/apps/Braid.Server
```

The built application opens on `http://127.0.0.1:4100`, starting at `/catalog`.
`BRAID_DEV_LISTEN_ADDR` changes the API's loopback address. The unauthenticated
shell rejects public interfaces.

The root install uses `bun.lock` for the frontend workspace; the generator uses
`tools/contracts-generator/bun.lock`. A frozen install fails on a manifest/lockfile
mismatch rather than rewriting the lockfile.

The root scripts run Angular in its workspace, `apps/web`, where `angular.json`
lives. Use the scripts rather than running `ng` at the repository root.

For frontend-only development, run `bun run dev` from the repository root and open
`http://127.0.0.1:5173`. This starts Angular, not the API, database or workers.
Angular forwards `/api` requests to the local service; it does not relax that
service's origin or permission checks. Use the packaged API origin for configured
catalog reads and qualification; the separate frontend proxy is not that origin.

### Configure only the capabilities you need

- [Project ownership](project-ownership.md) and [catalog reads](catalog-reads.md)
  own database/identity provisioning. Provision the
  [maintenance login](private-object-maintenance-metadata.md) before schema installation.
- [Payload permission](project-payload-permissions.md) uses a separate reader
  credential and migration-owner command.
- Filesystem storage needs [initialization](private-storage-activation.md);
  server startup does neither. Azure Blob has its own [initialization and settings](private-storage-blob.md).
- [Project creation](project-creation-ui.md), [metadata edits](mapping-metadata-ui.md),
  [native uploads](native-mapping-upload-ui.md) and [revision append](native-revision-append-ui.md)
  have separate write capabilities. Uploads retain original bytes; append checks
  the reviewed head, and metadata edits use the actual metadata ETag.
- [Saved reports](saved-reports.md) remain readable without a worker; deriving
  new reports requires one. For the Docker runner, use the immutable worker image
  ID in `BRAID_FERRULE_WORKER_IMAGE`. `BRAID_FERRULE_RUNNER=service` instead selects
  the [Ferrule service runner](ferrule-service.md#braid-service-runner). Native
  failures never become synthetic mapping data.
- [Chat](chat-http.md) is off unless configured, with server-owned profiles and
  credentials. It supports local OpenAI-compatible and Ollama endpoints. [Previews](preview-runs.md),
  [regressions](regression-runs.md) and [builds](build-workflow.md) each have their
  own worker and persistence settings.
- `/openapi/v1.json` describes the API.

## Updating API contracts

Every HTTP API that braid serves has an OpenAPI 3.1 document in `contracts/`, which
the host's build writes from its own route registrations:

| Document | Host | API |
| --- | --- | --- |
| `contracts/braid-v1.json` | `Braid.Server` | The browser and session API under `/api` |
| `contracts/braid-endpoint-v1.json` | `Braid.MachineEndpoint` | The [mapping endpoint](mapping-endpoint.md) |
| `contracts/ferrule-service-v1.json` | `Braid.FerruleService` | The [Ferrule service](ferrule-service.md#api-braidferrule-servicev1) |

Edit the C# contract or route, then build and regenerate:

```sh
dotnet build dotnet/Braid.slnx --configuration Release --no-restore
bun run contracts:generate
bun run contracts:check
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

### Generated OpenAPI and incremental builds

Building `Braid.Server`, `Braid.MachineEndpoint` or `Braid.FerruleService` exports its
OpenAPI document from the implementation. `dotnet/Directory.Build.targets` tracks
resolved copy-local implementation assemblies as export dependencies. A referenced
implementation change invalidates the SDK's document cache even if its public
reference assembly and host compilation are unchanged. An unchanged build keeps
the export cached. `OpenApiGenerateDocuments=false` still disables generation.

After an API change, build the affected host, generate the application types when
needed, and check the committed output as described above.

The existing opt-in regression exercises all three hosts in a disposable source
copy with two build jobs, without listeners, databases, workers or browsers:

```sh
BRAID_OPENAPI_INCREMENTAL_TESTS=1 dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~OpenApiIncrementalBuildTests
```

It checks the pinned SDK's stale-cache behavior without the shared target,
reference-implementation-only invalidation without host recompilation, stale-contract
diff failure, unchanged-build reuse and clean-build parity. It changes only its
private copy and removes it afterward. Choose disk-backed temporary storage for
this build-heavy check on hosts with RAM-backed `/tmp`.

## Verification

Run commands from the repository root after the frozen installs
[above](#manual-hosting-and-frontend-development). Web checks also need the
pinned edit-WASM assets described in [Mapping editing](mapping-editing.md).
Backend tests need a restored Release build; the runner does not build it.

| Task | Command | Scope |
| --- | --- | --- |
| Web and static checks | `bun run check` | Formatting, dependency/security and pure fixture checks, generated contracts and frontend/type builds. |
| Web build | `bun run build` | Build Angular assets before publishing the server. |
| Linux tooling checks | `bun run check:tooling` | Python 3.11+ mock suites for the backend test selector, browser launcher and compiler-image helper; no application startup, Docker access or browser execution. |
| Small managed backend selection | `bun run test` | The explicit `fast` group after a current full Release build; prints omitted groups. `bun test` invokes Bun's own test runner instead. |
| Backend suite | `python3 tools/ci/run_tests.py` | Run the built solution tests; external prerequisites and opt-in lanes still apply. |
| .NET formatting | `dotnet format dotnet/Braid.slnx --verify-no-changes --no-restore` | Check source formatting after restore. |
| Generated contracts | `bun run contracts:check` | Check committed contract/type consistency; also compare generated files after host builds. |
| Targeted backend troubleshooting | `dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~UserGuideTests` | Select an existing class explicitly. |
| Browser scenarios | `bun run test:browser` | Use the isolated browser and prerequisite setup below. |

`check:tooling` is a separate Linux/Python route. It is not part of `bun run check`,
which remains the web/static route for other platforms too.

For an explicit backend selection, use `python3 tools/ci/run_tests.py --group NAME`;
repeat `--group` to combine groups. The [test-group reference](../tools/ci/test-groups.md)
lists their exact prerequisites and limits. Missing prerequisites, unknown or
stale class assignments, and an inexact discovered selection stop the run rather
than silently dropping tests. Source changes require a classification review.
The unflagged full suite and CI shards run all groups.

### Backend test prerequisites

Restore and build before testing:

```sh
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false
dotnet restore dotnet/Braid.slnx --locked-mode
dotnet build dotnet/Braid.slnx --configuration Release --no-restore
git diff --exit-code -- contracts apps/web/src/api/generated.ts
```

Host process tests require explicit absolute paths to fresh publications of the
same Release build:

```sh
braid_host_qualification="$(mktemp -d "${TMPDIR:-/tmp}/braid-host-qualification.XXXXXXXX")"
dotnet publish dotnet/apps/Braid.Server/Braid.Server.csproj --configuration Release --no-build --no-restore --output "$braid_host_qualification/server"
dotnet publish dotnet/tools/Braid.HostPrivacyProbe/Braid.HostPrivacyProbe.csproj --configuration Release --no-build --no-restore --output "$braid_host_qualification/probe"
export BRAID_HOST_TEST_SERVER_PATH="$braid_host_qualification/server/Braid.Server.dll"
export BRAID_HOST_PRIVACY_PROBE_PATH="$braid_host_qualification/probe/Braid.HostPrivacyProbe.dll"
python3 tools/ci/run_tests.py
```

The privacy probe is a qualification tool, not a deployed Server dependency;
keep its publication separate from the shipped host. Use task-owned disk-backed
temporary/build storage and remove disposable publications after all tests finish.

Tests with missing opt-ins can skip.
`.github/workflows/ci.yml` lists the exhaustive CI preparation and shard selection.
Use the focused prerequisite owners: [Ferrule worker](ferrule-worker.md),
[native PostgreSQL](native-postgres-tests.md), [document worker host](spec-document-host.md),
[authored creation qualification](creation-sandbox-qualification.md),
[regression browser](regression-browser.md) and [hosted acceptance](hosted-acceptance.md).

The existing `bun run test:project-creation-browser`,
`bun run test:mapping-metadata-browser`, `bun run test:native-mapping-upload-browser`
and `bun run test:native-revision-append-browser` aliases keep their specific
permission and recovery scopes. Their fixture READMEs under `tools/` describe
the required private bindings.

### Docker test networks

A Docker network created without a subnet takes a whole /16 or /20 from the
daemon's default address pools, and a few concurrent test runs exhaust them. Test
fixtures that create their own networks (the PostgreSQL test fixture and the
browser fixture) therefore give each one an explicit /28 from
`BRAID_TEST_SUBNET_RANGE`, by default `198.19.0.0/16`. That default is the upper
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
dotnet restore dotnet/tests/Braid.Tests/Braid.Tests.csproj --locked-mode --artifacts-path artifacts/windows-tests
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --no-restore --configuration Release --artifacts-path artifacts/windows-tests --filter FullyQualifiedName~ChildProgramBuildOutputTests
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
`BRAID_AZURITE_IMAGE` names its pinned image
(`mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5`);
otherwise they are skipped. See [Azure Blob private storage](private-storage-blob.md).

The owned HTTPS sign-in qualification drives the pinned Chrome for Testing through
real TLS, a mock OIDC issuer and the actual session modules hosted by the probe
(see [transport admission](browser-https-network-admission.md)). It also needs
`certutil`, `xvfb-run`, `Xvfb`, `xauth` and an explicit admission record that hashes
the publications, Bun, Chromium and `certutil`. Prepare a fresh record after
every publication, in a private directory short enough for its Unix sockets:

```sh
braid_admission="$(mktemp -d "${TMPDIR:-/tmp}/braid-ah.XXXXXXXX")"
export BRAID_AUTH_BROWSER_ADMISSION_ROOT="$braid_admission"
export BRAID_AUTH_BROWSER_ADMISSION_FILE="$braid_admission/admission.json"
bun tools/auth-browser-https/admit.mjs --approved
```

Without a record the real-browser cases are skipped locally. CI always prepares
one, so they fail rather than skip there. The browser runs on the first free
virtual display unless `BRAID_XVFB_SERVER_NUM` selects one.

The browser harness owns API and Angular development processes on ports 4101
and 5174, fails on occupied ports, and stops them when it exits. Build the Release
server before invoking it. The root browser-test command uses an alternate virtual
display. Keep `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR`, `XDG_SESSION_TYPE` and
`DBUS_SESSION_BUS_ADDRESS` out of browser children so they cannot attach to the
active desktop. Never run headed Playwright on an active user display.

The Linux aliases that use `tools/browser-fixture/launch.py` require Python 3,
`xvfb-run`, `Xvfb` and `xauth`. That shared launcher removes all four desktop
variables, preserves the scenario arguments and returns the child's exit status.
Scenario-specific limits, display selection and authentication core-dump policy
remain with their existing aliases and fixtures.

After the frozen install, use Chrome for Testing **157.0.8090.0** for Linux browser checks.
The Chromium bundled with the locked Playwright package lacks the
[streaming-response fix](https://chromium.googlesource.com/chromium/src/+/62473ad0f747e245e514c93e741bb8e168a2a207)
reported in [Playwright #42742](https://github.com/microsoft/playwright/issues/42742).
That defect can report `ERR_ABORTED` for a `no-store` response after the page has
read it to EOF. Keep the application headers and strict failure assertions.
Verify the archive checksum before extracting it:

```sh
bun node_modules/@playwright/test/cli.js install-deps chromium
braid_browser_directory="$(mktemp -d "${TMPDIR:-/tmp}/braid-chromium.XXXXXXXX")"
curl --fail --location --retry 3 --output "$braid_browser_directory/chrome.zip" \
  https://storage.googleapis.com/chrome-for-testing-public/157.0.8090.0/linux64/chrome-linux64.zip
printf '%s  %s\n' 3ec877f440c1ea393c4f164ce5339dc0fd6b5384184a84b38550a78721c2086c "$braid_browser_directory/chrome.zip" | sha256sum --check --status && \
  unzip -q "$braid_browser_directory/chrome.zip" -d "$braid_browser_directory"
BRAID_CHROMIUM_EXECUTABLE="$braid_browser_directory/chrome-linux64/chrome"
export BRAID_CHROMIUM_EXECUTABLE
bun run test:browser
```

CI uses this same version, checksum and executable override. Playwright does not
guarantee compatibility with arbitrary external executables; keep this tested
pin when reproducing results and verify any future browser update with the
existing suite. The lockfile continues to select the Playwright API version.
Linux system libraries are still required; see
[Playwright's browser setup](https://playwright.dev/docs/browsers).

The harness runs the API in Development. Supply the configured immutable worker
image for native viewer flows. Display-only fixtures exercise generic contract states.
NuGet and Bun lockfiles are committed. Domain/application assemblies remain
independent of ASP.NET Core and provider SDK types, following the Wareboxes-style
modular layout. Wolverine carries durable execution commands; its persistence and
worker settings are separate from the browser-facing API.
