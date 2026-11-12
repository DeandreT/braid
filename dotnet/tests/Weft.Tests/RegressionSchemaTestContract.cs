using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Exact additive regression suffix, admitted before older conservation snapshots exclude it.</summary>
internal static class RegressionSchemaTestContract
{
    internal const string CasesChecksum = "ec00cf3e9869eb59f89eec5d0e7d38d672112970e9cb2beae41c6a3a88f204a1";
    internal const string RunsChecksum = "943b1df0d034568fc0eba4f26c8a7c06c2936fad60e9c6a322054efc83925efc";
    internal const string DispatchChecksum = "51d8fdab1a338cc2c903223f0c45c0469f193638462dfc10f828762262510539";
    internal const string HistoryChecksum = "0ce99a72cc4822ec9b09710becf297436d36d966c23d88cb8212e0d04b0bc89a";
    internal const string BuildChecksum = "5c4f348a577ac36d7d4e2410318e26cee11521bcfb879e620af54705e39d03d2";
    internal const string BuildDispatchChecksum = "3f1262c5b3e538e472b5e015b5fdb8e683983505faebc24a9057816f4aee3cfc";
    internal const string BuildMetadataChecksum = "cfc43e570c53b8f2f830de4fcbe7b0e06ff0b8acc1a49c8eb7d7003d69110627";
    internal const string QualificationChecksum = "88b28690c2c295854a670b79b4394bb8a2dbd838badcace066036dfbc6403e2b";
    internal const string LocalReleaseChecksum = "91ea3ec6ca0652000a319b565f699a53f296c44f0c2f633670cfce83f9eb63e3";
    internal const string MachineInvocationChecksum = "8a1b8ec1221b58a5cb102b76dba7c17be0855e07097275b323a324a51f981b13";
    internal const string ApiRequestAuditChecksum = "e8ce44832823c885a709222b66f317c86b2bf789262804266a9920f50e7601a2";
    internal const string DocumentPreviewsChecksum = "849f17688af5f6dc46dfc8792ffe6578ac491a8083b581f0d1221d5a30808254";
    internal const string X12InterchangeBuildsChecksum = "c17397b4829cb9bcbcfbd49a4bae1612c839e61d39c84104115b1eede1ea0cb2";
    internal const string ComparisonSettlementChecksum = "52f31240810b895e9b858fd58dc5bec08457b998189224c066b49b346645f4d3";
    internal const string CsvDocumentsChecksum = "1d338dc7831f9079269c2ecfc2014492ab6e9402e1d28daf5eba301226d5f9f9";
    internal const string AccountDisplayChecksum = "922e9c46ea1ee9374a95ca0e087cf20e4306826dd72aaad7f89292c31ad8760e";
    internal const string ReleaseOperationsChecksum = "926d5bc816765c6b4263c78e129337eb3f1fe4ec7794e1ed276fb6923dfe640d";
    internal const string AutomationChecksum = "e4dd2a5c41fed83eb3246aafd144a059c47dc5c476f6bd1247e1153cfc93e1f2";
    internal const string AdministrationChecksum = "18b3e19431c2b48c3e506d4e6286c07ea1bf5dbd5a0e9fc2f51226925545743e";
    internal static readonly string[] Tables = ["regression_cases", "regression_case_versions", "regression_runs", .. BuildRoleGuard.Tables, .. BuildRunRoleGuard.Tables, .. QualificationRoleGuard.Tables, .. LocalReleaseRoleGuard.Tables, .. MachineInvocationRoleGuard.Tables, .. ApiRequestAuditRoleGuard.Tables, .. AdministrationRoleGuard.Objects, .. ReleaseOperationsRoleGuard.Tables, .. AutomationSchemaAdmission.Tables, .. MappingDraftSchemaTestContract.Tables];
    internal static readonly string[] Functions = ["case_authorized", "guard_regression_case", "guard_regression_version", "read_regression_case_metadata",
        "save_regression_case", "read_regression_case", "list_regression_cases", "regression_run_authorized", "guard_regression_run",
        "submit_regression_run", "claim_regression_run", "observe_regression_run", "finish_regression_run", "release_regression_run", "csv_dialect_valid", "csv_dialect_char_canonical", "csv_dialect_canonical",
        "cancel_regression_run", "read_regression_run", "read_regression_run_metadata", "list_ready_regression_runs", "list_regression_run_history", .. BuildRoleGuard.Names, .. BuildRunRoleGuard.Names, .. BuildMetadataRoleGuard.Names, .. QualificationRoleGuard.Names, .. LocalReleaseRoleGuard.Names, .. MachineInvocationRoleGuard.Names, .. ApiRequestAuditRoleGuard.Names, .. AdministrationRoleGuard.Names.Where(name => name != "guard_machine_invocation"), .. ReleaseOperationsRoleGuard.Names, .. AutomationSchemaAdmission.Functions, .. MappingDraftSchemaTestContract.Functions];
    internal static readonly string[] Roles = ["weft_case_reader", "weft_case_writer", "weft_run_reader", "weft_run_writer", "weft_build_reader", "weft_build_writer",
        "weft_build_run_reader", "weft_build_run_writer", "weft_build_dispatcher", "weft_build_metadata_reader", "weft_qualification_reader", "weft_qualification_writer", "weft_release_reader", "weft_release_writer",
        "weft_invocation_grant_writer", "weft_invocation_writer", "weft_api_audit_writer", "weft_administration", "weft_comparison_settler", .. MappingDraftSchemaTestContract.Roles];

