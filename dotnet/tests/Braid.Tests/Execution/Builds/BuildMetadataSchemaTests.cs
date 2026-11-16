using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildMetadataSchemaTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Fresh_build_metadata_schema_has_the_exact_qualified_contract()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new Npgsql.NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        var publication = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRoleGuard.Tables, default);
        var runs = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRunRoleGuard.Tables, default);
        output.WriteLine("PublicationFingerprint=" + publication);
        output.WriteLine("RunFingerprint=" + runs);
        Assert.Equal(BuildMetadataRoleGuard.PublicationFingerprint, publication);
        Assert.Equal(BuildMetadataRoleGuard.RunFingerprint, runs);
        await transaction.CommitAsync();
    }

    [PostgresFact]
    public async Task Missing_metadata_reader_preserves_the_installed_dispatch_and_control_contract()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionBuildMetadata: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(19).ToArray()))
            await prefix.MigrateAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => test.MigrateAsync());
        Assert.Equal(19L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        await using var controls = new PostgresBuildStore(test.RegressionConnectionString("braid_build_reader"), false);
        Assert.Null(await controls.ReadEnvironmentAsync(new(new(Guid.NewGuid())), new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), 1)));
        await using var dispatch = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
        Assert.Empty(await dispatch.DiscoverAsync(1));
    }
}
