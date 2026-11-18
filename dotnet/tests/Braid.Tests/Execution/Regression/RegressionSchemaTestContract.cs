using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Exact additive regression suffix, admitted before older conservation snapshots exclude it.</summary>
internal static class RegressionSchemaTestContract
{
    internal const string CasesChecksum = "727e289e32436687f8ca795e171a626d5c6c88fd8c2de735cdff253f11701214";
    internal const string RunsChecksum = "01798cd635bbf6153f92720d23ca752fe527a9d4d9cf9affe2a618333fd8af47";
    internal const string DispatchChecksum = "828e9d4b92714ca54e5721d9790ec5769c309135acb08a9d7aad05453b7f9f3b";
    internal const string HistoryChecksum = "ba69f50bddb3c14d0a8b16661246b658495dfa11f130353be9584a9d35fc509b";
    internal const string BuildChecksum = "8e449c0d7729f78b2903ffab1a8d5aa09e8bd6bad10a0ff9f7b8275c20742420";
    internal const string BuildDispatchChecksum = "912bf2f9fd0379396aff987cc6033cb72e2e1e5c72617940492526886283b87d";
    internal const string BuildMetadataChecksum = "338d9fa7c688ed1c94a8450a40e82f04da19a950dd9afef001eb3151c5847f02";
    internal const string QualificationChecksum = "f10e02b7b0db373bfb9bc642617daf7cbf30eb0cef31fc82ab3dfbaac2af301d";
    internal const string LocalReleaseChecksum = "93ddadcfe530595736a8c5110d4e51c28686b469b2c9802ad79c42cc291c6d63";
    internal const string MachineInvocationChecksum = "e55944db93914b81594505d570e722f285404fa505ccb4f5499829887e02734c";
    internal const string ApiRequestAuditChecksum = "658ad2b814f61a4fbd469b7da6be8e1d8cf2243d2b3a86ee6d98a0531143e8bc";
    internal const string DocumentPreviewsChecksum = "84090aba30c81e57f83ebc3505accc1acac619423cf2fb7e8dc2ff04685712bb";
    internal const string X12InterchangeBuildsChecksum = "be9bd668fadc3aa45fef5083f73805a4ce55ef71bcec70ad9c85e1924dcb87c0";
    internal const string ComparisonSettlementChecksum = "3b2b3c1b4a8f1486afcf49a8540f0c9bcf67080f4dc53beb514573efcf37bd83";
    internal const string CsvDocumentsChecksum = "74fe36768eb3965baa95deaf233ec9e3e347f3ad0434ea2950fdab8347f6af9b";
    internal const string AccountDisplayChecksum = "1a8b83454f3742fdfcd99d1eab8df173ad988ca4d73f5a99c28efccbd0cf3bd3";
    internal const string ReleaseOperationsChecksum = "8e161e93687787b85f0692b37e41afac91aa71ebeb9e40c493207d174935c789";
    internal const string AutomationChecksum = "1f256d6310b091cd2a579d880d05d5c76fea43b38a69745f41e0c47e9faf9034";
    internal const string AdministrationChecksum = "f61e9c7b0369b3eed4c75cd652002d493862eda19ebf62ea9773317f69e11e6c";
    internal static readonly string[] Tables = ["regression_cases", "regression_case_versions", "regression_runs", .. BuildRoleGuard.Tables, .. BuildRunRoleGuard.Tables, .. QualificationRoleGuard.Tables, .. LocalReleaseRoleGuard.Tables, .. MachineInvocationRoleGuard.Tables, .. MachineInvocationRoleGuard.HostTables, .. ApiRequestAuditRoleGuard.Tables, .. AdministrationRoleGuard.Objects, .. ReleaseOperationsRoleGuard.Tables, .. AutomationSchemaAdmission.Tables, .. MappingDraftSchemaTestContract.Tables, .. SpecSchemaTestContract.Tables, .. CreationSchemaAdmission.Tables];
    internal static readonly string[] Functions = [.. MachineInvocationRoleGuard.AttemptFunctions, "guard_host_order", "case_authorized", "guard_regression_case", "guard_regression_version", "read_regression_case_metadata",
        "save_regression_case", "read_regression_case", "list_regression_cases", "regression_run_authorized", "guard_regression_run",
        "submit_regression_run", "claim_regression_run", "observe_regression_run", "finish_regression_run", "release_regression_run", "csv_dialect_valid", "csv_dialect_char_canonical", "csv_dialect_canonical",
        "cancel_regression_run", "read_regression_run", "read_regression_run_metadata", "list_ready_regression_runs", "list_regression_run_history", .. BuildRoleGuard.Names, .. BuildRunRoleGuard.Names, .. BuildMetadataRoleGuard.Names, .. QualificationRoleGuard.Names, .. LocalReleaseRoleGuard.Names, .. MachineInvocationRoleGuard.Names, .. ApiRequestAuditRoleGuard.Names, .. AdministrationRoleGuard.Names.Where(name => name != "guard_machine_invocation"), .. ReleaseOperationsRoleGuard.Names, .. AutomationSchemaAdmission.Functions, .. MappingDraftSchemaTestContract.Functions, .. SpecSchemaTestContract.Functions, .. CreationSchemaAdmission.Functions];
    internal static readonly string[] Roles = ["braid_case_reader", "braid_case_writer", "braid_run_reader", "braid_run_writer", "braid_build_reader", "braid_build_writer",
        "braid_build_run_reader", "braid_build_run_writer", "braid_build_dispatcher", "braid_build_metadata_reader", "braid_qualification_reader", "braid_qualification_writer", "braid_release_reader", "braid_release_writer",
        "braid_invocation_grant_writer", "braid_invocation_writer", "braid_api_audit_writer", "braid_administration", "braid_comparison_settler", .. MappingDraftSchemaTestContract.Roles, .. SpecSchemaTestContract.Roles, .. CreationSchemaAdmission.Roles];

