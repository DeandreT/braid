using Npgsql;
using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LocalReleaseSchemaTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Local_schema_is_exact_and_has_separate_credentials()
    {
        PostgresCase test;
        try { test = await fixture.CreateCaseAsync(); }
        catch (PostgresException error)
        {
            output.WriteLine($"Schema diagnostic: {error.SqlState}; position={error.Position}; internal={error.InternalPosition}; context={error.Where}");
            throw;
        }
        await using var owned = test;
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, LocalReleaseRoleGuard.Names,
            BuildRoleGuard.LoadFunctions("0022_local_releases.sql", LocalReleaseRoleGuard.Names.Length), default);
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, LocalReleaseRoleGuard.Tables, default);
        output.WriteLine("LocalReleaseFingerprint=" + fingerprint);
        Assert.Equal(ReleaseOperationsRoleGuard.ReleaseFingerprint, fingerprint);
    }

    [PostgresFact]
    public async Task Missing_credentials_preserve_schema21()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionLocalReleases: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(21).ToArray()))
            await prefix.MigrateAsync();
        await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
        Assert.Equal(21L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
    }
}
