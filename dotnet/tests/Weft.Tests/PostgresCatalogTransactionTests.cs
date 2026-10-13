using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task CommitReusesOneBackendAcrossOrganizationsThenReturnsAnUnboundConnection()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString).MaxPoolSize);
        int backend;
        await using (var a = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(a, "SELECT pg_backend_pid()");
            Assert.Equal(seed.OrganizationA.ToString("D"), await ScalarAsync<string>(a, "SHOW weft.organization_id"));
            Assert.Equal(2L, await ScalarAsync<long>(a, "SELECT count(*) FROM weft.projects"));
            Assert.Equal(0L, await ScalarAsync<long>(a, "SELECT count(*) FROM weft.projects WHERE organization_id=$1", seed.OrganizationB));
            await a.CommitAsync();
        }
        await using (var b = await database.BeginTransactionAsync(seed.OrganizationB))
        {
            Assert.Equal(backend, await ScalarAsync<int>(b, "SELECT pg_backend_pid()"));
            Assert.Equal(seed.OrganizationB.ToString("D"), await ScalarAsync<string>(b, "SHOW weft.organization_id"));
            Assert.Equal(1L, await ScalarAsync<long>(b, "SELECT count(*) FROM weft.projects"));
            Assert.Equal(0L, await ScalarAsync<long>(b, "SELECT count(*) FROM weft.projects WHERE organization_id=$1", seed.OrganizationA));
            await b.CommitAsync();
        }
        await AssertUnboundAsync(database, backend);
    }

    [PostgresFact]
    public async Task RollbackAndIncompleteDisposalReuseTheBackendWithoutOrganizationOrTransactionSettings()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        int backend;
        await using (var a = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(a, "SELECT pg_backend_pid()");
            Assert.Equal(2L, await ScalarAsync<long>(a, "SELECT count(*) FROM weft.projects"));
            await a.RollbackAsync();
        }
        await AssertUnboundAsync(database, backend);
        await using (var b = await database.BeginTransactionAsync(seed.OrganizationB))
        {
            Assert.Equal(backend, await ScalarAsync<int>(b, "SELECT pg_backend_pid()"));
            Assert.Equal(seed.OrganizationB.ToString("D"), await ScalarAsync<string>(b, "SHOW weft.organization_id"));
            Assert.Equal(1L, await ScalarAsync<long>(b, "SELECT count(*) FROM weft.projects"));
            await b.RollbackAsync();
        }
        await AssertUnboundAsync(database, backend);
        await using (var abandoned = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            Assert.Equal(backend, await ScalarAsync<int>(abandoned, "SELECT pg_backend_pid()"));
            Assert.Equal(seed.OrganizationA.ToString("D"), await ScalarAsync<string>(abandoned, "SHOW weft.organization_id"));
        }
        await AssertUnboundAsync(database, backend);
    }

    [PostgresFact]
    public async Task ReadOnlyScopeHasBoundedLocalTimeoutsAndOrganizationBindingIsExactlyOnce()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        int backend;
        await using (var scope = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(scope, "SELECT pg_backend_pid()");
            Assert.Equal("on", await ScalarAsync<string>(scope, "SHOW transaction_read_only"));
            Assert.Equal("1s", await ScalarAsync<string>(scope, "SHOW lock_timeout"));
            Assert.Equal("5s", await ScalarAsync<string>(scope, "SHOW statement_timeout"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(seed.OrganizationB));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(seed.OrganizationA));
            Assert.Equal(seed.OrganizationA.ToString("D"), await ScalarAsync<string>(scope, "SHOW weft.organization_id"));
            Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.projects WHERE organization_id=$1", seed.OrganizationB));
            await scope.CommitAsync();
            Assert.Throws<InvalidOperationException>(() => scope.CreateCommand("SELECT 1"));
        }
        await AssertUnboundAsync(database, backend);
    }

    [PostgresFact]
    public async Task CancellationAndSqlFailureReuseTheSameBackendAndPermitTheNextScopedRead()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        int backend;
        await using (var canceled = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(canceled, "SELECT pg_backend_pid()");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await using var sleep = canceled.CreateCommand("SELECT pg_sleep(10)");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sleep.ExecuteNonQueryAsync(cancellation.Token));
        }
        await AssertUnboundAsync(database, backend);
        var reader = new PostgresCatalogReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        var projectB = new ProjectScope(seed.OrganizationB, seed.ProjectB);
        Assert.Equal("Project B", (await reader.GetProjectAsync(actor, projectB))!.Summary.Name);
        await using (var failed = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            Assert.Equal(backend, await ScalarAsync<int>(failed, "SELECT pg_backend_pid()"));
            await using var division = failed.CreateCommand("SELECT 1/0");
            var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        await AssertUnboundAsync(database, backend);
        var page = Assert.IsType<CatalogProjectPage>(await reader.ListProjectsAsync(actor, seed.OrganizationB, 10));
        Assert.Equal(seed.ProjectB, Assert.Single(page.Projects).Summary.Scope.ProjectId);
        Assert.Equal(ProjectRole.Runner, Assert.Single(page.Projects).Access.Role);
        await AssertUnboundAsync(database, backend);
    }

    [PostgresFact]
    public async Task RejectedCatalogAdmissionReleasesTheOnlyConnectionWithoutBindingAndRecoversAfterRestore()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using var database = new PostgresCatalogDatabase(test.CatalogConnectionString);
        var reader = new PostgresCatalogReader(database);
        var actor = new ActorContext(new PrincipalId(seed.Alice));
        int backend;
        await using (var scope = await database.BeginTransactionAsync(seed.OrganizationA))
        {
            backend = await ScalarAsync<int>(scope, "SELECT pg_backend_pid()");
            await scope.CommitAsync();
        }
        await test.ExecuteAsync("REVOKE SELECT ON weft.project_memberships FROM weft_catalog_reader");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetProjectAsync(actor, new ProjectScope(seed.OrganizationA, seed.ProjectA)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListProjectsAsync(actor, seed.OrganizationA, 10));
            await AssertUnboundAsync(database, backend, inspectProjectMemberships: false);
        }
        finally { await test.ExecuteAsync("GRANT SELECT ON weft.project_memberships TO weft_catalog_reader"); }
        Assert.Equal("Project B", (await reader.GetProjectAsync(actor, new ProjectScope(seed.OrganizationB, seed.ProjectB)))!.Summary.Name);
        await AssertUnboundAsync(database, backend);
    }

    private static async Task<T> ScalarAsync<T>(PostgresTransaction transaction, string sql, params object[] values)
    {
        await using var command = transaction.CreateCommand(sql);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected catalog transaction scalar."));
    }

    private static async Task AssertUnboundAsync(PostgresCatalogDatabase database, int expectedBackend,
        bool inspectProjectMemberships = true)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var backend = new NpgsqlCommand("SELECT pg_backend_pid()", connection))
            Assert.Equal(expectedBackend, await backend.ExecuteScalarAsync());
        await using (var context = new NpgsqlCommand("SELECT NULLIF(current_setting('weft.organization_id',true),'')", connection))
            Assert.Equal(DBNull.Value, await context.ExecuteScalarAsync());
        foreach (var (setting, expected) in new[] { ("transaction_read_only", "off"), ("lock_timeout", "0"), ("statement_timeout", "0") })
        {
            await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
            Assert.Equal(expected, await command.ExecuteScalarAsync());
        }
        foreach (var table in new[] { "organizations", "organization_memberships", "projects", "project_memberships", "mappings", "mapping_revisions" })
        {
            if (!inspectProjectMemberships && table == "project_memberships")
                continue;
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM weft.{table}", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        await using var reservations = new NpgsqlCommand("SELECT count(operation_id) FROM weft.revision_ingestions", connection);
        Assert.Equal(0L, await reservations.ExecuteScalarAsync());
    }
}