    internal static async Task AdmitAsync(PostgresCase test, CancellationToken token = default)
    {
        await MappingDraftSchemaTestContract.AdmitPreviewParentAsync(test);
        await SpecSchemaTestContract.AdmitAsync(test, token);
        foreach (var profile in Enum.GetValues<CreationDatabaseProfile>())
        {
            await using var creation = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(test.RegressionConnectionString(CreationSchemaAdmission.Roles[(int)profile])) { Pooling = false }.ConnectionString);
            await creation.OpenAsync(token);
            await using var creationTransaction = await creation.BeginTransactionAsync(token);
            await using (var setup = new Npgsql.NpgsqlCommand("SET LOCAL search_path=pg_catalog", creation, creationTransaction)) await setup.ExecuteNonQueryAsync(token);
            await CreationSchemaAdmission.ValidateAsync(creation, creationTransaction, profile, token);
        }
        Assert.Equal(AccountDisplayChecksum, MigrationRunner.EmbeddedMigrations[25].Checksum);
        Assert.Equal(AdministrationChecksum, MigrationRunner.EmbeddedMigrations[26].Checksum);
        Assert.Equal(AutomationChecksum, MigrationRunner.EmbeddedMigrations[29].Checksum);
        Assert.Equal(ReleaseOperationsChecksum, MigrationRunner.EmbeddedMigrations[30].Checksum);
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, MigrationRunner.EmbeddedMigrations[31].Checksum);
        Assert.Equal(PreviewSchemaTestContract.ReadsMigrationChecksum, MigrationRunner.EmbeddedMigrations[32].Checksum);
        Assert.Equal(DocumentPreviewsChecksum, MigrationRunner.EmbeddedMigrations[33].Checksum);
        Assert.Equal(X12InterchangeBuildsChecksum, MigrationRunner.EmbeddedMigrations[34].Checksum);
        Assert.Equal(ComparisonSettlementChecksum, MigrationRunner.EmbeddedMigrations[35].Checksum);
        Assert.Equal(MappingDraftSchemaTestContract.DraftChecksum, MigrationRunner.EmbeddedMigrations[36].Checksum);
        Assert.Equal(MappingDraftSchemaTestContract.PreviewChecksum, MigrationRunner.EmbeddedMigrations[37].Checksum);
        Assert.Equal(ChatSchemaContract.MappingWriteMigrationChecksum, MigrationRunner.EmbeddedMigrations[38].Checksum);
        Assert.Equal(ChatSchemaContract.WriteJournalMigrationChecksum, MigrationRunner.EmbeddedMigrations[39].Checksum);
        Assert.Equal(CsvDocumentsChecksum, MigrationRunner.EmbeddedMigrations[40].Checksum);
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, MigrationRunner.EmbeddedMigrations[41].Checksum);
        await using (var preview = new PostgresPreviewDatabase(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader))
            Assert.Null(await new PostgresPreviewRunReader(preview).ReadAsync(new(new(Guid.NewGuid())), Guid.NewGuid(), Guid.NewGuid(), token));
        await using (var administration = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration")))
            Assert.Null(await administration.ReadOrganizationAsync(new(new(Guid.NewGuid())), Guid.NewGuid(), token));
        var actor = new ActorContext(new(Guid.NewGuid()));
        var mapping = new MappingScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var profile in new[] { RegressionCaseDatabaseProfile.Reader, RegressionCaseDatabaseProfile.Writer })
        {
            var role = profile == RegressionCaseDatabaseProfile.Reader ? "braid_case_reader" : "braid_case_writer";
            await using var store = new PostgresRegressionCaseStore(test.RegressionConnectionString(role), profile);
            Assert.Null(await store.ReadAsync(actor, mapping, Guid.NewGuid(), 1, token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresRegressionRunStore(test.RegressionConnectionString(writer ? "braid_run_writer" : "braid_run_reader"), writer);
            Assert.Null(await store.ReadAsync(actor, mapping.OrganizationId, Guid.NewGuid(), token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresBuildStore(test.RegressionConnectionString(writer ? "braid_build_writer" : "braid_build_reader"), writer);
            Assert.Null(await store.ReadEnvironmentAsync(actor, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), token));
        }
        foreach (var profile in new[] { BuildRunDatabaseProfile.Reader, BuildRunDatabaseProfile.Writer })
        {
            await using var store = new PostgresBuildRunStore(test.RegressionConnectionString(profile == BuildRunDatabaseProfile.Reader ? "braid_build_run_reader" : "braid_build_run_writer"), profile);
            Assert.Null(await store.ReadAsync(actor, mapping.OrganizationId, Guid.NewGuid(), token));
        }
        await using var dispatcher = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
        Assert.True((await dispatcher.DiscoverAsync(1, token)).Count <= 1);
        await using var metadata = new PostgresBuildMetadataReader(test.RegressionConnectionString("braid_build_metadata_reader"));
        Assert.Null(await metadata.ReadEnvironmentAsync(actor, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), token));
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresBuildQualificationStore(test.RegressionConnectionString(writer ? "braid_qualification_writer" : "braid_qualification_reader"), writer);
            Assert.Null(await store.ReadAsync(actor, mapping, Guid.NewGuid(), token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresLocalReleaseStore(test.RegressionConnectionString(writer ? "braid_release_writer" : "braid_release_reader"), writer);
            Assert.Null(await store.ReadTargetAsync(actor, new(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid()), token));
        }
        var target = new ReleaseTargetReference(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid());
        await using (var grants = new PostgresInvocationGrantStore(test.RegressionConnectionString("braid_invocation_grant_writer")))
            Assert.Empty(await grants.ReadAsync(actor, target, token));
        await using (var ledger = new PostgresInvocationLedger(test.RegressionConnectionString("braid_invocation_writer")))
            Assert.Equal(0, await ledger.PurgeExpiredAsync(target, 1, token));
        // An unknown binding is refused before any write, after the login and the 0025 contract are admitted.
        await using (var audit = new PostgresApiRequestAuditWriter(test.RegressionConnectionString("braid_api_audit_writer")))
            Assert.Equal(ApiRequestAuditOutcome.IdentityChanged, await audit.RecordAsync(new(Guid.NewGuid(), mapping.OrganizationId,
                actor.PrincipalId, Guid.NewGuid(), Guid.NewGuid(), "AdmitApiAudit", "POST", []), token));
        await using var connection = await test.Owner.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await AutomationSchemaAdmission.ValidateAsync(connection, transaction, token);
    }
}
