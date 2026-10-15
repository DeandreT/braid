# Local development

The PostgreSQL ownership library and its separate local setup are described in
[Project ownership](project-ownership.md). Explicit local identity, persistent
catalog reads and owner provisioning are described in [Catalog reads](catalog-reads.md);
that increment's qualification status is recorded there.
Current database setup also requires the separate maintenance login before
migration 0007; its explicit upgrade and metadata-only scope are described in
[Maintenance metadata](private-object-maintenance-metadata.md).

The Angular/ASP.NET Core workspace is local-only. It supplies a live API health
check and a read-only mapping viewer over development native examples, with
generated API contracts. A separate Ferrule adapter supplies native inspection,
validation, and projection. The catalog API supports immutable native uploads;
saved-revision browser integration, hosted authentication, chat and
Wolverine-backed execution follow separate increments. The separate
[project creation HTTP](project-creation-http.md) increment adds explicit
organization actions and its dedicated creator configuration.

## Prerequisites

- .NET SDK 10.0.112, pinned in the root `global.json`.
- Node 22.23.3 and npm 12.1.0, pinned in `.node-version` and `package.json`.
- For Linux browser tests: Xvfb and Chromium, or Playwright's installed Chromium.
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
npm run check
npm run build
dotnet restore dotnet/Weft.slnx --locked-mode
dotnet format dotnet/Weft.slnx --verify-no-changes --no-restore
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
npm run contracts:check
git diff --exit-code -- contracts/weft-v1.json apps/web/src/api/generated.ts
dotnet test dotnet/Weft.slnx --configuration Release --no-build --no-restore
npm run test:browser
```

The browser harness owns API and Angular development processes on ports 4101
and 5174, fails on occupied ports, and stops them when it exits. Build the Release
server before invoking it. The root browser-test command always uses a fresh
alternate virtual display and unsets Wayland. Never run headed Playwright on an
active user display. For a preinstalled Chromium executable:

```sh
WEFT_CHROMIUM_EXECUTABLE=/usr/bin/chromium npm run test:browser
```

Otherwise run `npx playwright install chromium` before browser verification.
The harness runs the API in Development. Supply the configured immutable worker
image for actual native viewer flows; missing configuration does not qualify
those flows. Display-only fixtures are explicitly labelled synthetic and exercise
generic contract states independently of native backend qualification.
NuGet and npm lockfiles are committed. Domain/application assemblies will stay
independent of ASP.NET Core and provider SDK types, following the Wareboxes-style
modular layout. Wolverine will enter with the first durable job workflow.
