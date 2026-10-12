# Local development

The initial Angular/ASP.NET Core shell is local-only. It supplies a real API
health endpoint and a browser workspace. Mapping, authentication, chat, and
Wolverine-backed execution follow separate increments.

## Prerequisites

- .NET SDK 10.0.112, pinned in the root `global.json`.
- Node 22.23.3 and npm 12.1.0, pinned in `.node-version` and `package.json`.
- For Linux browser tests: Xvfb and Chromium, or Playwright's installed Chromium.

From the repository root:

```sh
npm ci
npm run build
dotnet restore dotnet/Weft.slnx --locked-mode
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
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

## Verification

```sh
npm run check
npm run build
dotnet restore dotnet/Weft.slnx --locked-mode
dotnet format dotnet/Weft.slnx --verify-no-changes --no-restore
dotnet build dotnet/Weft.slnx --configuration Release --no-restore
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
