using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class OperationsAdministrationIntegrationTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Administration_reads_writes_and_redemption_admit_the_exact_installed_profile(bool operations)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        var version = MigrationRunner.EmbeddedMigrations.Single(migration =>
            migration.Checksum == RegressionSchemaTestContract.ReleaseOperationsChecksum).Version;
        await using (var migration = new MigrationRunner(test.MigrationConnectionString,
            MigrationRunner.EmbeddedMigrations.Take(operations ? version : version - 1).ToArray()))
            await migration.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var tenant = Guid.NewGuid();
        await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString,
            seed.OrganizationA, seed.Alice, tenant);
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("weft_administration"));
        var actor = new ActorContext(new(seed.Alice));
        var snapshot = await store.ReadOrganizationAsync(actor, seed.OrganizationA, default);
        Assert.NotNull(snapshot);
        var secret = OrganizationInvitationSecret.Create();
        var objectId = Guid.NewGuid();
        var outcome = await store.WriteAsync(actor, seed.OrganizationA, null, snapshot.Version,
            AdministrationOperation.Invite, Guid.NewGuid(), JsonSerializer.SerializeToElement(new
            {
                tenantId = tenant,
                expectedObjectId = objectId,
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                projectRoles = Array.Empty<object>(),
                secretHash = OrganizationInvitationSecret.Hash(secret)
            }), default);
        Assert.Equal("updated", outcome);
        Assert.True(await store.RedeemAsync(seed.OrganizationA, secret, EntraUserIdentity.Key(tenant, objectId), default));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.administration_audit WHERE operation='inviteRedeem'"));
    }

    [RegressionPostgresTheory]
    [InlineData("ALTER TABLE weft.local_release_targets DISABLE TRIGGER release_target_capture")]
    [InlineData("ALTER FUNCTION weft.capture_release_target() SECURITY DEFINER")]
    public async Task Administration_refuses_drift_in_the_installed_operations_profile(string change)
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString,
            seed.OrganizationA, seed.Alice, Guid.NewGuid());
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("weft_administration"));
        var actor = new ActorContext(new(seed.Alice));
        Assert.NotNull(await store.ReadOrganizationAsync(actor, seed.OrganizationA, default));
        await test.ExecuteAsync(change);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadOrganizationAsync(actor, seed.OrganizationA, default));
    }
}
