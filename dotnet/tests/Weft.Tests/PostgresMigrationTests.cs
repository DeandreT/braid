using Npgsql;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMigrationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task EmptyInstallationAndRepeatedMigrationPreserveOriginalFactsAndJournal()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var applied = await test.ScalarAsync<DateTime>("SELECT applied_at FROM weft.schema_migrations WHERE version=1");
        var checksum = await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1");
        Assert.Matches("^[0-9a-f]{64}$", checksum);
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        await test.MigrateAsync();
        await test.MigrateAsync();
        Assert.Equal(applied, await test.ScalarAsync<DateTime>("SELECT applied_at FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal(checksum, await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM weft.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
        Assert.Equal("viewer", await test.ScalarAsync<string>("SELECT role FROM weft.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.projects"));
    }

    [PostgresFact]
    public async Task ConcurrentInitializersSerializeOneAtomicInstallation()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        await PostgresSeedData.CreateAsync(test);
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.projects"));
    }

    [PostgresFact]
    public async Task ExactAppliedChecksumTamperingIsRefusedWithoutChangingFacts()
    {
        await using var test = await fixture.CreateCaseAsync();
        await PostgresSeedData.CreateAsync(test);
        var original = await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1");
        var tampered = (original[0] == '0' ? "1" : "0") + original[1..];
        await test.ExecuteAsync("UPDATE weft.schema_migrations SET checksum=$1 WHERE version=1", tampered);
        await Assert.ThrowsAsync<InvalidOperationException>(test.MigrateAsync);
        Assert.Equal(tampered, await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.projects"));
    }

    [PostgresFact]
    public async Task UnknownAppliedVersionIsRefusedWithoutAdoptingIt()
    {
        await using var test = await fixture.CreateCaseAsync();
        await test.ExecuteAsync("INSERT INTO weft.schema_migrations(version,checksum) VALUES (99,$1)", new string('a', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(test.MigrateAsync);
        Assert.Equal(2L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task HoleInOtherwiseKnownMigrationHistoryIsRefused()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        MigrationScript[] scripts = [.. MigrationRunner.EmbeddedMigrations, new(2, "CREATE TABLE weft.second_probe(id integer PRIMARY KEY);")];
        await using var runner = new MigrationRunner(test.MigrationConnectionString, scripts);
        await runner.MigrateAsync();
        await test.ExecuteAsync("DELETE FROM weft.schema_migrations WHERE version=1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.Equal(2, await test.ScalarAsync<int>("SELECT version FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task UnversionedOwnedSchemaIsNeverAdopted()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await test.ExecuteAsync("CREATE SCHEMA weft; CREATE TABLE weft.unversioned_probe(id integer PRIMARY KEY); INSERT INTO weft.unversioned_probe VALUES (7)");
        await Assert.ThrowsAsync<InvalidOperationException>(test.MigrateAsync);
        Assert.Equal(7, await test.ScalarAsync<int>("SELECT id FROM weft.unversioned_probe"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema='weft' AND table_name='schema_migrations'"));
    }

    [PostgresFact]
    public async Task FailedScriptRollsBackSchemaAndJournalTogether()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var broken = new MigrationRunner(test.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations, new MigrationScript(2, "CREATE TABLE weft.failed_probe(id integer PRIMARY KEY); INSERT INTO weft.failed_probe VALUES (7); SELECT 1/0;")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.schemata WHERE schema_name='weft'"));
        await test.MigrateAsync();
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task RuntimeCannotInvokeMigrationEntryPoint()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var runner = new MigrationRunner(test.RuntimeConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }
}
