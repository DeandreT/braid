using Npgsql;
using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMappingMetadataPersistenceTests(PostgresFixture fixture)
{
    private static readonly string[] AcceptedChecksums =
    [
        "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1",
        "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5",
        "0ed73cc9d597390366b661beb24f3bdfeddf1dbd9d49812ae575c2c42283fff3",
        "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113",
        ProjectCreationTestSupport.MigrationChecksum,
        RevisionDerivationSchemaContract.MigrationChecksum,
        MaintenanceSchemaContract.MigrationChecksum,
        ExternalIdentitySchemaContract.MigrationChecksum,
        PayloadPermissionSchemaContract.MigrationChecksum,
        ServerSessionSchemaContract.MigrationChecksum,
        ChatSchemaContract.MigrationChecksum,
        PreviewSchemaTestContract.MigrationChecksum
    ];

    [PostgresFact]
    public async Task FreshAndRepeatedMetadataInstallationPinsHistoryAndPreservesEditedAndImmutableFacts()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var applied = await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        var original = await test.ImmutableSnapshotAsync();
        var edited = await test.ReplaceAsync(1, "survives migration replay", "only the live metadata changes");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, edited.Outcome);
        var row = await test.RowSnapshotAsync();
        await AssertHistoryAsync(test.Case);
        await test.Case.MigrateAsync();
        await test.Case.MigrateAsync();
        await AssertHistoryAsync(test.Case);
        Assert.Equal(applied, await test.Case.ScalarAsync<string[]>("SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(row, await test.RowSnapshotAsync());
        Assert.Equal(original, await test.ImmutableSnapshotAsync());
        Assert.Equal(edited.Metadata, await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope));
        Assert.Equal(test.Original.CommittedRevision,
            (await test.Ingestion.GetReceiptAsync(test.Actor, test.Project, test.Original.Facts.OperationId))!.CommittedRevision);
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_column_privilege('weft_ingest','weft.mappings','name','UPDATE')"));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_column_privilege('weft_catalog_reader','weft.mappings','name','UPDATE')"));
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT has_table_privilege('weft_runtime','weft.mappings','SELECT')"));
    }

    [PostgresFact]
    public async Task ReopenedPoolsAndRestartOfSameOwnedClusterRetainEditHistoryAndAdmitNextVersion()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var originalFacts = await test.ImmutableSnapshotAsync();
        var edit = await test.ReplaceAsync(1, "persistent edited name", "exact metadata survives cluster restart");
        var append = await test.CommitAppendAsync(valid: false);
        var header = await test.HeaderAsync();
        var metadataConnectionString = test.Case.MetadataConnectionString;
        var catalogConnectionString = test.Case.CatalogConnectionString;
        var ingestionConnectionString = test.Case.IngestionConnectionString;
        await test.Database.DisposeAsync();
        await test.CatalogDatabase.DisposeAsync();
        await test.IngestionDatabase.DisposeAsync();
        await test.Case.CloseOwnerPoolAsync();
        await using (var recreatedDatabase = new PostgresMappingMetadataDatabase(metadataConnectionString))
        {
            var noOp = await new PostgresMappingMetadataWriter(recreatedDatabase).ReplaceAsync(test.Actor, test.Scope,
                new(test.Scope, edit.Metadata!.Version), new(edit.Metadata.Name, edit.Metadata.Description));
            Assert.Equal(MappingMetadataWriteOutcome.Unchanged, noOp.Outcome);
            Assert.Equal(edit.Metadata, noOp.Metadata);
        }
        await fixture.RestartAsync();
        await using var metadataDatabase = new PostgresMappingMetadataDatabase(metadataConnectionString);
        await using var catalogDatabase = new PostgresCatalogDatabase(catalogConnectionString);
        await using var ingestionDatabase = new PostgresIngestionDatabase(ingestionConnectionString);
        var reader = new PostgresCatalogReader(catalogDatabase);
        var reopened = (await reader.GetMappingAsync(test.Actor, test.Scope))!.Summary;
        Assert.Equal(header, reopened);
        Assert.Equal(edit.Metadata, await reader.GetMappingMetadataAsync(test.Actor, test.Scope));
        var store = new PostgresRevisionIngestionStore(ingestionDatabase);
        Assert.Equal(test.Original.CommittedRevision, (await store.GetReceiptAsync(test.Actor, test.Project, test.Original.Facts.OperationId))!.CommittedRevision);
        Assert.Equal(append.CommittedRevision, (await store.GetReceiptAsync(test.Actor, test.Project, append.Facts.OperationId))!.CommittedRevision);
        await using (var owner = NpgsqlDataSource.Create(test.Case.MigrationConnectionString))
        {
            await using var originalRevision = owner.CreateCommand("SELECT to_jsonb(revision)::text FROM weft.mapping_revisions revision WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND id=$4");
            foreach (var value in new[] { test.Scope.OrganizationId, test.Scope.ProjectId, test.Scope.MappingId, test.Original.Facts.FutureRevision.RevisionId }) originalRevision.Parameters.AddWithValue(value);
            Assert.Equal(originalFacts[0], await originalRevision.ExecuteScalarAsync());
        }
        var next = await new PostgresMappingMetadataWriter(metadataDatabase).ReplaceAsync(test.Actor, test.Scope,
            new(test.Scope, 2), new("next persisted edit", "condition uses the preserved version"));
        Assert.Equal(MappingMetadataWriteOutcome.Updated, next.Outcome);
        Assert.Equal(3, next.Metadata!.Version);
        Assert.Equal(header.Head, (await reader.GetMappingAsync(test.Actor, test.Scope))!.Summary.Head);
    }

    private static async Task AssertHistoryAsync(PostgresCase test)
    {
        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(AcceptedChecksums, await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"));
    }
}
