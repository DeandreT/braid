# Native PostgreSQL qualification launcher

`run.py` runs an explicit .NET test selection against the repository's existing
native Windows PostgreSQL fixture. It installs nothing and never connects to an
existing application database. Linux filesystem, Docker, browser and live-provider
qualification are separate gates.

Use Python 3.12, the SDK pinned by `global.json`, and PostgreSQL 16.15. Select the
already-installed native binaries and a shared native NuGet cache. The work and
results roots must exist, be absolute and contain no links or junctions. Keep the
work root short (for example `C:/example/weft-native-checks`); the launcher refuses a
source prefix over 80 characters before building. Results must be outside the
disposable work root.

```powershell
py -3.12 tools/native-postgres/run.py `
  --dotnet "$env:LOCALAPPDATA/ITS/Tooling/weft-dotnet-10.0.112/dotnet.exe" `
  --postgres-bin "$env:LOCALAPPDATA/ITS/Tooling/weft-postgres-16.15-1/pgsql/bin" `
  --nuget-packages "$env:LOCALAPPDATA/ITS/Tooling/weft-nuget/packages" `
  --work-root C:/example/weft-native-checks `
  --results-root "$env:LOCALAPPDATA/Temp/opencode" `
  --filter 'FullyQualifiedName~RegressionDeploymentProvisioningTests' `
  --expected-tests 6
```

The source checkout must be clean. The launcher creates a detached worktree at
the current commit and preserves the captured working copy's exact text bytes
when Git chooses different LF/CRLF line endings. It admits all differences first,
rejects binary or content changes, and records the copied paths. The original
checkout is never rewritten. It verifies every tracked source file and tool binary, performs a locked restore
from NuGet.org, builds isolated Release outputs beneath that source, then runs the
selection with `--no-build`. Only approved Windows environment fields flow to the
child; application credentials, `PG*`, cloud credentials, runtime hooks and Docker
test-mode settings do not. Each child explicitly opts out of adding global tools
to User PATH, and the saved User PATH is checked afterward.
Both the frozen source and original checkout must still match at the end; edits
or a changed original HEAD prevent this run from qualifying the current checkout.

The result directory contains source and product hashes, phase logs, `tests.trx`
and `summary.json`. Only a nonempty, fully passing, unskipped result can pass;
`--expected-tests` also checks the selected count. A process error, timeout,
interruption, missing result, changed source or incomplete cleanup is not success.
Logs and test data can contain private synthetic fixtures; keep them local rather
than attaching the entire directory publicly.

Each phase has a 30-minute default wall deadline, configurable with
`--phase-seconds`; this does not change test, query or lock timeouts. The launcher
checks disk space during the process and requests interruption if the 25 GiB
free-space floor is crossed. It signals only the process it started and retains
the work directory if settlement is unconfirmed. It never kills every `dotnet`
or `postgres` process, repairs database permissions or deletes an unverified
cluster. Follow [native fixture recovery](../../docs/native-postgres-tests.md)
for any cluster left by a forcibly interrupted test process.

Successful runs remove their detached worktree only when the test cluster area is
empty. Failed runs retain the worktree, source, outputs and cluster directory for
inspection. Use `--keep-work` to retain successful work too. Results are always
retained; cleanup failure changes the overall status to `cleanupIncomplete`.
Retaining work does not bypass the empty-cluster or ownership checks.

Pure launcher checks need no database:

```powershell
py -3.12 -m unittest discover -s tools/native-postgres -p 'test_*.py'
```
