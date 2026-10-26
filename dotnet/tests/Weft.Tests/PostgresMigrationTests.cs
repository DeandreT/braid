using Npgsql;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMigrationTests(PostgresFixture fixture)
{
    private const string OwnershipChecksum = "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1";
    private const string IngestionChecksum = "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5";
    private const string CatalogChecksum = "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3";
    private const string MetadataChecksum = "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113";
    private static string ProjectCreationChecksum => ProjectCreationTestSupport.MigrationChecksum;
    private static string RevisionDerivationChecksum => RevisionDerivationSchemaContract.MigrationChecksum;
    private static string MaintenanceChecksum => MaintenanceSchemaContract.MigrationChecksum;
    private static string ExternalIdentityChecksum => ExternalIdentitySchemaContract.MigrationChecksum;
    private static string PayloadPermissionChecksum => PayloadPermissionSchemaContract.MigrationChecksum;
    private static string ServerSessionChecksum => ServerSessionSchemaContract.MigrationChecksum;
    private static string ChatChecksum => ChatSchemaContract.MigrationChecksum;
    private static string PreviewChecksum => PreviewSchemaTestContract.MigrationChecksum;
    private static string PreviewMessagesChecksum => PreviewSchemaTestContract.MessagesMigrationChecksum;
    private static string RegressionCasesChecksum => RegressionSchemaTestContract.CasesChecksum;
    private static string RegressionRunsChecksum => RegressionSchemaTestContract.RunsChecksum;
    private static string[] CurrentChecksums => [OwnershipChecksum, IngestionChecksum, CatalogChecksum, MetadataChecksum,
        ProjectCreationChecksum, RevisionDerivationChecksum, MaintenanceChecksum, ExternalIdentityChecksum, PayloadPermissionChecksum,
        ServerSessionChecksum, ChatChecksum, PreviewChecksum, PreviewMessagesChecksum, RegressionCasesChecksum, RegressionRunsChecksum,
        RegressionSchemaTestContract.DispatchChecksum, RegressionSchemaTestContract.HistoryChecksum, RegressionSchemaTestContract.BuildChecksum, RegressionSchemaTestContract.BuildDispatchChecksum, RegressionSchemaTestContract.BuildMetadataChecksum, RegressionSchemaTestContract.QualificationChecksum, RegressionSchemaTestContract.LocalReleaseChecksum,
        MaintenanceSchemaContract.StoreKindMigrationChecksum, RegressionSchemaTestContract.MachineInvocationChecksum,
        RegressionSchemaTestContract.ApiRequestAuditChecksum,
        RegressionSchemaTestContract.AccountDisplayChecksum, RegressionSchemaTestContract.AdministrationChecksum, InterchangeSchemaTestContract.MigrationChecksum];
    [PostgresFact]
    public async Task EmptyInstallationAndRepeatedMigrationPreserveOriginalFactsAndJournal()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var applied = await test.ScalarAsync<DateTime>("SELECT applied_at FROM weft.schema_migrations WHERE version=1");
        var checksum = await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1");
        Assert.Equal(OwnershipChecksum, checksum);
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
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
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
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
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count + 1, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task HoleInOtherwiseKnownMigrationHistoryIsRefused()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        var nextVersion = checked(MigrationRunner.EmbeddedMigrations[^1].Version + 1);
        MigrationScript[] scripts = [.. MigrationRunner.EmbeddedMigrations, new(nextVersion, "CREATE TABLE weft.future_probe(id integer PRIMARY KEY);")];
        await using var runner = new MigrationRunner(test.MigrationConnectionString, scripts);
        await runner.MigrateAsync();
        await test.ExecuteAsync("DELETE FROM weft.schema_migrations WHERE version=1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
        Assert.Equal(scripts.Skip(1).Select(script => script.Version), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
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
        var nextVersion = checked(MigrationRunner.EmbeddedMigrations[^1].Version + 1);
        await using (var broken = new MigrationRunner(test.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations, new MigrationScript(nextVersion, "CREATE TABLE weft.failed_probe(id integer PRIMARY KEY); INSERT INTO weft.failed_probe VALUES (7); SELECT 1/0;")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.schemata WHERE schema_name='weft'"));
        await test.MigrateAsync();
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task RuntimeCannotInvokeMigrationEntryPoint()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using var runner = new MigrationRunner(test.RuntimeConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
        await using var ingestion = new MigrationRunner(test.IngestionConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ingestion.MigrateAsync());
        await using var creator = new MigrationRunner(test.ProjectCreationConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => creator.MigrateAsync());
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromUnchangedOwnershipMigrationPreservesOriginalFacts()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(1).ToArray()))
            await old.MigrateAsync();
        Assert.Equal(OwnershipChecksum, await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        var seed = await PostgresSeedData.CreateAsync(test);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(OwnershipChecksum, await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM weft.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
        Assert.Equal("viewer", await test.ScalarAsync<string>("SELECT role FROM weft.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.mappings"));
    }

    [PostgresFact]
    public async Task FailedUpgradeRollsBackOnlyTheNewInstallationAndPreservesOwnershipJournalAndFacts()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(1).ToArray()))
            await old.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var applied = await test.ScalarAsync<DateTime>("SELECT applied_at FROM weft.schema_migrations WHERE version=1");
        var nextVersion = checked(MigrationRunner.EmbeddedMigrations[^1].Version + 1);
        await using (var broken = new MigrationRunner(test.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations, new MigrationScript(nextVersion, "CREATE TABLE weft.failed_upgrade_probe(id integer); SELECT 1/0;")]))
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, failure.SqlState);
        }
        Assert.Equal(new[] { 1 }, await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(OwnershipChecksum, await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal(applied, await test.ScalarAsync<DateTime>("SELECT applied_at FROM weft.schema_migrations WHERE version=1"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema='weft' AND table_name IN ('mappings','mapping_revisions','revision_ingestions','revision_ingestion_attempts','failed_upgrade_probe')"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM weft.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
        await test.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM weft.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
    }
}
