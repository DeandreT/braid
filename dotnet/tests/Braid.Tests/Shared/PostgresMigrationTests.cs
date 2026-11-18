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
        DocumentJsonSchemaAdmission.MigrationChecksum, MachineInvocationRoleGuard.HostOrderChecksum, LocalReleaseRoleGuard.ReplicaMigrationChecksum,
        TriggerRoleGuard.MigrationChecksum, TriggerRoleGuard.ScheduleMigrationChecksum, TriggerRoleGuard.OperationsMigrationChecksum,
        "83a6006f4bc55199c9d292b0f2d9c1609b0811ce6058e7b7298410ac929e9428", // AS2 send fence
        "7fa579dba2b2a974c7ee2389a3940c752908df2504ec6938e1901d61fe31aa19", // pilot quarantine
        AuditLogSchemaAdmission.MigrationChecksum, AiUsageSchemaAdmission.MigrationChecksum,
        "327c6d2068d1b27c5f27d1d4c3d9b0a2e432fd506c4c51a998f3791be3d68152", // organization switching
        "aa3158893a2527406a97d40ba3e546b35c7fe28d99d063ef42cfbefb0bd5c4c3", // 0058_resource_lifecycle
        "35ce67e7d6d8c6ef4dcc18c5f077646b4d09f648d3311905dcb881d7166e3b5c", // 0059_resource_deletion
        "76c50ed5190d4dd5c62b7bb07c82890792949db81414fca54d8e87218435f8fe", // 0060_retention_policies
        "fd2cdcb86a18e861119283e9556f4892e4d74298b3f321c502b58e6113af78c7", // 0061_resource_purge
        "fcc470a90b97ece9509353ad66a0357b58cd6359253b25888bf30b6566edab79", // 0062_retention_enforcement
        "e55a49d1d580e09ae6280115a3fda9fc69d569b465695c90ab1e6fc3e9f6bb04", // 0063_retention_completion
        "b2bd388dd5221a51a0e4bb615212ac6cc79bca35449e6fc9dc83d4abd0438f94", // 0064_order_retention_constraints
        "7bf1013d631501692a858e0c9928b76ac0d373cbba554981ac48912808bcc6c6", // 0065_native_document_profiles
        "ed429f73a7ce9b6accddff123fb018ad1b0cadefe62d8b91b3a9c7ba1d9ca3e6"]; // 0066_project_creation_retention_hooks
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
