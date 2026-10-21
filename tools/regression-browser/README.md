# Actual regression browser qualification

This fixture uses the published server and ordinary `Weft.Worker`, an isolated
PostgreSQL database and the pinned Ferrule native image. Playwright drives the
real Angular screen. It does not substitute case stores, execution results or
API replies.

## Prerequisites

- Native Windows Python 3.12, Node 22.23.3 and npm 12.1.0.
- Headless Chrome for Testing matching the repository browser tooling.
- Example-Build-Linux Docker Engine, with its data on the verified Btrfs volume.
- Linux SDK 10.0.112 at `/srv/example-build/cache/weft-tooling/dotnet` and Linux Node
  22.23.3 at `/srv/example-build/cache/weft-tooling/node-v22.23.3-linux-x64`.
- An already qualified immutable Ferrule native image ID. Use a fixture-owned
  image distinct from an interactive viewer's image to isolate lifecycle events.
- At least 25 GiB free on the actual Windows host before Linux writers start.

The fixture is a local development operation with Docker administrator access.
It never provisions Azure resources or grants production-payload permission.

## Prepare and run

Commit the fixture source first. The preparation launcher refuses a dirty source
tree, rebuilds Angular, exports that exact commit, verifies ordinary source hashes,
and creates normal locked .NET publications on Btrfs. Configuration and journals
are private files outside the repository. There is no external release/handoff
dependency for this scenario.

From the repository root, substitute the actual pinned tool paths and image ID:

```powershell
py -3.12 -B tools/regression-browser/prepare.py `
  --work-root "$env:LOCALAPPDATA/Temp/opencode" `
  --node "<node-22.23.3>/node.exe" `
  --npm-cli "<npm-12.1.0>/bin/npm-cli.js" `
  --worker-image sha256:<qualified-native-image-id>
```

The launcher prints a native Windows `bindings.json` path. Pass it to the browser:

```powershell
$env:WEFT_REGRESSION_BINDINGS = '<printed-bindings-path>'
$env:WEFT_CHROMIUM_EXECUTABLE = '<chrome-for-testing>/chrome.exe'
# From apps/web:
& '<node-22.23.3>/node.exe' ../../node_modules/@playwright/test/cli.js `
  test --config=playwright.regression-live.config.ts
```

The native browser adapter starts the Linux operator through a private WSL pipe.
The root operator uses a private mount namespace with the fixture's Btrfs data
bound beneath a root-owned `/run` mountpoint. Only the empty mountpoint lives in
`/run`; source, products, scratch and payload bytes stay on Btrfs. Shared Btrfs
ancestor ownership and Docker socket permissions are unchanged. The namespace
launcher unmounts and removes the empty mountpoint after operator shutdown.
Only bounded controls and scoped public metadata cross it; database passwords,
cookies, antiforgery tokens, source payloads and result bytes are not pipe fields.
Trace, screenshots and video are off for the actual-data scenario. The separate
controlled browser suite remains useful but is not end-to-end evidence.

## Acceptance and cleanup

The operator genuinely creates a project/mapping through HTTP before the browser
starts. The browser publishes exact fixture versions, executes valid cases before
and after a malformed case, rejects an otherwise-identical baseline missing its
final line feed, reopens results after an
API/worker/PostgreSQL restart, and proves same-session access revocation removes
private presentation. Viewer mode remains metadata-only.

Worker readiness is established by actual completed runs, not merely a mapped
process or a startup log. Owner captures verify the exact revision, submitting
principal, terminal comparison and one execution attempt. Old captured results,
native rows, credentials, migration history and private native-file identities
must remain unchanged.

Shutdown stops only the identified API and worker processes and settles automatic
native-worker cleanup. It then removes only the fixture's labeled database,
network and volume. A cleanup failure fails qualification. Private source,
publication and journal artifacts remain available for investigation; their
removal is separate from proving successful resource settlement.
