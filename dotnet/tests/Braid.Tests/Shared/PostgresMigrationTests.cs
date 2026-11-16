using Npgsql;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMigrationTests(PostgresFixture fixture)
{
    private const string OwnershipChecksum = "f0875300876276f876ff403ec0365fe8c245cf0bf4af10e1f2841d9c4ad14aed";
    private const string IngestionChecksum = "6a981b9683fc629e3b174ec0b5c7b444b0b5d32698c9b41ee3d552221f808b70";
    private const string CatalogChecksum = "79d47c70c5131cd2973f98337815edd801f505b668f0f26561b1db1b955fb33b";
    private const string MetadataChecksum = "08e63461c49e7dabd64a951f0b0d0c4501f55e669cfec5b1fefb105732b807bd";
    internal const string MappingCreationChecksum = "7756574c6fd95a5799fe1fea20c8055f627d55ba36dd41a4071180c9dbb36906";
    internal const string DocumentEvidenceChecksum = "36fcbda4ef9bf4ac1442d5f9971f45e69f8b7068b98bdc027b38f45c6c012829";
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
    internal static string[] CurrentChecksums => [OwnershipChecksum, IngestionChecksum, CatalogChecksum, MetadataChecksum,
        ProjectCreationChecksum, RevisionDerivationChecksum, MaintenanceChecksum, ExternalIdentityChecksum, PayloadPermissionChecksum,
        ServerSessionChecksum, ChatChecksum, PreviewChecksum, PreviewMessagesChecksum, RegressionCasesChecksum, RegressionRunsChecksum,
        RegressionSchemaTestContract.DispatchChecksum, RegressionSchemaTestContract.HistoryChecksum, RegressionSchemaTestContract.BuildChecksum, RegressionSchemaTestContract.BuildDispatchChecksum, RegressionSchemaTestContract.BuildMetadataChecksum, RegressionSchemaTestContract.QualificationChecksum, RegressionSchemaTestContract.LocalReleaseChecksum,
        MaintenanceSchemaContract.StoreKindMigrationChecksum, RegressionSchemaTestContract.MachineInvocationChecksum,
        RegressionSchemaTestContract.ApiRequestAuditChecksum,
        RegressionSchemaTestContract.AccountDisplayChecksum, RegressionSchemaTestContract.AdministrationChecksum,
        InterchangeSchemaTestContract.MigrationChecksum, CatalogSearchSchemaAdmission.MigrationChecksum, RegressionSchemaTestContract.AutomationChecksum, RegressionSchemaTestContract.ReleaseOperationsChecksum,
        ChatSchemaContract.WorkspaceMigrationChecksum, PreviewSchemaTestContract.ReadsMigrationChecksum,
        RegressionSchemaTestContract.DocumentPreviewsChecksum, RegressionSchemaTestContract.X12InterchangeBuildsChecksum, RegressionSchemaTestContract.ComparisonSettlementChecksum, MappingDraftSchemaTestContract.DraftChecksum, MappingDraftSchemaTestContract.PreviewChecksum, ChatSchemaContract.MappingWriteMigrationChecksum, ChatSchemaContract.WriteJournalMigrationChecksum, RegressionSchemaTestContract.CsvDocumentsChecksum, XmlPreviewSchemaAdmission.MigrationChecksum,
        ChatSchemaContract.EditProposalMigrationChecksum, SpecSchemaTestContract.Checksum, MappingCreationChecksum, DocumentEvidenceChecksum,
        DocumentJsonSchemaAdmission.MigrationChecksum];
    [PostgresFact]
    public async Task EmptyInstallationAndRepeatedMigrationPreserveOriginalFactsAndJournal()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var applied = await test.ScalarAsync<DateTime>("SELECT applied_at FROM braid.schema_migrations WHERE version=1");
        var checksum = await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1");
        Assert.Equal(OwnershipChecksum, checksum);
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM braid.schema_migrations"));
        await test.MigrateAsync();
        await test.MigrateAsync();
        Assert.Equal(applied, await test.ScalarAsync<DateTime>("SELECT applied_at FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal(checksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM braid.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
        Assert.Equal("viewer", await test.ScalarAsync<string>("SELECT role FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
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
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM braid.schema_migrations"));
        await PostgresSeedData.CreateAsync(test);
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
    }

    [PostgresFact]
    public async Task ExactAppliedChecksumTamperingIsRefusedWithoutChangingFacts()
    {
        await using var test = await fixture.CreateCaseAsync();
        await PostgresSeedData.CreateAsync(test);
        var original = await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1");
        var tampered = (original[0] == '0' ? "1" : "0") + original[1..];
        await test.ExecuteAsync("UPDATE braid.schema_migrations SET checksum=$1 WHERE version=1", tampered);
        await Assert.ThrowsAsync<InvalidOperationException>(test.MigrateAsync);
        Assert.Equal(tampered, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"));
    }

    [PostgresFact]
    public async Task UnknownAppliedVersionIsRefusedWithoutAdoptingIt()
    {
        await using var test = await fixture.CreateCaseAsync();
        await test.ExecuteAsync("INSERT INTO braid.schema_migrations(version,checksum) VALUES (99,$1)", new string('a', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(test.MigrateAsync);
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count + 1, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
    }

    [PostgresFact]
    public async Task HoleInOtherwiseKnownMigrationHistoryIsRefused()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        var nextVersion = checked(MigrationRunner.EmbeddedMigrations[^1].Version + 1);
        MigrationScript[] scripts = [.. MigrationRunner.EmbeddedMigrations, new(nextVersion, "CREATE TABLE braid.future_probe(id integer PRIMARY KEY);")];
        await using var runner = new MigrationRunner(test.MigrationConnectionString, scripts);
        await runner.MigrateAsync();
        await test.ExecuteAsync("DELETE FROM braid.schema_migrations WHERE version=1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
        Assert.Equal(scripts.Skip(1).Select(script => script.Version), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
    }

    [PostgresFact]
    public async Task UnversionedOwnedSchemaIsNeverAdopted()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await test.ExecuteAsync("CREATE SCHEMA braid; CREATE TABLE braid.unversioned_probe(id integer PRIMARY KEY); INSERT INTO braid.unversioned_probe VALUES (7)");
        await Assert.ThrowsAsync<InvalidOperationException>(test.MigrateAsync);
        Assert.Equal(7, await test.ScalarAsync<int>("SELECT id FROM braid.unversioned_probe"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema='braid' AND table_name='schema_migrations'"));
    }

    [PostgresFact]
    public async Task FailedScriptRollsBackSchemaAndJournalTogether()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        var nextVersion = checked(MigrationRunner.EmbeddedMigrations[^1].Version + 1);
        await using (var broken = new MigrationRunner(test.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations, new MigrationScript(nextVersion, "CREATE TABLE braid.failed_probe(id integer PRIMARY KEY); INSERT INTO braid.failed_probe VALUES (7); SELECT 1/0;")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.schemata WHERE schema_name='braid'"));
        await test.MigrateAsync();
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
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
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromUnchangedOwnershipMigrationPreservesOriginalFacts()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(1).ToArray()))
            await old.MigrateAsync();
        Assert.Equal(OwnershipChecksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        var seed = await PostgresSeedData.CreateAsync(test);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(OwnershipChecksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM braid.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
        Assert.Equal("viewer", await test.ScalarAsync<string>("SELECT role FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Alice));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
    }

    [PostgresFact]
    public async Task FailedUpgradeRollsBackOnlyTheNewInstallationAndPreservesOwnershipJournalAndFacts()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(1).ToArray()))
            await old.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var applied = await test.ScalarAsync<DateTime>("SELECT applied_at FROM braid.schema_migrations WHERE version=1");
        var nextVersion = checked(MigrationRunner.EmbeddedMigrations[^1].Version + 1);
        await using (var broken = new MigrationRunner(test.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations, new MigrationScript(nextVersion, "CREATE TABLE braid.failed_upgrade_probe(id integer); SELECT 1/0;")]))
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, failure.SqlState);
        }
        Assert.Equal(new[] { 1 }, await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(OwnershipChecksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal(applied, await test.ScalarAsync<DateTime>("SELECT applied_at FROM braid.schema_migrations WHERE version=1"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema='braid' AND table_name IN ('mappings','mapping_revisions','revision_ingestions','revision_ingestion_attempts','failed_upgrade_probe')"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM braid.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
        await test.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(CurrentChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal("Project A", await test.ScalarAsync<string>("SELECT name FROM braid.projects WHERE organization_id=$1 AND id=$2", seed.OrganizationA, seed.ProjectA));
    }
}
