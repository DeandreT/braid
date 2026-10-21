# Actual preview browser qualification

`run.mjs` qualifies the Angular preview panel against the actual API, PostgreSQL,
Wolverine worker host and Ferrule worker image. It owns every resource it uses:

- one labeled PostgreSQL container with the restricted roles from
  `deploy/local/postgres-init.sh`, migrated to the current schema with the
  migrator login;
- a private v2 storage root in a fresh temporary directory;
- a Development administrator host on `127.0.0.1:4101` with the in-process worker
  runner, which creates the project and uploads `preview-orders.json` and
  `preview-csv-source.json` through ordinary HTTP;
- the browser host on `127.0.0.1:4100` (Development identity, previews enabled with
  the two preview logins) and the separate `Weft.Worker` preview host.

Publisher and viewer memberships are provisioned with `Weft.DatabaseTool`. The
Playwright spec `apps/web/tests/preview-live.spec.ts` drives the real panel through
a successful preview with exact output downloads, worker-refused invalid input and
an unsupported mapping, cancellation of a queued run while the worker is stopped,
reload recovery including a lost submission answer, and viewer denial. Its only
routed request forwards the real submission and drops the answer. A small
token-protected loopback control surface lets the spec stop or start the worker,
restart the browser host as another profile, and read run rows; it carries no
credentials.

## Run

Build the Angular application, then publish the four products from the same build:

```sh
npm ci && npm run build
dotnet build dotnet/Weft.slnx -c Release
for item in server:apps/Weft.Server worker:apps/Weft.Worker \
    database:tools/Weft.DatabaseTool storage:tools/Weft.StorageTool; do
  name=${item%%:*} project=${item#*:}
  dotnet publish "dotnet/$project/${project#*/}.csproj" -c Release --no-build -o "$PUBLISH/$name"
done
```

With Docker access, the pinned PostgreSQL image and the Ferrule worker image
available locally, and Chrome for Testing in `WEFT_CHROMIUM_EXECUTABLE`:

```sh
env -u WAYLAND_DISPLAY -u XDG_RUNTIME_DIR -u XDG_SESSION_TYPE xvfb-run -a -w 10 \
  node tools/preview-browser/run.mjs --publications "$PUBLISH" \
  --worker-image "$WEFT_FERRULE_WORKER_IMAGE"
```

Ports 4100 and 4101 must be free. Arguments after `--playwright-args` are passed to
Playwright. The run fails if any test fails or if a process, the labeled container
or the temporary directory cannot be removed.
