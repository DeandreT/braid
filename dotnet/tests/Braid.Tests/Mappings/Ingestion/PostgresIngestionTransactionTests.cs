using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIngestionTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task CommitAndRollbackReuseOneBackendAcrossOrganizationsThenUnboundWithoutContextOrTimeouts()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(test.Database.DataSource.ConnectionString).MaxPoolSize);
        int? backend = null;
        foreach (var commit in new[] { true, false })
        {
            foreach (var (organization, projectCount) in new[] { (test.Seed.OrganizationA, 2L), (test.Seed.OrganizationB, 1L) })
            {
                await using var scope = await test.Database.BeginTransactionAsync(organization);
                var current = await ScalarAsync<int>(scope, "SELECT pg_backend_pid()");
                backend ??= current;
                Assert.Equal(backend, current);
                Assert.Equal(organization.ToString("D"), await ScalarAsync<string>(scope, "SELECT current_setting('braid.organization_id')"));
                Assert.Equal(projectCount, await ScalarAsync<long>(scope, "SELECT count(*) FROM braid.projects"));
                Assert.Equal("1s", await ScalarAsync<string>(scope, "SHOW lock_timeout"));
                Assert.Equal("5s", await ScalarAsync<string>(scope, "SHOW statement_timeout"));
                Assert.Equal("off", await ScalarAsync<string>(scope, "SHOW transaction_read_only"));
                if (commit)
                    await scope.CommitAsync();
                else
                    await scope.RollbackAsync();
            }
            await AssertUnboundAsync(test.Database, backend ?? throw new InvalidOperationException("The test did not lease a backend."));
        }
    }

    [PostgresFact]
    public async Task PresetAndConflictingOrganizationBindingsAreRefusedBeforeScopedUse()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var preset = new NpgsqlConnectionStringBuilder(test.Case.IngestionConnectionString)
        {
            Options = $"-c braid.organization_id={test.Seed.OrganizationB:D}"
        };
        await using (var unsafePool = new PostgresIngestionDatabase(preset.ConnectionString))
            await Assert.ThrowsAsync<InvalidOperationException>(() => unsafePool.BeginTransactionAsync(test.Seed.OrganizationA));
        await using (var scope = await test.Database.BeginTransactionAsync(test.Seed.OrganizationA))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(test.Seed.OrganizationB));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(test.Seed.OrganizationA));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RequireOrganizationAsync(test.Seed.OrganizationB, CancellationToken.None));
            Assert.Equal(test.Seed.OrganizationA.ToString("D"), await ScalarAsync<string>(scope, "SELECT current_setting('braid.organization_id')"));
            await using var changed = scope.CreateCommand("SELECT set_config('braid.organization_id',$1,true)");
            changed.Parameters.AddWithValue(test.Seed.OrganizationB.ToString("D"));
            await changed.ExecuteScalarAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RequireOrganizationAsync(test.Seed.OrganizationA, CancellationToken.None));
        }
        await using var clean = await test.Database.BeginTransactionAsync(test.Seed.OrganizationB);
        Assert.Equal(1L, await ScalarAsync<long>(clean, "SELECT count(*) FROM braid.projects"));
    }

    [PostgresFact]
    public async Task CancellationAndSqlFailureRollbackReuseTheSameBackendAndAnotherOrganizationSucceeds()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        int backend;
        await using (var canceled = await test.Database.BeginTransactionAsync(test.Seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(canceled, "SELECT pg_backend_pid()");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await using var sleep = canceled.CreateCommand("SELECT pg_sleep(10)");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sleep.ExecuteScalarAsync(cancellation.Token));
        }
        await AssertUnboundAsync(test.Database, backend);
        await using (var failed = await test.Database.BeginTransactionAsync(test.Seed.OrganizationA))
        {
            Assert.Equal(backend, await ScalarAsync<int>(failed, "SELECT pg_backend_pid()"));
            await using var division = failed.CreateCommand("SELECT 1/0");
            var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        await AssertUnboundAsync(test.Database, backend);
        var projectB = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var reserved = await test.Store.ReserveAsync(test.Actor, projectB, Guid.NewGuid(), PostgresIngestionTestSupport.CreateIntent(),
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        Assert.Equal(projectB, reserved.Receipt!.Facts.Project);
        await AssertUnboundAsync(test.Database, backend);
    }

    [PostgresFact]
    public async Task IncompleteScopeDisposalAndQueuedCancellationReleaseTheOnlyPooledConnection()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        int backend;
        await using (var active = await test.Database.BeginTransactionAsync(test.Seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(active, "SELECT pg_backend_pid()");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Database.BeginTransactionAsync(test.Seed.OrganizationB, cancellation.Token));
            Assert.Equal(backend, await ScalarAsync<int>(active, "SELECT pg_backend_pid()"));
        }
        await AssertUnboundAsync(test.Database, backend);
        await using var next = await test.Database.BeginTransactionAsync(test.Seed.OrganizationB);
        Assert.Equal(backend, await ScalarAsync<int>(next, "SELECT pg_backend_pid()"));
        Assert.Equal(1L, await ScalarAsync<long>(next, "SELECT count(*) FROM braid.projects"));
    }

    private static async Task<T> ScalarAsync<T>(PostgresIngestionTransaction scope, string sql)
    {
        await using var command = scope.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected ingestion transaction probe scalar."));
    }

    private static async Task AssertUnboundAsync(PostgresIngestionDatabase database, int backend)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var query = new NpgsqlCommand("SELECT pg_backend_pid()", connection))
            Assert.Equal(backend, await query.ExecuteScalarAsync());
        await using (var context = new NpgsqlCommand("SELECT NULLIF(current_setting('braid.organization_id',true),'')", connection))
            Assert.Equal(DBNull.Value, await context.ExecuteScalarAsync());
        foreach (var setting in new[] { "lock_timeout", "statement_timeout" })
        {
            await using var timeout = new NpgsqlCommand($"SHOW {setting}", connection);
            Assert.Equal("0", await timeout.ExecuteScalarAsync());
        }
        foreach (var table in new[] { "organizations", "organization_memberships", "projects", "project_memberships", "mappings", "revision_ingestions", "revision_ingestion_attempts", "mapping_revisions" })
        {
            await using var rows = new NpgsqlCommand($"SELECT count(*) FROM braid.{table}", connection);
            Assert.Equal(0L, await rows.ExecuteScalarAsync());
        }
    }
}
