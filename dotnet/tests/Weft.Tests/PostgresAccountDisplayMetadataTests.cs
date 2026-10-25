using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresAccountDisplayMetadataTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OptionalUpgradeWorksWithoutProvisioningAnyMetadataCredential()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionAccountDisplay: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(25).ToArray()))
            await old.MigrateAsync();
        Assert.False(await test.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='weft_account_display')"));
        await test.MigrateAsync();
        Assert.True(await test.ScalarAsync<bool>("SELECT pg_catalog.to_regclass('weft.account_display_metadata') IS NOT NULL"));
    }

    [PostgresFact]
    public async Task PersistsAcrossPoolsSeparatesBindingsAndOrganizationsAndNeverRegressesObservation()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var binding = await test.BindAsync(PostgresExternalIdentityTestSupport.Key("display-a"), principal);
        var other = await test.BindAsync(PostgresExternalIdentityTestSupport.Key("display-b"), principal);
        var organization = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        await test.Case.ExecuteAsync("INSERT INTO weft.organizations(id,name) VALUES ($1,'Display A'),($2,'Display B')", organization, organizationB);
        var connection = fixture.AccountDisplayConnectionString(test.Case);
        var now = DateTimeOffset.UtcNow;
        await using (var store = new PostgresAccountDisplayMetadata(connection))
        {
            Assert.Null(await store.ReadAsync(organization, binding.BindingId, default));
            await store.ObserveAsync(organization, binding.BindingId, "New <Name>", now, default);
            await store.ObserveAsync(organization, binding.BindingId, "Old Name", now.AddSeconds(-1), default);
        }
        await using var reopened = new PostgresAccountDisplayMetadata(connection);
        Assert.Equal("New <Name>", await reopened.ReadAsync(organization, binding.BindingId, default));
        Assert.Null(await reopened.ReadAsync(organizationB, binding.BindingId, default));
        Assert.Null(await reopened.ReadAsync(organization, other.BindingId, default));
        await reopened.ObserveAsync(organization, binding.BindingId, null, now.AddSeconds(1), default);
        Assert.Null(await reopened.ReadAsync(organization, binding.BindingId, default));
        await Task.WhenAll(
            reopened.ObserveAsync(organization, binding.BindingId, "Newest", now.AddSeconds(3), default),
            reopened.ObserveAsync(organization, binding.BindingId, "Older", now.AddSeconds(2), default));
        Assert.Equal("Newest", await reopened.ReadAsync(organization, binding.BindingId, default));
        foreach (var sql in new[] { "UPDATE weft.principals SET active=false", "SELECT * FROM weft.server_sessions", "SELECT * FROM weft.external_identity_bindings", "DELETE FROM weft.account_display_metadata", "UPDATE weft.account_display_metadata SET binding_id=gen_random_uuid()" })
            await PostgresExternalIdentityTestSupport.DeniedAsync(connection, sql);
        await using var privileged = new PostgresAccountDisplayMetadata(test.Case.MigrationConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => privileged.ReadAsync(organization, binding.BindingId, default));
        await test.Case.ExecuteAsync("GRANT SELECT(id) ON weft.principals TO weft_account_display");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReadAsync(organization, binding.BindingId, default));
        await test.Case.ExecuteAsync("REVOKE SELECT(id) ON weft.principals FROM weft_account_display");
        Assert.Equal("Newest", await reopened.ReadAsync(organization, binding.BindingId, default));
    }

    [PostgresFact]
    public async Task UpgradeAddsEmptyMetadataWithoutChangingExistingBindingFacts()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(25).ToArray()))
            await old.MigrateAsync();
        var principal = Guid.NewGuid();
        var binding = Guid.NewGuid();
        await test.ExecuteAsync("INSERT INTO weft.principals(id) VALUES ($1)", principal);
        await test.ExecuteAsync("INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES ($1,'test','tenant','subject',$2)", binding, principal);
        var before = await test.ScalarAsync<string>("SELECT row_to_json(b)::text FROM weft.external_identity_bindings b");
        await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string>("SELECT row_to_json(b)::text FROM weft.external_identity_bindings b"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.account_display_metadata"));
    }
}
