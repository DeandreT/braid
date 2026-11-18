using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RegressionMigrationIntegrationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Disabled_parent_foreign_key_triggers_refuse_regression_admission()
    {
        await using var test = await fixture.CreateCaseAsync();
        var trigger = await test.ScalarAsync<string>("""
            SELECT t.tgname FROM pg_trigger t JOIN pg_constraint c ON c.oid=t.tgconstraint
            WHERE c.conrelid='braid.regression_cases'::regclass AND c.contype='f'
                AND t.tgrelid='braid.principals'::regclass ORDER BY t.tgname LIMIT 1
            """);
        var quoted = '"' + trigger.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        await test.BootstrapAsync($"ALTER TABLE braid.principals DISABLE TRIGGER {quoted}");
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => RegressionSchemaTestContract.AdmitAsync(test)); }
        finally { await test.BootstrapAsync($"ALTER TABLE braid.principals ENABLE TRIGGER {quoted}"); }
        await RegressionSchemaTestContract.AdmitAsync(test);
    }

    [PostgresFact]
    public async Task Missing_regression_roles_roll_back_the_ordered_upgrade_without_adopting_partial_tables()
    {
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionRegressionProfiles: false);
        await isolated.InitializeAsync();
        try
        {
            await using var test = await isolated.CreateCaseAsync(migrate: false);
            await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(13).ToArray()))
                await prefix.MigrateAsync();
            var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r");
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync())).SqlState);
            Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r"));
            Assert.True(await test.ScalarAsync<bool>("SELECT to_regclass('braid.regression_cases') IS NULL AND to_regclass('braid.regression_runs') IS NULL"));
        }
        finally { await isolated.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Full_ordered_installation_admits_both_regression_profiles_without_changing_the_prefix()
    {
        await using var test = await fixture.CreateCaseAsync();
        await RegressionSchemaTestContract.AdmitAsync(test);
        Assert.Equal(66, MigrationRunner.EmbeddedMigrations.Count);
        Assert.Equal(Enumerable.Range(1, 66), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, MigrationRunner.EmbeddedMigrations[41].Checksum);
        var source = MigrationRunner.EmbeddedMigrations;
        Assert.Equal(PreviewSchemaTestContract.MigrationChecksum, source[11].Checksum);
        Assert.Equal(PreviewSchemaTestContract.MessagesMigrationChecksum, source[12].Checksum);
        Assert.Equal(RegressionSchemaTestContract.CasesChecksum, source[13].Checksum);
        Assert.Equal(RegressionSchemaTestContract.RunsChecksum, source[14].Checksum);
        Assert.Equal(RegressionSchemaTestContract.DispatchChecksum, source[15].Checksum);
        Assert.Equal(RegressionSchemaTestContract.HistoryChecksum, source[16].Checksum);
        Assert.Equal(RegressionSchemaTestContract.BuildChecksum, source[17].Checksum);
        Assert.Equal(RegressionSchemaTestContract.BuildDispatchChecksum, source[18].Checksum);
        Assert.Equal(RegressionSchemaTestContract.BuildMetadataChecksum, source[19].Checksum);
        Assert.Equal(RegressionSchemaTestContract.QualificationChecksum, source[20].Checksum);
        Assert.Equal(RegressionSchemaTestContract.LocalReleaseChecksum, source[21].Checksum);
        Assert.Equal(MaintenanceSchemaContract.StoreKindMigrationChecksum, source[22].Checksum);
        Assert.Equal(RegressionSchemaTestContract.MachineInvocationChecksum, source[23].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ApiRequestAuditChecksum, source[24].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AccountDisplayChecksum, source[25].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AdministrationChecksum, source[26].Checksum);
        Assert.Equal(InterchangeSchemaTestContract.MigrationChecksum, source[27].Checksum);
        Assert.Equal(CatalogSearchSchemaAdmission.MigrationChecksum, source[28].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum, source[29].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ReleaseOperationsChecksum, source[30].Checksum);
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, source[31].Checksum);
        Assert.Equal(PreviewSchemaTestContract.ReadsMigrationChecksum, source[32].Checksum);
        Assert.Equal(RegressionSchemaTestContract.DocumentPreviewsChecksum, source[33].Checksum);
        Assert.Equal(RegressionSchemaTestContract.X12InterchangeBuildsChecksum, source[34].Checksum);
        Assert.Equal(RegressionSchemaTestContract.ComparisonSettlementChecksum, source[35].Checksum);
        Assert.Equal(RegressionSchemaTestContract.AutomationChecksum,
            await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=30"));
    }

    [RegressionPostgresTheory]
    [InlineData(13)]
    [InlineData(33)]
    public async Task Populated_preview_prefix_upgrade_preserves_native_and_preview_facts_and_accepts_regression_runs(int prefixVersion)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await old.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(prefixVersion).ToArray()))
            await prefix.MigrateAsync();
        var nativeBefore = await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM braid.mapping_revisions r");
        var historyBefore = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r");
        await using var previews = new PostgresPreviewDatabase(test.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var previewReads = new PostgresPreviewDatabase(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var previewReader = new PostgresPreviewRunReader(previewReads);
        var submission = new PreviewRunSubmission(receipt.Facts.FutureRevision, Guid.NewGuid(), PreviewTargetSelection.All,
            new(null, "{}"u8), [], []);
        var record = new PreviewRunRecord(submission, PreviewRequestDocument.Encode(submission, PreviewLimits.Default),
            "2026-10-31T00:00:00Z", 3, TimeSpan.FromHours(24));
        var submitted = await HistoricalPreviewAccess.SubmitAsync(test, prefixVersion, actor, record);
        Assert.Equal(PreviewSubmitOutcome.Created, submitted.Outcome);
        var before = await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM braid.preview_runs r WHERE run_id=$1", submitted.RunId!.Value);
        await test.MigrateAsync();
        await RegressionSchemaTestContract.AdmitAsync(test);
        Assert.Equal(nativeBefore, await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM braid.mapping_revisions r"));
        Assert.Equal(historyBefore, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM braid.schema_migrations r WHERE version<=$1", prefixVersion));
        Assert.Equal(before, await test.ScalarAsync<string>("SELECT jsonb_object_agg(v.key,v.value)::text FROM braid.preview_runs r CROSS JOIN LATERAL jsonb_each(to_jsonb(r)) v WHERE run_id=$1 AND v.key IN (SELECT jsonb_object_keys($2::jsonb))", submitted.RunId.Value, before));
        Assert.NotNull(await previewReader.ReadAsync(actor, seed.OrganizationA, submitted.RunId.Value));
        await using var caseWriter = new PostgresRegressionCaseStore(test.RegressionConnectionString("braid_case_writer"), RegressionCaseDatabaseProfile.Writer);
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, actor.PrincipalId, "upgrade case", receipt.Facts.FutureRevision,
            receipt.Facts.NativeObject.Digest, new("fixture", "engine", "build", "adapter", PreviewPolicy.Profile),
            new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []),
            RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([]));
        var saved = await caseWriter.SaveAsync(actor, value);
        Assert.Equal(RegressionCaseSaveOutcome.Created, saved.Outcome);
        await using var runs = new PostgresRegressionRunStore(test.RegressionConnectionString("braid_run_writer"), writer: true);
        var runId = Guid.NewGuid();
        Assert.Equal(RegressionRunSubmitOutcome.Created, await runs.SubmitAsync(new(actor, runId, saved.Reference!)));
        Assert.Equal(RegressionRunState.Queued, (await runs.ReadAsync(actor, seed.OrganizationA, runId))!.State);
        await test.MigrateAsync();
        Assert.Equal(RegressionRunSubmitOutcome.Replayed, await runs.SubmitAsync(new(actor, runId, saved.Reference!)));
    }
}
