using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task SeparateRuntimeLoginCannotOwnBypassAssumeOwnerCreateTruncateOrMutateHistory()
    {
        await using var test = await fixture.CreateCaseAsync();
        await PostgresSeedData.CreateAsync(test);
        await using var runtime = NpgsqlDataSource.Create(test.RuntimeConnectionString);
        await using (var identity = runtime.CreateCommand("SELECT current_user || ':' || current_database()"))
            Assert.Equal($"weft_runtime:{test.Name}", await identity.ExecuteScalarAsync());
        Assert.Equal($"weft_migrator:{test.Name}", await test.ScalarAsync<string>("SELECT current_user || ':' || current_database()"));
        await using (var safety = runtime.CreateCommand("""
            SELECT NOT rolsuper AND NOT rolbypassrls AND NOT rolcreaterole AND NOT rolcreatedb
              AND NOT pg_has_role('weft_runtime','weft_migrator','MEMBER')
              AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                              WHERE n.nspname='weft' AND c.relowner=pg_roles.oid)
            FROM pg_roles WHERE rolname=current_user
            """))
            Assert.Equal(true, await safety.ExecuteScalarAsync());
        foreach (var sql in new[]
        {
            "SET ROLE weft_migrator",
            "CREATE SCHEMA runtime_probe",
            "CREATE TABLE weft.runtime_probe(id integer)",
            "TRUNCATE TABLE weft.projects",
            "UPDATE weft.schema_migrations SET checksum=repeat('0',64)",
            "DELETE FROM weft.schema_migrations",
            "INSERT INTO weft.schema_migrations(version,checksum) VALUES (99,repeat('0',64))",
            "UPDATE weft.projects SET name='runtime mutation'"
        })
        {
            await using var forbidden = runtime.CreateCommand(sql);
            var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.projects"));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task RuntimeRefusesOwnerSuperuserBypassAndReachableOwnerMembership()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using (var ownerAsRuntime = new PostgresDatabase(test.MigrationConnectionString))
            await Assert.ThrowsAsync<InvalidOperationException>(() => ownerAsRuntime.BeginTransactionAsync(seed.OrganizationA));
        foreach (var (unsafeSql, restoreSql) in new[]
        {
            ("ALTER ROLE weft_runtime SUPERUSER", "ALTER ROLE weft_runtime NOSUPERUSER"),
            ("ALTER ROLE weft_runtime BYPASSRLS", "ALTER ROLE weft_runtime NOBYPASSRLS"),
            ("GRANT weft_migrator TO weft_runtime", "REVOKE weft_migrator FROM weft_runtime")
        })
        {
            await test.BootstrapAsync(unsafeSql);
            try
            {
                await using var unsafeRuntime = new PostgresDatabase(test.RuntimeConnectionString);
                await Assert.ThrowsAsync<InvalidOperationException>(() => unsafeRuntime.BeginTransactionAsync(seed.OrganizationA));
            }
            finally { await test.BootstrapAsync(restoreSql); }
        }
        await using var restored = new PostgresDatabase(test.RuntimeConnectionString);
        await using var scope = await restored.BeginTransactionAsync(seed.OrganizationA);
        Assert.Equal(2L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.projects"));
    }

    [PostgresFact]
    public async Task MissingOrganizationAndWrongOrganizationReadNoForeignRows()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        await using (var unbound = await database.DataSource.OpenConnectionAsync())
        {
            foreach (var table in new[] { "organizations", "organization_memberships", "projects", "project_memberships" })
            {
                await using var query = new NpgsqlCommand($"SELECT count(*) FROM weft.{table}", unbound);
                Assert.Equal(0L, await query.ExecuteScalarAsync());
            }
        }
        await using var scope = await database.BeginTransactionAsync(seed.OrganizationA);
        Assert.Equal(2L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.projects"));
        Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.projects WHERE organization_id=$1", seed.OrganizationB));
        Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.organizations WHERE id=$1", seed.OrganizationB));
        Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.organization_memberships WHERE organization_id=$1", seed.OrganizationB));
        Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.project_memberships WHERE organization_id=$1", seed.OrganizationB));
    }

    [PostgresFact]
    public async Task CommitReusesTheSameBackendForOrganizationAThenBThenUnboundWithoutContext()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString).MaxPoolSize);
        int first;
        await using (var a = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            first = await ScalarAsync<int>(a, "SELECT pg_backend_pid()");
            Assert.Equal(seed.OrganizationA.ToString("D"), await ScalarAsync<string>(a, "SELECT current_setting('weft.organization_id')"));
            Assert.Equal(2L, await ScalarAsync<long>(a, "SELECT count(*) FROM weft.projects"));
            await a.CommitAsync();
        }
        await using (var b = await database.BeginTransactionAsync(seed.OrganizationB))
        {
            Assert.Equal(first, await ScalarAsync<int>(b, "SELECT pg_backend_pid()"));
            Assert.Equal(seed.OrganizationB.ToString("D"), await ScalarAsync<string>(b, "SELECT current_setting('weft.organization_id')"));
            Assert.Equal(1L, await ScalarAsync<long>(b, "SELECT count(*) FROM weft.projects"));
            await b.CommitAsync();
        }
        await AssertUnboundAsync(database, first);
    }

    [PostgresFact]
    public async Task RollbackAndIncompleteDisposalReuseSameBackendWithoutCarryingOrganizationContext()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        int first;
        await using (var a = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            first = await ScalarAsync<int>(a, "SELECT pg_backend_pid()");
            Assert.Equal(2L, await ScalarAsync<long>(a, "SELECT count(*) FROM weft.projects"));
            await a.RollbackAsync();
        }
        await using (var b = await database.BeginTransactionAsync(seed.OrganizationB))
        {
            Assert.Equal(first, await ScalarAsync<int>(b, "SELECT pg_backend_pid()"));
            Assert.Equal(seed.OrganizationB.ToString("D"), await ScalarAsync<string>(b, "SELECT current_setting('weft.organization_id')"));
            Assert.Equal(1L, await ScalarAsync<long>(b, "SELECT count(*) FROM weft.projects"));
            await b.RollbackAsync();
        }
        await AssertUnboundAsync(database, first);
        await using (var abandoned = await database.BeginTransactionAsync(seed.OrganizationA))
            Assert.Equal(first, await ScalarAsync<int>(abandoned, "SELECT pg_backend_pid()"));
        await AssertUnboundAsync(database, first);
    }

    [PostgresFact]
    public async Task OrganizationBindingIsSingleUseAndConflictingRebindIsRejected()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        await using var scope = await database.BeginTransactionAsync(seed.OrganizationA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(seed.OrganizationB));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(seed.OrganizationA));
        Assert.Equal(seed.OrganizationA.ToString("D"), await ScalarAsync<string>(scope, "SELECT current_setting('weft.organization_id')"));
        Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.projects WHERE organization_id=$1", seed.OrganizationB));
    }

    [PostgresFact]
    public async Task CancellationAndSqlFailureCleanUpSoAnotherOrganizationCanSucceed()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        await using (var canceled = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await using var sleep = canceled.CreateCommand("SELECT pg_sleep(10)");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sleep.ExecuteNonQueryAsync(cancellation.Token));
        }
        var reader = new PostgresProjectReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var scopeB = new ProjectScope(seed.OrganizationB, seed.ProjectB);
        Assert.Equal("Project B", (await reader.GetVisibleProjectAsync(actor, scopeB))!.Name);
        await using (var failed = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            await using var division = failed.CreateCommand("SELECT 1/0");
            var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(ProjectRole.Runner, (await reader.GetAccessAsync(actor, scopeB))!.Role);
        Assert.Equal(seed.ProjectB, Assert.Single((await reader.ListVisibleProjectsAsync(actor, seed.OrganizationB, 10)).Projects).Scope.ProjectId);
        await using var unbound = await database.DataSource.OpenConnectionAsync();
        await using var context = new NpgsqlCommand("SELECT NULLIF(current_setting('weft.organization_id',true),'')", unbound);
        Assert.Equal(DBNull.Value, await context.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task FailedApplicationDatabaseReadIsAnErrorAndDoesNotPoisonTheNextScopedCall()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        await test.ExecuteAsync("REVOKE SELECT ON weft.project_memberships FROM weft_runtime");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetVisibleProjectAsync(actor, new ProjectScope(seed.OrganizationA, seed.ProjectA)));
        }
        finally { await test.ExecuteAsync("GRANT SELECT ON weft.project_memberships TO weft_runtime"); }
        Assert.Equal("Project B", (await reader.GetVisibleProjectAsync(actor, new ProjectScope(seed.OrganizationB, seed.ProjectB)))!.Name);
    }

    [PostgresFact]
    public async Task AdditionalPermissiveOrWeakenedOrganizationPoliciesRejectFreshRuntimeReads()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresDatabase(test.RuntimeConnectionString);
        var reader = new PostgresProjectReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var scope = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        Assert.Equal("Project A", (await reader.GetVisibleProjectAsync(actor, scope))!.Name);
        foreach (var (weaken, restore) in new[]
        {
            ("CREATE POLICY extra_unscoped_probe ON weft.projects USING (true)",
             "DROP POLICY extra_unscoped_probe ON weft.projects"),
            ("ALTER POLICY projects_organization_scope ON weft.projects USING (true) WITH CHECK (true)",
             "ALTER POLICY projects_organization_scope ON weft.projects USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid) WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)")
        })
        {
            await test.ExecuteAsync(weaken);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetVisibleProjectAsync(actor, scope));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListVisibleProjectsAsync(actor, seed.OrganizationA, 10));
                await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetAccessAsync(actor, scope));
            }
            finally { await test.ExecuteAsync(restore); }
            Assert.Equal("Project A", (await reader.GetVisibleProjectAsync(actor, scope))!.Name);
            Assert.Equal("Project B", (await reader.GetVisibleProjectAsync(actor, new ProjectScope(seed.OrganizationB, seed.ProjectB)))!.Name);
        }
    }

    private static async Task<T> ScalarAsync<T>(PostgresTransaction transaction, string sql, params object[] values)
    {
        await using var query = transaction.CreateCommand(sql);
        foreach (var value in values)
            query.Parameters.Add(new NpgsqlParameter { Value = value });
        return (T)(await query.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected scoped probe scalar."));
    }

    private static async Task AssertUnboundAsync(PostgresDatabase database, int expectedBackend)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var backend = new NpgsqlCommand("SELECT pg_backend_pid()", connection))
            Assert.Equal(expectedBackend, await backend.ExecuteScalarAsync());
        await using (var context = new NpgsqlCommand("SELECT NULLIF(current_setting('weft.organization_id',true),'')", connection))
            Assert.Equal(DBNull.Value, await context.ExecuteScalarAsync());
        await using var projects = new NpgsqlCommand("SELECT count(*) FROM weft.projects", connection);
        Assert.Equal(0L, await projects.ExecuteScalarAsync());
    }
}
