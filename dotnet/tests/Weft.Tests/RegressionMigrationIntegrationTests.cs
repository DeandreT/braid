using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RegressionMigrationIntegrationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Disabled_parent_foreign_key_triggers_refuse_regression_admission()
    {
        await using var test = await fixture.CreateCaseAsync();
        var trigger = await test.ScalarAsync<string>("""
            SELECT t.tgname FROM pg_trigger t JOIN pg_constraint c ON c.oid=t.tgconstraint
            WHERE c.conrelid='weft.regression_cases'::regclass AND c.contype='f'
                AND t.tgrelid='weft.principals'::regclass ORDER BY t.tgname LIMIT 1
            """);
        var quoted = '"' + trigger.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        await test.BootstrapAsync($"ALTER TABLE weft.principals DISABLE TRIGGER {quoted}");
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => RegressionSchemaTestContract.AdmitAsync(test)); }
        finally { await test.BootstrapAsync($"ALTER TABLE weft.principals ENABLE TRIGGER {quoted}"); }
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
            var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM weft.schema_migrations r");
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync())).SqlState);
            Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM weft.schema_migrations r"));
            Assert.True(await test.ScalarAsync<bool>("SELECT to_regclass('weft.regression_cases') IS NULL AND to_regclass('weft.regression_runs') IS NULL"));
        }
        finally { await isolated.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task Full_ordered_installation_admits_both_regression_profiles_without_changing_the_prefix()
    {
        await using var test = await fixture.CreateCaseAsync();
        await RegressionSchemaTestContract.AdmitAsync(test);
        Assert.Equal(25, MigrationRunner.EmbeddedMigrations.Count);
        Assert.Equal(Enumerable.Range(1, 25), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
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
    }

    [PostgresFact]
    public async Task Populated_thirteen_migration_upgrade_preserves_native_and_preview_facts_and_accepts_regression_runs()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await old.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(13).ToArray()))
            await prefix.MigrateAsync();
        var nativeBefore = await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM weft.mapping_revisions r");
        var historyBefore = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM weft.schema_migrations r");
        await using var previews = new PostgresPreviewDatabase(test.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var previewReads = new PostgresPreviewDatabase(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var previewReader = new PostgresPreviewRunReader(previewReads);
        var preview = new PreviewRunService(new PostgresPreviewRunStore(previews), previewReader, TimeProvider.System);
        var submitted = await preview.SubmitAsync(actor, new(receipt.Facts.FutureRevision, Guid.NewGuid(), PreviewTargetSelection.All,
            new(null, "{}"u8), [], []));
        Assert.Equal(PreviewSubmitOutcome.Created, submitted.Outcome);
        var before = JsonSerializer.Serialize(await previewReader.ReadAsync(actor, seed.OrganizationA, submitted.RunId!.Value));
        await test.MigrateAsync();
        await RegressionSchemaTestContract.AdmitAsync(test);
        Assert.Equal(nativeBefore, await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM weft.mapping_revisions r"));
        Assert.Equal(historyBefore, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY version)::text FROM weft.schema_migrations r WHERE version<=13"));
        Assert.Equal(before, JsonSerializer.Serialize(await previewReader.ReadAsync(actor, seed.OrganizationA, submitted.RunId.Value)));
        await using var caseWriter = new PostgresRegressionCaseStore(test.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, actor.PrincipalId, "upgrade case", receipt.Facts.FutureRevision,
            receipt.Facts.NativeObject.Digest, new("fixture", "engine", "build", "adapter", PreviewPolicy.Profile),
            new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []),
            RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([]));
        var saved = await caseWriter.SaveAsync(actor, value);
        Assert.Equal(RegressionCaseSaveOutcome.Created, saved.Outcome);
        await using var runs = new PostgresRegressionRunStore(test.RegressionConnectionString("weft_run_writer"), writer: true);
        var runId = Guid.NewGuid();
        Assert.Equal(RegressionRunSubmitOutcome.Created, await runs.SubmitAsync(new(actor, runId, saved.Reference!)));
        Assert.Equal(RegressionRunState.Queued, (await runs.ReadAsync(actor, seed.OrganizationA, runId))!.State);
        await test.MigrateAsync();
        Assert.Equal(RegressionRunSubmitOutcome.Replayed, await runs.SubmitAsync(new(actor, runId, saved.Reference!)));
    }
}
