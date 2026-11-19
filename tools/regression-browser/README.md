# Regression browser fixture

This fixture uses the published server and ordinary `Braid.Worker`, an isolated
PostgreSQL database and the pinned Ferrule native image. Playwright drives the
Angular screen.

## Prerequisites

- Native Windows Python 3.12 and Bun 1.4.5.
- Headless Chrome for Testing matching the repository browser tooling.
- A WSL distribution with systemd, Docker Engine, Linux SDK 10.0.112 and Bun 1.4.5.
  Provision a [Linux storage profile](../../docs/qualification-storage.md) for the
  fixture, including the Docker data root and Linux executables on the approved volume.
- A pinned immutable Ferrule native image ID. Use a fixture-owned
  image distinct from an interactive viewer's image to isolate lifecycle events.
- At least 25 GiB free on the actual Windows host before Linux writers start.

The fixture requires Docker administrator access. After storage and disk-headroom
checks, the WSL launcher checks Docker's data root and starts the existing guarded
`docker.service` only if the daemon is
unavailable. It does not change daemon settings or start an interactive viewer.

The default regression scenario uses the native Ferrule image. For
`--scenario build`, supply the combined compiler image built from
`engines/build-worker/Dockerfile`, which includes both `/ferrule-host` and
`/braid-build-worker`. The fixture uses that same immutable image identity for
inspection, preview evidence and compilation; a native-only image cannot run
the build scenario. Use different free read/mutation ports for independent fixtures.

The shared browser fixture pins the complete migration inventory in
`tools/browser-fixture/schema.mjs` and conserves qualification/release tables.
After adding a migration, review that inventory and run
`bun test ./tools/browser-fixture/schema.test.mjs`; do not remove the admission
check to make an old fixture start.

## Prepare and run

Commit the fixture source first. The preparation launcher refuses a dirty source
tree, rebuilds Angular, exports that exact commit, verifies ordinary source hashes,
and creates normal locked .NET publications on Btrfs. Configuration and journals
are private files outside the repository.

From the repository root, install the locked workspace dependencies and substitute
the actual pinned tool path and image ID. Set `$WindowsWorkRoot` to an existing
private Windows directory, `$WslDistribution` to the installed distribution name,
and `$LinuxStorageProfile` to the absolute Linux path of the profile in that distribution:

```powershell
bun install --frozen-lockfile
py -3.12 -B tools/regression-browser/prepare.py `
  --work-root "$WindowsWorkRoot" `
  --distribution "$WslDistribution" --storage-profile "$LinuxStorageProfile" `
  --bun "<bun-1.4.5>/bun.exe" `
  --worker-image sha256:<qualified-native-image-id>
```

The launcher prints a native Windows `bindings.json` path.

For fixture-observer-only corrections, `--reuse-bindings <previous-bindings.json>`
can retain the previous publications. It refuses changed runtime files, filenames,
modes, package inputs or emitted browser bytes and verifies every retained product
file. Every browser/worker scenario runs again with a new isolated owner.

```powershell
$env:BRAID_REGRESSION_BINDINGS = '<printed-bindings-path>'
$env:BRAID_CHROMIUM_EXECUTABLE = '<chrome-for-testing>/chrome.exe'
# From the repository root:
bun run --cwd apps/web test:e2e --config=playwright.regression-live.config.ts
```

The native browser adapter starts the Linux operator through a private WSL pipe.
The root operator uses a private mount namespace with the fixture's Btrfs data
bound beneath a root-owned `/run` mountpoint. Only the empty mountpoint lives in
`/run`; source, products, scratch and payload bytes stay on Btrfs. Shared Btrfs
ancestor ownership and Docker socket permissions are unchanged. The namespace
launcher unmounts and removes the empty mountpoint after operator shutdown.
Only bounded controls and scoped public metadata cross it; database passwords,
cookies, antiforgery tokens, source payloads and result bytes are not pipe fields.
Trace, screenshots and video are off for the actual-data scenario. An explicit
`BRAID_WORKBENCH_CAPTURE_DIR` enables only named workbench screenshots of this
fixture's self-authored samples; point it at an existing absolute private output
directory. The fixture derives its real saved reports before recording its
conservation baseline, so field inspection and sample-value mode use actual
worker output without changing that baseline during the browser session. The
controlled browser suite uses mocked responses.

## Scenario and cleanup

The operator genuinely creates a project/mapping through HTTP before the browser
starts. The browser publishes exact fixture versions, executes valid cases before
and after a malformed case, rejects an otherwise-identical baseline missing its
final line feed, cancels a queued run while its worker is stopped, and publishes
a corrected second version without changing the failed first version. It
reopens results after an API/worker/PostgreSQL restart, and checks that same-session
access revocation removes
private presentation. Viewer mode remains metadata-only.

Worker readiness is established by actual completed runs, not merely a mapped
process or a startup log. Owner captures verify the exact revision, submitting
principal, terminal comparison and one execution attempt. Old captured results,
native rows, credentials, migration history and private native-file identities
must remain unchanged.

Shutdown stops only the identified API and worker processes and settles automatic
native-worker cleanup. It then removes only the fixture's labeled database,
network and volume. A cleanup failure fails the run. Private source,
publication and journal artifacts remain available for investigation; their
removal is separate from proving successful resource settlement.

Setup observations retain only allowlisted report state/failure categories and
HTTP status. Host settlement records fixed fatal-error categories detected from
bounded output prefixes; it never stores framework messages, paths, tokens or
payloads.
