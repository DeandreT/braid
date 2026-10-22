using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogPersistenceTests(PostgresFixture fixture)
{
    private const string OwnershipChecksum = "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1";
    private const string IngestionChecksum = "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5";
    private const string CatalogChecksum = "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3";
    private const string MetadataChecksum = "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113";

    [PostgresFact]
    public async Task FreshAndRepeatedCatalogMigrationsPinHistoryAndPreserveCommittedFactsAndOlderProfiles()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = await test.CommitCreateAsync(valid: false, filename: "synthetic catalog.native.json");
        var target = PostgresCatalogTestSupport.Target(committed);
        var original = await test.Reader.GetMappingAsync(test.Actor, target);
        var originalRevision = await test.Reader.GetRevisionAsync(test.Actor, committed.Facts.FutureRevision);
        var timestamps = await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        await AssertPinnedHistoryAsync(test.Case);
        await test.Case.MigrateAsync();
        await test.Case.MigrateAsync();
        await AssertPinnedHistoryAsync(test.Case);
        Assert.Equal(timestamps, await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(original, await test.Reader.GetMappingAsync(test.Actor, target));
        Assert.Equal(originalRevision, await test.Reader.GetRevisionAsync(test.Actor, committed.Facts.FutureRevision));
        Assert.Equal(committed.Facts, (await test.Store.GetReceiptAsync(test.Actor, test.Project, committed.Facts.OperationId))!.Facts);
        await using var runtime = new PostgresDatabase(test.Case.RuntimeConnectionString);
        var ownershipReader = new PostgresProjectReader(runtime);
        Assert.Equal(test.Project, (await ownershipReader.GetVisibleProjectAsync(test.Actor, test.Project))!.Scope);
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_table_privilege('weft_runtime','weft.mappings','SELECT')"));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_table_privilege('weft_ingest','weft.revision_ingestion_attempts','DELETE')"));
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromQualifiedIngestionSchemaPreservesOriginalRowsAndOlderProfiles()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture, installCatalog: false);
        var first = await test.CommitCreateAsync(filename: "before upgrade.native.json");
        var second = await test.CommitAppendAsync(first, valid: false);
        var originalFacts = await HistoricalIngestionSeed.CaptureSnapshotAsync(test.Case);
        var originalHistory = await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        Assert.Equal(new[] { 1, 2 }, await test.Case.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(new[] { OwnershipChecksum, IngestionChecksum }, await test.Case.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.GetProjectAsync(test.Actor, test.Project));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(test.Case.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        await AssertPinnedHistoryAsync(test.Case);
        Assert.Equal(originalFacts.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(test.Case)).RetainedFactsJson);
        Assert.Equal(originalHistory, await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations WHERE version<=2"));
        var target = PostgresCatalogTestSupport.Target(first);
        var mapping = (await test.Reader.GetMappingAsync(test.Actor, target))!;
        Assert.Equal(second.Facts.FutureRevision, mapping.Summary.Head.Scope);
        Assert.Equal(2, mapping.Summary.Head.Sequence);
        Assert.Equal(IngestionAdmissionClassification.EligibleDecodedInvalid, mapping.Summary.Head.Admission);
        var revisions = (await test.Reader.ListRevisionsAsync(test.Actor, target, 10))!.Revisions;
        Assert.Equal(new[] { second.Facts.FutureRevision, first.Facts.FutureRevision }, revisions.Select(row => row.Scope));
        Assert.Equal(new[] { "entry.native.json", "before upgrade.native.json" }, revisions.Select(row => row.OriginalFilename));
        Assert.Equal(first.CommittedRevision, (await test.Store.GetReceiptAsync(test.Actor, test.Project, first.Facts.OperationId))!.CommittedRevision);
        Assert.Equal(second.CommittedRevision, (await test.Store.GetReceiptAsync(test.Actor, test.Project, second.Facts.OperationId))!.CommittedRevision);
    }

    [PostgresFact]
    public async Task FailedCatalogUpgradeRollsBackGrantsAndJournalWithoutChangingCommittedIngestionFacts()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture, installCatalog: false);
        var committed = await test.CommitCreateAsync();
        var originalSnapshot = await HistoricalIngestionSeed.CaptureSnapshotAsync(test.Case);
        var originalApplied = await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        var catalog = MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 3);
        await using (var broken = new MigrationRunner(test.Case.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations.Take(2), new MigrationScript(3, catalog.Sql + "\nSELECT 1/0;\n")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(new[] { 1, 2 }, await test.Case.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(originalApplied, await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations"));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_schema_privilege('weft_catalog_reader','weft','USAGE')"));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_table_privilege('weft_catalog_reader','weft.mappings','SELECT')"));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_column_privilege('weft_catalog_reader','weft.revision_ingestions','display_filename','SELECT')"));
        Assert.Equal(originalSnapshot, await HistoricalIngestionSeed.CaptureSnapshotAsync(test.Case));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.GetMappingAsync(test.Actor, PostgresCatalogTestSupport.Target(committed)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Store.GetReceiptAsync(test.Actor, test.Project, committed.Facts.OperationId));
        await test.Case.MigrateAsync();
        await AssertPinnedHistoryAsync(test.Case);
        Assert.Equal(originalSnapshot.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(test.Case)).RetainedFactsJson);
        Assert.Equal(committed.CommittedRevision,
            (await test.Store.GetReceiptAsync(test.Actor, test.Project, committed.Facts.OperationId))!.CommittedRevision);
        Assert.Equal(committed.Facts.FutureRevision, (await test.Reader.GetMappingAsync(test.Actor,
            PostgresCatalogTestSupport.Target(committed)))!.Summary.Head.Scope);
    }

    [PostgresFact]
    public async Task RecreatedCatalogPoolsAndRestartOfTheSameOwnedClusterRetainOriginalMetadataAndHistory()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var first = await test.CommitCreateAsync("persisted synthetic catalog", "preserve original metadata", filename: "persisted.native.json");
        await test.CommitAppendAsync(first, valid: false, filename: "persisted invalid.native.json");
        var target = PostgresCatalogTestSupport.Target(first);
        var header = await test.Reader.GetMappingAsync(test.Actor, target);
        var metadata = await test.Reader.GetMappingMetadataAsync(test.Actor, target);
        var revisions = (await test.Reader.ListRevisionsAsync(test.Actor, target, 10))!.Revisions.ToArray();
        var connectionString = test.Case.CatalogConnectionString;
        await test.Database.DisposeAsync();
        await using (var recreated = new PostgresCatalogDatabase(connectionString))
        {
            var reader = new PostgresCatalogReader(recreated);
            Assert.Equal(header, await reader.GetMappingAsync(test.Actor, target));
            Assert.Equal(revisions, (await reader.ListRevisionsAsync(test.Actor, target, 10))!.Revisions);
        }
        // Release only this case's pools. The owned persistent volume and exact
        // cluster container survive stop/start; saved endpoints remain unchanged.
        await test.IngestionDatabase.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await fixture.RestartAsync();
        await using var restarted = new PostgresCatalogDatabase(connectionString);
        var reopened = new PostgresCatalogReader(restarted);
        Assert.Equal(header, await reopened.GetMappingAsync(test.Actor, target));
        Assert.Equal(metadata, await reopened.GetMappingMetadataAsync(test.Actor, target));
        Assert.Equal(revisions, (await reopened.ListRevisionsAsync(test.Actor, target, 10))!.Revisions);
        foreach (var revision in revisions)
            Assert.Equal(revision, (await reopened.GetRevisionAsync(test.Actor, revision.Scope))!.Summary);
    }

    private static async Task AssertPinnedHistoryAsync(PostgresCase test)
    {
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(new[] { OwnershipChecksum, IngestionChecksum, CatalogChecksum, MetadataChecksum, ProjectCreationTestSupport.MigrationChecksum, RevisionDerivationSchemaContract.MigrationChecksum, MaintenanceSchemaContract.MigrationChecksum, ExternalIdentitySchemaContract.MigrationChecksum, PayloadPermissionSchemaContract.MigrationChecksum, ServerSessionSchemaContract.MigrationChecksum, ChatSchemaContract.MigrationChecksum, PreviewSchemaTestContract.MigrationChecksum, PreviewSchemaTestContract.MessagesMigrationChecksum, RegressionSchemaTestContract.CasesChecksum, RegressionSchemaTestContract.RunsChecksum, RegressionSchemaTestContract.DispatchChecksum, RegressionSchemaTestContract.HistoryChecksum, RegressionSchemaTestContract.BuildChecksum, RegressionSchemaTestContract.BuildDispatchChecksum, RegressionSchemaTestContract.BuildMetadataChecksum, RegressionSchemaTestContract.QualificationChecksum, RegressionSchemaTestContract.LocalReleaseChecksum, MaintenanceSchemaContract.StoreKindMigrationChecksum },
            await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
    }
}
