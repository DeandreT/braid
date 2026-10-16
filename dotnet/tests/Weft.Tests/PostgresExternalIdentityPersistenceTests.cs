using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresExternalIdentityPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task FreshAndRepeatedMigrationPreserveJournalTimesAndAcknowledgedIdentityFacts()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var binding = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        var times = await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        await AssertHistoryAsync(test.Case);
        await test.Case.MigrateAsync();
        await test.Case.MigrateAsync();
        await AssertHistoryAsync(test.Case);
        Assert.Equal(times, await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations"));
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        PostgresExternalIdentityTestSupport.SameBinding(binding,
            PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Existing, (await test.Owner.BindAsync(key, principal)).Outcome);
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromActualVersionSevenPreservesEveryExistingTableRowAndOlderRuntimeRead()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var historical = new MigrationRunner(test.MigrationConnectionString,
            MigrationRunner.EmbeddedMigrations.Take(7).ToArray()))
            await historical.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, 7), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        var seed = await PostgresSeedData.CreateAsync(test);
        // These are genuine pre-0008 ownership rows. This test does not manufacture
        // native/report objects or claim their bytes were exercised during upgrade.
        await test.ExecuteAsync("INSERT INTO weft.organization_project_creation_capabilities(organization_id,principal_id,can_create_projects) VALUES($1,$2,true)", seed.OrganizationA, seed.Admitted);
        await using var oldConnection = PostgresExternalIdentityTestSupport.UnpooledConnection(test.MigrationConnectionString);
        await oldConnection.OpenAsync();
        var conservation = await PostgresExternalIdentityUpgradeConservation.CaptureSevenAsync(oldConnection);
        var names = await test.ScalarAsync<string[]>("""
            SELECT array_agg(c.relname::text ORDER BY c.relname)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='weft' AND c.relkind='r'
            """);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
            rows.Add(name, await PostgresExternalIdentityTestSupport.TableRowsAsync(oldConnection, name));
        var journal = await test.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        await using (var beforeDatabase = new PostgresExternalIdentityDatabase(test.IdentityConnectionString))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresExternalIdentityReader(beforeDatabase).ResolveAsync(PostgresExternalIdentityTestSupport.Key()));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString,
                MigrationRunner.EmbeddedMigrations.Take(8).ToArray());
            await runner.MigrateAsync();
        }));
        await AssertIdentityPrefixHistoryAsync(test);
        await conservation.AssertEightPreservedAsync(oldConnection);
        Assert.Equal(journal, await test.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations WHERE version<=7"));
        foreach (var name in names.Where(name => name != "schema_migrations"))
            Assert.True(string.Equals(rows[name], await PostgresExternalIdentityTestSupport.TableRowsAsync(oldConnection, name), StringComparison.Ordinal), "A private historical row snapshot changed.");
        await using var runtime = new PostgresDatabase(test.RuntimeConnectionString);
        var project = await new PostgresProjectReader(runtime).GetVisibleProjectAsync(new(new PrincipalId(seed.Alice)), new(seed.OrganizationA, seed.ProjectA));
        Assert.NotNull(project);
        Assert.Equal(new ProjectScope(seed.OrganizationA, seed.ProjectA), project.Scope);
        // Complete the strict original eight-script conservation before applying
        // any later supplement. The current owner still requires full history.
        await conservation.AssertEightPreservedAsync(oldConnection);
        await test.MigrateAsync();
        await AssertHistoryAsync(test);
        await using var owner = new ExternalIdentityBindingProvisioner(test.MigrationConnectionString);
        var bound = await owner.BindAsync(PostgresExternalIdentityTestSupport.Key(), new PrincipalId(seed.Admitted));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Created, bound.Outcome);
        await using var database = new PostgresExternalIdentityDatabase(test.IdentityConnectionString);
        PostgresExternalIdentityTestSupport.SameBinding(Assert.IsType<ExternalIdentityBinding>(bound.Binding),
            PostgresExternalIdentityTestSupport.Resolved(await new PostgresExternalIdentityReader(database).ResolveBindingAsync(bound.Binding!.BindingId)));
        foreach (var name in names.Where(name => name != "schema_migrations"))
            Assert.True(string.Equals(rows[name], await PostgresExternalIdentityTestSupport.TableRowsAsync(oldConnection, name), StringComparison.Ordinal), "A private historical row snapshot changed.");
        await AssertHistoryAsync(test);
    }

    [PostgresFact]
    public async Task RecreatedPoolsAndSameClusterRestartRetainOriginalBindingAndClosedActivityStates()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var activePrincipal = await test.PrincipalAsync();
        var inactivePrincipal = await test.PrincipalAsync();
        var activeKey = PostgresExternalIdentityTestSupport.Key("active");
        var disabledKey = PostgresExternalIdentityTestSupport.Key("disabled");
        var inactiveKey = PostgresExternalIdentityTestSupport.Key("inactive");
        var active = await test.BindAsync(activeKey, activePrincipal);
        var disabledOriginal = await test.BindAsync(disabledKey, activePrincipal);
        var inactive = await test.BindAsync(inactiveKey, inactivePrincipal);
        var disabledResult = await test.Owner.DisableAsync(disabledKey, activePrincipal, disabledOriginal.Version);
        var disabled = Assert.IsType<ExternalIdentityBinding>(disabledResult.Binding);
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", inactivePrincipal.Value);
        var readerConnection = test.Case.IdentityConnectionString;
        var ownerConnection = test.Case.MigrationConnectionString;
        var before = await test.BindingsAsync();
        await test.Database.DisposeAsync();
        await test.Owner.DisposeAsync();
        await using (var recreated = new PostgresExternalIdentityDatabase(readerConnection))
        {
            var reader = new PostgresExternalIdentityReader(recreated);
            PostgresExternalIdentityTestSupport.SameBinding(active, PostgresExternalIdentityTestSupport.Resolved(await reader.ResolveAsync(activeKey)));
            PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await reader.ResolveAsync(disabledKey));
            PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.PrincipalInactive, await reader.ResolveAsync(inactiveKey));
        }
        await test.Case.CloseOwnerPoolAsync();
        // The fixture stops/starts its original exact container and persistent
        // volume; no replacement cluster or migration rerun is used as evidence.
        await fixture.RestartAsync();
        await using var reopened = new PostgresExternalIdentityDatabase(readerConnection);
        var retainedReader = new PostgresExternalIdentityReader(reopened);
        PostgresExternalIdentityTestSupport.SameBinding(active, PostgresExternalIdentityTestSupport.Resolved(await retainedReader.ResolveBindingAsync(active.BindingId)));
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await retainedReader.ResolveBindingAsync(disabled.BindingId));
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.PrincipalInactive, await retainedReader.ResolveBindingAsync(inactive.BindingId));
        await using var retainedOwner = new ExternalIdentityBindingProvisioner(ownerConnection);
        var replay = await retainedOwner.BindAsync(disabledKey, activePrincipal);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Existing, replay.Outcome);
        PostgresExternalIdentityTestSupport.SameBinding(disabled, Assert.IsType<ExternalIdentityBinding>(replay.Binding));
        await using var actualOwner = PostgresExternalIdentityTestSupport.UnpooledConnection(ownerConnection);
        await actualOwner.OpenAsync();
        Assert.True(string.Equals(before, await PostgresExternalIdentityTestSupport.TableRowsAsync(actualOwner, "external_identity_bindings"), StringComparison.Ordinal), "The private binding snapshot changed after restart.");
    }

    private static async Task AssertHistoryAsync(PostgresCase test)
    {
        Assert.Equal(Enumerable.Range(1, 9), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(MigrationRunner.EmbeddedMigrations.Select(migration => migration.Checksum),
            await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
    }

    private static async Task AssertIdentityPrefixHistoryAsync(PostgresCase test)
    {
        Assert.Equal(Enumerable.Range(1, 8), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(MigrationRunner.EmbeddedMigrations.Take(8).Select(migration => migration.Checksum),
            await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
    }
}
