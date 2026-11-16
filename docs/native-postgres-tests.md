# Native Windows database qualification

The database test fixture can use either its pinned Linux Docker image or an
explicit native Windows PostgreSQL installation. Native mode exercises the real
server, migrations, grants, transactions and restart behavior.

Set `BRAID_POSTGRES_BIN` to an absolute directory containing PostgreSQL 16.15
`postgres.exe`, `initdb.exe` and `pg_ctl.exe`. Every executable must report that
exact version. Set `BRAID_POSTGRES_WORK_ROOT` to an existing absolute directory
for disposable clusters, or leave it unset to use the current process's temporary
directory. Do not combine native mode with `BRAID_POSTGRES_IMAGE`,
`BRAID_TEST_CLIENT_CONTAINER` or `BRAID_DOCKER_EXECUTABLE`; mixed configuration fails.

```powershell
$env:BRAID_POSTGRES_BIN = 'C:/tools/postgresql-16.15/pgsql/bin'
$env:BRAID_POSTGRES_WORK_ROOT = 'C:/temp/braid-tests'
dotnet test dotnet/tests/Braid.Tests/Braid.Tests.csproj --filter 'FullyQualifiedName~PostgresOwnershipTests'
```

Use the repository's pinned SDK and isolated native build outputs. Installing
PostgreSQL is an explicit setup operation, not a test side effect. Prefer the
vendor's verified portable binaries; no Windows service or global PostgreSQL
configuration is required or changed by the fixture.

For a clean source snapshot, short isolated output paths, controlled child
environment and retained source/TRX receipts, use the
[native qualification launcher](../tools/native-postgres/README.md). It wraps
this same fixture rather than introducing another database implementation.

Each fixture creates its own marked data directory, bootstrap identity and
random test passwords. It starts PostgreSQL as an owned child on an unused
loopback port, disables statement logging, and verifies the server's version,
listen address and exact data directory. The short-lived init password file is
removed after initialization. Existing restricted application roles, migrations
and per-case databases are reused unchanged.

Native connection setup has a 15-second allowance for Windows to create each
backend process. This does not retry failed operations or change the independent
query/lock deadlines; the Docker transport retains its shorter connection budget.

Restart uses fast shutdown and the same data and port. Before asking `pg_ctl`
to stop anything, the host verifies the directory marker, live child handle and
postmaster PID/data path. On failure it can terminate only the process tree it
started; it never targets an arbitrary PID read from disk. Cleanup refuses to
delete a directory without its exact ownership marker and propagates stop or
deletion failures. A failed cleanup may deliberately leave owned data for
inspection rather than remove an unverifiable path.

Forcibly terminating the test runner bypasses normal disposal. If that happens,
inspect the exact marked cluster and its postmaster PID before stopping it with
the same PostgreSQL installation. Never stop all `postgres.exe` processes or
delete a broad temporary directory to recover one fixture.

Generated test credentials, PostgreSQL output and data files are private local
artifacts. Do not publish them as evidence or add them to the repository.
