using Npgsql;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogSearchUpgradePrerequisiteTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RestoringPublicExtensionExecutionAfterMigrationRefusesRuntimeAdmission()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        await test.Case.BootstrapAsync("GRANT EXECUTE ON FUNCTION weft_search_extensions.similarity(text,text) TO PUBLIC");
        var search = new PostgresCatalogSearch(test.Reader);
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(test.Actor, test.Project.OrganizationId, new("orders"), 1));
    }

    [PostgresFact]
    public async Task MissingOrPubliclyExecutableExtensionRefusesTheUpgradeWithoutPartialSearchObjects()
    {
        foreach (var change in new[]
        {
            "DROP EXTENSION pg_trgm",
            "GRANT EXECUTE ON FUNCTION weft_search_extensions.similarity(text,text) TO PUBLIC"
        })
        {
            await using var test = await fixture.CreateCaseAsync(migrate: false);
            await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(28).ToArray()))
                await prefix.MigrateAsync();
            var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM weft.schema_migrations m");
            await test.BootstrapAsync(change);
            var error = await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            Assert.True(await test.ScalarAsync<bool>("SELECT to_regclass('weft.catalog_search_report_index') IS NULL AND to_regclass('weft.catalog_search_reports') IS NULL"));
            Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM weft.schema_migrations m"));
        }
    }
}
