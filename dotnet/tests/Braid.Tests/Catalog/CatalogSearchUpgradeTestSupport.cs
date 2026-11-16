using Braid.Persistence.Postgres;

namespace Braid.Tests;

internal static class CatalogSearchUpgradeTestSupport
{
    // Search conservation observes the exact search installation, not a later
    // suffix that deliberately replaces existing document/profile functions.
    internal static IReadOnlyList<MigrationScript> SearchMigrations
    {
        get
        {
            var scripts = MigrationRunner.EmbeddedMigrations.Take(29).ToArray();
            Assert.Equal(Enumerable.Range(1, 29), scripts.Select(script => script.Version));
            Assert.Equal(CatalogSearchSchemaAdmission.MigrationChecksum, scripts[^1].Checksum);
            return scripts;
        }
    }

    internal static async Task MigrateSearchAsync(PostgresCase test)
    {
        var scripts = SearchMigrations;
        await using var runner = new MigrationRunner(test.MigrationConnectionString, scripts);
        await runner.MigrateAsync();
        Assert.Equal(scripts.Select(script => script.Version),
            await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(scripts.Select(script => script.Checksum),
            await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM braid.schema_migrations"));
    }

    // A conservation snapshot may exclude the additive search objects only after
    // their complete installed definitions have passed the runtime admission.
    internal static async Task<bool> AdmitAsync(PostgresCase test)
    {
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        return await CatalogSearchSchemaAdmission.ValidateAsync(connection, transaction, default);
    }
}
