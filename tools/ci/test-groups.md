# Local backend test groups

Run these commands from the repository root after building the current solution
in Release with the pinned SDK. The runner never builds or restores anything.

```sh
python3 tools/ci/run_tests.py --group fast
python3 tools/ci/run_tests.py --group integration
python3 tools/ci/run_tests.py --group fast --group live-provider
```

`npm test` is a convenience alias for `--group fast`, not the full solution.

With no selection flags, the command runs the full solution. CI's
`--shard-index` and `--shard-count` partition every discovered method by
class, with the same filters and checks. Group and shard flags cannot be mixed.

## What each group selects

- `fast`: four classes covering account display-name validation,
  external identity keys, project payload permission contracts and the fake
  maintenance clock. These use only managed contracts and an in-memory timer
  helper, not services, native workers, browsers or real providers. The full
  solution must still be built; this is not a small build target.
- `integration`: the explicitly listed remaining Linux integration classes,
  including database/container, native-worker, published-host, HTTP, process,
  fake-provider and source-contract tests. Ordinary `Fact` and `Theory` do not
  establish that a class is pure. This group deliberately requires the combined
  prerequisites below rather than silently skipping unavailable worker tests.
- `packaged-browser`: `AuthBrowserHttpsQualificationTests` and
  `ChatWorkspaceBrowserTests`, including all their partial definitions. Requires
  the existing external browser admission and isolation owner.
- `installed-documents`: the installed document qualification, hostile/shutdown
  and Linux host classes. Requires an independently protected installation and
  the external dedicated PID namespace record; it does not create either.
- `installed-document-probes`: the separate shared-profile and resource probe
  classes. Requires the document installation plus pinned probe assemblies.
  This is not included by `installed-documents`.
- `live-provider`: actual local Ollama tests. The whole partial `AiProviderTests`
  class stays here, including its fake-provider cases; it is not split by file.
- `native-windows`: native PostgreSQL fixture and deployment provisioning tests
  on Windows. This selects .NET tests only, not the native PostgreSQL launcher.
- `incremental-build`: the opt-in OpenAPI incremental-build tests, which build
  real hosts in an isolated copy.

There is no live-cloud group: the Azure solution tests exercise fake tools or
local transports, not a deployed cloud environment. Native, browser and cloud
launchers run separately.

## Prerequisites and refusals

Every group requires `dotnet` on `PATH` and the built test assembly at
`dotnet/tests/Braid.Tests/bin/Release/net10.0/Braid.Tests.dll`, including its normal
build-copied dependencies and fixtures. The runner keeps `--no-build` and
`--no-restore`; the caller is responsible for building the current source.

For `integration`, provide Linux, `bash`, Docker (or the existing
`BRAID_DOCKER_EXECUTABLE`), the exact `BRAID_POSTGRES_IMAGE` and
`BRAID_AZURITE_IMAGE` digests used in CI, and no native/client-container PostgreSQL
or loopback Azurite settings. Also provide:

- `BRAID_FERRULE_WORKER_IMAGE` and `BRAID_BUILD_WORKER_IMAGE`, naming the same
  combined native/compiler build, plus `BRAID_COMPILED_RUNTIME_IMAGE` and
  `BRAID_FERRULE_SERVICE_IMAGE`.
- `BRAID_AUTOMATION_DOCKER_TESTS=true`.
- Existing absolute executable paths in `BRAID_FERRULE_HOST_EXECUTABLE`,
  `BRAID_COMPILE_SANDBOX_EXECUTABLE`, `BRAID_TEST_PDF_EXECUTABLE`,
  `BRAID_AUTHORING_TEST_EXECUTABLE`, `BRAID_AUTHORED_PREVIEW_TEST_EXECUTABLE` and
  `BRAID_EDIT_TEST_EXECUTABLE`.
- Existing absolute files in `BRAID_HOST_TEST_SERVER_PATH` and
  `BRAID_HOST_PRIVACY_PROBE_PATH`, published through the existing host procedure.
- `DOTNET_ROOT` with the pinned compiler and reference pack.

For `packaged-browser`, provide Linux, the pinned Docker PostgreSQL fixture,
`BRAID_AUTH_BROWSER_ADMISSION_ROOT`, `BRAID_AUTH_BROWSER_ADMISSION_FILE`,
`BRAID_WORKSPACE_CHAT_BROWSER=true` and the display assigned by the existing
isolated browser owner. The tests validate the admission record's exact
node/npm/browser/server bytes, assets and custody. Prepare the admission record
with `admit.mjs` and use the assigned isolated display.

For both installed-document groups, provide Linux,
`BRAID_TEST_DOCUMENT_HOST_LINUX=1`, `BRAID_SPEC_DOCUMENT_WORKERS=optional`, the
existing `BRAID_SPEC_DOCUMENT_*` installation paths and profile/build/runtime
digests required by `SpecDocumentHostSettings`, `BRAID_TEST_PDF_EXECUTABLE`, and
`BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD` with its `_SHA256` digest. The probe
group additionally requires `BRAID_TEST_DOCUMENT_PROFILE_PROBE_ASSEMBLY` and
`BRAID_TEST_DOCUMENT_RESOURCE_PROBE_ASSEMBLY`, each with its matching
`_BUILD_SHA256` and `_RUNTIME_SHA256` settings (replace `_ASSEMBLY`, do not append
to it). The tests enforce protection, source pins, containment
and settlement.

For `live-provider`, set `BRAID_LIVE_OLLAMA_ENDPOINT` and
`BRAID_LIVE_OLLAMA_MODEL` for an actual local Ollama service. For
`native-windows`, provide Windows and `BRAID_POSTGRES_BIN` containing `initdb.exe`,
`pg_ctl.exe`, `postgres.exe` and `psql.exe`, without Docker PostgreSQL settings.
For `incremental-build`, set `BRAID_OPENAPI_INCREMENTAL_TESTS=1`.

Absent or refused prerequisites return exit code 2 before discovery, with the
missing setting names. These checks inspect files and configuration; the tests
perform runtime checks. Child exit codes and unrelated environment settings are
preserved; `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH` remains
explicitly `false` in child processes.

## Maintaining the classification

`test_groups.py` is a closed class roster, not a name-prefix rule or catch-all.
The source fingerprint binds it to test attributes, all partial classes and
helpers, linked probe code, production dependencies and build inputs. A source
change refuses local grouping until reviewed. Do not automatically refresh that
fingerprint to bypass an audit.

Review changed attributes, constructor/static/module initializers, theory data
sources, collection fixtures and called helpers before assigning or retaining a
class. The fast classes use only ordinary `Fact`/`Theory` and inline data, with
no collection fixtures. Discovery of the whole assembly can evaluate attributes
and theory data: the audited data sources construct managed vectors or read
build-copied contract files, not start services or native processes. Keep that
constraint when changing discovery-time code. Custom PostgreSQL/Azurite,
worker/compiler, Linux/Windows, browser, installed-document and live-Ollama
attributes have different admission requirements; ordinary attributes elsewhere
also contain real process/native cases.

Update the roster and prerequisites after that review, then update
`AUDITED_SOURCE_SHA256` to `source_fingerprint(Path.cwd())`. At runtime the runner
discovers fully qualified method names, rejects unknown or stale classes, proves a
nonempty disjoint/exhaustive partition, prints every selected and omitted group,
and checks filtered discovery exactly before running the solution. Exact method
filters keep all theory rows together, including runtime-discovered rows.

Run the launcher fixtures without .NET execution:

```sh
python3 -m unittest discover -s tools/ci -p 'test_*.py'
```
