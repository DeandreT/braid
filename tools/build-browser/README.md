# Build browser fixture

This scenario extends the maintained execution fixture in
`tools/regression-browser`. It creates a real native mapping through HTTP,
declares JSON input/output formats explicitly, derives its stored projection and
runs actual saved regression evidence. The browser then registers an environment,
approves and queues exact builds through the normal API and ordinary worker.

## Prepare and run

Use the pinned native Windows Node/npm, Python and Chrome tooling, the Linux SDK
and Node described in `tools/regression-browser/README.md`, and a pinned
combined native/compiler image. The image must have its own fixture identity so
the Docker event observer cannot confuse it with an interactive viewer. Changing
only an ownership label over identical compiler layers is sufficient.

Commit the source, then from the repository root:

```powershell
py -3.12 -B tools/regression-browser/prepare.py `
  --scenario build `
  --work-root "$env:LOCALAPPDATA/Temp/opencode" `
  --node "<node-22.23.3>/node.exe" `
  --npm-cli "<npm-12.1.0>/bin/npm-cli.js" `
  --worker-image sha256:<fixture-owned-qualified-compiler-image>
```

Set `BRAID_BUILD_BINDINGS` to the printed native `bindings.json` path and
`BRAID_CHROMIUM_EXECUTABLE` to the pinned Chrome executable. From `apps/web`:

```powershell
& '<node-22.23.3>/node.exe' ../../node_modules/@playwright/test/cli.js `
  test --config=playwright.builds-live.config.ts
```

The shared preparation launcher exports and verifies exact source bytes, builds
the Angular assets natively and publishes ordinary Linux applications on verified
Btrfs. A private mount namespace provides root-owned filesystem ancestry without
moving private objects to the distribution's ext4 filesystem. At least 25 GiB
must remain free on the Windows host before Linux writers start.

## Scenario

The browser registers a server-selected environment profile, selects actual
passing exact-byte regression evidence, approves and queues a build, reopens it
after API/worker/database restart, and compiles another approved build. The owner
checks original actor/revision/native/evidence bindings, canonical intent digest,
final publication, artifact digest/length and executable-header bytes. Captured
original artifacts and terminal results must remain unchanged.

The scenario also cancels a queued build at attempt zero while the worker is
paused, supersedes an environment, recovers and revokes an own original approval,
and verifies permission revocation and viewer denial.

Only bounded controls and public scoped metadata cross the owner pipe. Passwords,
tokens, source payloads and artifact bytes do not. Raw artifact bodies are excluded
from snapshots; digest and byte length establish conservation. Screenshots,
traces and videos are off in this actual-data path.

Shutdown validates the original process identities, stops the owned API/worker,
waits for automatic compiler-container cleanup, and removes only the fixture's
labeled database/network/volume. Cleanup failure fails the run. Shared
Docker socket permissions, service enablement and interactive containers are not
changed.
