using Npgsql;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCaseLifetimeTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task CompletedCaseDisposalRemovesOnlyItsDatabaseAndIsSequentiallyIdempotent()
    {
        await using var completed = await fixture.CreateCaseAsync();
        await using var unrelated = await fixture.CreateCaseAsync();
        await SeedAsync(completed, "completed case literal ' $1 Ω");
        await SeedAsync(unrelated, "unrelated active case literal ' $1 Ω");
        await using var unrelatedConnection = new NpgsqlConnection(Unpooled(unrelated.MigrationConnectionString));
        await unrelatedConnection.OpenAsync();
        var unrelatedBackend = await ScalarAsync<int>(unrelatedConnection, "SELECT pg_backend_pid()");
        Assert.True(await fixture.DatabaseExistsAsync(completed.Name));
        Assert.True(await fixture.DatabaseExistsAsync(unrelated.Name));

        await completed.DisposeAsync();
        Assert.False(await fixture.DatabaseExistsAsync(completed.Name));
        await completed.DisposeAsync();
        await completed.DisposeAsync();
        Assert.False(await fixture.DatabaseExistsAsync(completed.Name));
        Assert.True(await fixture.DatabaseExistsAsync(unrelated.Name));
        Assert.Equal(unrelatedBackend, await ScalarAsync<int>(unrelatedConnection, "SELECT pg_backend_pid()"));
        await AssertProbeAsync(unrelatedConnection, "unrelated active case literal ' $1 Ω");
        Assert.Equal("unrelated active case literal ' $1 Ω", await unrelated.ScalarAsync<string>("SELECT value FROM braid.case_lifetime_probe WHERE id=1"));
        Assert.Equal(2L, await unrelated.ScalarAsync<long>("SELECT count(*) FROM braid.case_lifetime_probe"));
    }

    [PostgresFact]
    public async Task ClosingOnlyTheOwnerPoolRetainsOriginalDataUntilFinalCaseDisposal()
    {
        await using var test = await fixture.CreateCaseAsync();
        await SeedAsync(test, "retained after owner pool closes");
        await test.CloseOwnerPoolAsync();
        await test.CloseOwnerPoolAsync();
        Assert.True(await fixture.DatabaseExistsAsync(test.Name));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var unexpected = await test.Owner.OpenConnectionAsync();
        });

        await using (var fresh = NpgsqlDataSource.Create(test.MigrationConnectionString))
        {
            await using var connection = await fresh.OpenConnectionAsync();
            Assert.Equal(test.Name, await ScalarAsync<string>(connection, "SELECT current_database()"));
            await AssertProbeAsync(connection, "retained after owner pool closes");
            await test.CloseOwnerPoolAsync();
            Assert.Equal("retained after owner pool closes", await ValueAsync(connection));
        }

        await test.DisposeAsync();
        Assert.False(await fixture.DatabaseExistsAsync(test.Name));
        await test.CloseOwnerPoolAsync();
        await test.DisposeAsync();
        Assert.False(await fixture.DatabaseExistsAsync(test.Name));
    }

    [PostgresFact]
    public async Task UnknownInjectedAndProtectedDatabaseNamesAreRefusedWithoutChangingKnownFacts()
    {
        await using var test = await fixture.CreateCaseAsync();
        await SeedAsync(test, "registered case must remain unchanged");
        var unknown = $"braid_case_{Guid.NewGuid():N}";
        Assert.False(await fixture.DatabaseExistsAsync(unknown));
        Assert.True(await fixture.DatabaseExistsAsync("postgres"));
        Assert.True(await fixture.DatabaseExistsAsync("template0"));
        Assert.True(await fixture.DatabaseExistsAsync("template1"));
        foreach (var name in new[]
        {
            unknown, "postgres", "template0", "template1", "", " ",
            test.Name + "; DROP DATABASE postgres; --", "\"" + test.Name + "\"", test.Name + "' OR true --"
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DropCaseDatabaseAsync(name));
            Assert.True(await fixture.DatabaseExistsAsync(test.Name));
            Assert.Equal("registered case must remain unchanged", await test.ScalarAsync<string>("SELECT value FROM braid.case_lifetime_probe WHERE id=1"));
            Assert.Equal(2L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.case_lifetime_probe"));
        }
        Assert.False(await fixture.DatabaseExistsAsync(unknown));
        Assert.True(await fixture.DatabaseExistsAsync("postgres"));
        Assert.True(await fixture.DatabaseExistsAsync("template0"));
        Assert.True(await fixture.DatabaseExistsAsync("template1"));
    }

    [PostgresFact]
    public async Task LiveSeparateConnectionMakesDisposalFailVisiblyAndClosingItAllowsExplicitRetry()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var unrelated = await fixture.CreateCaseAsync();
        await SeedAsync(test, "blocked case retains original data");
        await SeedAsync(unrelated, "unrelated case survives failed and retried disposal");
        await using var live = new NpgsqlConnection(Unpooled(test.MigrationConnectionString));
        await live.OpenAsync();
        var backend = await ScalarAsync<int>(live, "SELECT pg_backend_pid()");

        var error = await Assert.ThrowsAsync<PostgresException>(() => test.DisposeAsync().AsTask());
        Assert.Equal("55006", error.SqlState);
        Assert.True(await fixture.DatabaseExistsAsync(test.Name));
        Assert.Equal(backend, await ScalarAsync<int>(live, "SELECT pg_backend_pid()"));
        await AssertProbeAsync(live, "blocked case retains original data");
        Assert.True(await fixture.DatabaseExistsAsync(unrelated.Name));
        Assert.Equal("unrelated case survives failed and retried disposal", await unrelated.ScalarAsync<string>("SELECT value FROM braid.case_lifetime_probe WHERE id=1"));

        await live.DisposeAsync();
        await test.DisposeAsync();
        Assert.False(await fixture.DatabaseExistsAsync(test.Name));
        await test.DisposeAsync();
        Assert.False(await fixture.DatabaseExistsAsync(test.Name));
        Assert.True(await fixture.DatabaseExistsAsync(unrelated.Name));
        Assert.Equal("unrelated case survives failed and retried disposal", await unrelated.ScalarAsync<string>("SELECT value FROM braid.case_lifetime_probe WHERE id=1"));
        Assert.Equal(2L, await unrelated.ScalarAsync<long>("SELECT count(*) FROM braid.case_lifetime_probe"));
    }

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task SeedAsync(PostgresCase test, string value)
    {
        await test.ExecuteAsync("CREATE TABLE braid.case_lifetime_probe(id integer PRIMARY KEY,value text NOT NULL)");
        await test.ExecuteAsync("INSERT INTO braid.case_lifetime_probe(id,value) VALUES (1,$1),(2,$2)", value, "second original row");
    }

    private static Task<string> ValueAsync(NpgsqlConnection connection) =>
        ScalarAsync<string>(connection, "SELECT value FROM braid.case_lifetime_probe WHERE id=1");

    private static async Task AssertProbeAsync(NpgsqlConnection connection, string firstValue)
    {
        Assert.Equal(new[] { 1, 2 }, await ScalarAsync<int[]>(connection,
            "SELECT array_agg(id ORDER BY id) FROM braid.case_lifetime_probe"));
        Assert.Equal(new[] { firstValue, "second original row" }, await ScalarAsync<string[]>(connection,
            "SELECT array_agg(value ORDER BY id) FROM braid.case_lifetime_probe"));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected owned case lifetime scalar."));
    }
}