    internal static async Task AdmitAsync(PostgresCase test, CancellationToken token = default)
    {
        await MappingDraftSchemaTestContract.AdmitPreviewParentAsync(test);
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
        await using (var administration = new PostgresAdministrationStore(test.RegressionConnectionString("weft_administration")))
            Assert.Null(await administration.ReadOrganizationAsync(new(new(Guid.NewGuid())), Guid.NewGuid(), token));
        var actor = new ActorContext(new(Guid.NewGuid()));
        var mapping = new MappingScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var profile in new[] { RegressionCaseDatabaseProfile.Reader, RegressionCaseDatabaseProfile.Writer })
        {
            var role = profile == RegressionCaseDatabaseProfile.Reader ? "weft_case_reader" : "weft_case_writer";
            await using var store = new PostgresRegressionCaseStore(test.RegressionConnectionString(role), profile);
            Assert.Null(await store.ReadAsync(actor, mapping, Guid.NewGuid(), 1, token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresRegressionRunStore(test.RegressionConnectionString(writer ? "weft_run_writer" : "weft_run_reader"), writer);
            Assert.Null(await store.ReadAsync(actor, mapping.OrganizationId, Guid.NewGuid(), token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresBuildStore(test.RegressionConnectionString(writer ? "weft_build_writer" : "weft_build_reader"), writer);
            Assert.Null(await store.ReadEnvironmentAsync(actor, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), token));
        }
        foreach (var profile in new[] { BuildRunDatabaseProfile.Reader, BuildRunDatabaseProfile.Writer })
        {
            await using var store = new PostgresBuildRunStore(test.RegressionConnectionString(profile == BuildRunDatabaseProfile.Reader ? "weft_build_run_reader" : "weft_build_run_writer"), profile);
            Assert.Null(await store.ReadAsync(actor, mapping.OrganizationId, Guid.NewGuid(), token));
        }
        await using var dispatcher = new PostgresBuildRunStore(test.RegressionConnectionString("weft_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
        Assert.True((await dispatcher.DiscoverAsync(1, token)).Count <= 1);
        await using var metadata = new PostgresBuildMetadataReader(test.RegressionConnectionString("weft_build_metadata_reader"));
        Assert.Null(await metadata.ReadEnvironmentAsync(actor, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), token));
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresBuildQualificationStore(test.RegressionConnectionString(writer ? "weft_qualification_writer" : "weft_qualification_reader"), writer);
            Assert.Null(await store.ReadAsync(actor, mapping, Guid.NewGuid(), token));
        }
        foreach (var writer in new[] { false, true })
        {
            await using var store = new PostgresLocalReleaseStore(test.RegressionConnectionString(writer ? "weft_release_writer" : "weft_release_reader"), writer);
            Assert.Null(await store.ReadTargetAsync(actor, new(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid()), token));
        }
        var target = new ReleaseTargetReference(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid());
        await using (var grants = new PostgresInvocationGrantStore(test.RegressionConnectionString("weft_invocation_grant_writer")))
            Assert.Empty(await grants.ReadAsync(actor, target, token));
        await using (var ledger = new PostgresInvocationLedger(test.RegressionConnectionString("weft_invocation_writer")))
            Assert.Equal(0, await ledger.PurgeExpiredAsync(target, 1, token));
        // An unknown binding is refused before any write, after the login and the 0025 contract are admitted.
        await using (var audit = new PostgresApiRequestAuditWriter(test.RegressionConnectionString("weft_api_audit_writer")))
            Assert.Equal(ApiRequestAuditOutcome.IdentityChanged, await audit.RecordAsync(new(Guid.NewGuid(), mapping.OrganizationId,
                actor.PrincipalId, Guid.NewGuid(), Guid.NewGuid(), "AdmitApiAudit", "POST", []), token));
        await using var connection = await test.Owner.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await AutomationSchemaAdmission.ValidateAsync(connection, transaction, token);
    }
}
