# Local development

The initial Angular/ASP.NET Core shell is local-only. It supplies a real API
health endpoint, a browser workspace, and development fixture projections with
generated API contracts. A separate Ferrule adapter implements native inspection
and validation; its projection and viewer follow the delivery plan. Authentication,
chat, and Wolverine-backed execution follow separate increments.

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
NuGet and npm lockfiles are committed. Domain/application assemblies will stay
independent of ASP.NET Core and provider SDK types, following the Wareboxes-style
modular layout. Wolverine will enter with the first durable job workflow.
