using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMetadataMigrationTests(PostgresFixture fixture)
{
    private const string MetadataChecksum = "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113";

    [PostgresFact]
    public async Task ConcurrentUpgradeFrom0003PreservesCompleteHistoricalFactsAndOlderGrants()
    {
        await using var support = await LegacyCatalogAsync(fixture);
        var first = await support.CommitCreateAsync(filename: "historical-create.native.json");
        var second = await support.CommitAppendAsync(first, valid: false);
        await InstallCatalogOnlyAsync(support.Case);
        var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case);
        var grants = await OlderGrantsAsync(support.Case);
        var history = await support.Case.ScalarAsync<string[]>(
            "SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations");
        var target = PostgresCatalogTestSupport.Target(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => support.Reader.GetMappingAsync(support.Actor, target));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var runner = new MigrationRunner(support.Case.MigrationConnectionString);
            await runner.MigrateAsync();
        }));
        await support.Case.MigrateAsync();
        Assert.Equal(new[] { 1, 2, 3, 4 }, await support.Case.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(MetadataChecksum, await support.Case.ScalarAsync<string>(
            "SELECT checksum FROM weft.schema_migrations WHERE version=4"));
        Assert.Equal(history, await support.Case.ScalarAsync<string[]>(
            "SELECT array_agg(applied_at::text ORDER BY version) FROM weft.schema_migrations WHERE version<=3"));
        Assert.Equal(before.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case)).RetainedFactsJson);
        Assert.Equal(grants, await OlderGrantsAsync(support.Case));
        Assert.Equal(first.CommittedRevision,
            (await support.Store.GetReceiptAsync(support.Actor, support.Project, first.Facts.OperationId))!.CommittedRevision);
        Assert.Equal(second.CommittedRevision,
            (await support.Store.GetReceiptAsync(support.Actor, support.Project, second.Facts.OperationId))!.CommittedRevision);
        var originalRevisions = (await support.Reader.ListRevisionsAsync(support.Actor, target, 10))!.Revisions.ToArray();
        await using var database = new PostgresMappingMetadataDatabase(support.Case.MetadataConnectionString);
        var result = await new PostgresMappingMetadataWriter(database).ReplaceAsync(support.Actor, target,
            new MappingMetadataVersion(target, 1), new MappingMetadataEdit("Renamed after upgrade", "Original head retained"));
        Assert.Equal(MappingMetadataWriteOutcome.Updated, result.Outcome);
        Assert.Equal(2, result.Metadata!.Version);
        Assert.Equal(second.Facts.FutureRevision, (await support.Reader.GetMappingAsync(support.Actor, target))!.Summary.Head.Scope);

        // The historical helper remains old-schema-only. Append through the
        // current ingestion port with explicitly synthetic C1 admission evidence.
        var reserved = await support.Store.ReserveAsync(support.Actor, support.Project, Guid.NewGuid(),
            PostgresIngestionTestSupport.AppendIntent(second), PostgresIngestionTestSupport.Profile,
            PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        var facts = Assert.IsType<IngestionReceipt>(reserved.Receipt).Facts;
        var claimed = await support.Store.ClaimAsync(support.Actor, support.Project, facts.OperationId, Guid.NewGuid());
        Assert.Equal(IngestionStoreOutcome.Claimed, claimed.Outcome);
        var claim = Assert.IsType<IngestionClaim>(claimed.Claim);
        Assert.Equal(IngestionStoreOutcome.Applied,
            (await support.Store.MarkStagedAsync(support.Actor, claim, facts.NativeObject)).Outcome);
        var completed = await support.Store.CompleteAttemptAsync(support.Actor, claim,
            PostgresIngestionTestSupport.SyntheticCompletion(facts));
        Assert.Equal(IngestionStoreOutcome.Applied, completed.Outcome);
        Assert.Equal(IngestionPhase.Ready, completed.Receipt!.Phase);
        var finalized = await support.Store.FinalizeAsync(support.Actor, claim);
        Assert.Equal(IngestionStoreOutcome.Applied, finalized.Outcome);
        var appended = Assert.IsType<IngestionReceipt>(finalized.Receipt);
        Assert.Equal(IngestionPhase.Committed, appended.Phase);
        var mapping = (await support.Reader.GetMappingAsync(support.Actor, target))!.Summary;
        Assert.Equal(appended.Facts.FutureRevision, mapping.Head.Scope);
        Assert.Equal(3, mapping.Head.Sequence);
        Assert.Equal("Renamed after upgrade", mapping.Name);
        Assert.Equal("Original head retained", mapping.Description);
        Assert.Equal(2, mapping.Version);
        Assert.Equal(result.Metadata, await support.Reader.GetMappingMetadataAsync(support.Actor, target));
        foreach (var revision in originalRevisions)
            Assert.Equal(revision, (await support.Reader.GetRevisionAsync(support.Actor, revision.Scope))!.Summary);
        Assert.Equal(new[] { 3L, 2L, 1L },
            (await support.Reader.ListRevisionsAsync(support.Actor, target, 10))!.Revisions.Select(revision => revision.Sequence));
        AssertHistoricalRowsPreserved(before.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case)).RetainedFactsJson);
    }

    [PostgresFact]
    public async Task Failed0004RollsBackItsFunctionGrantsAndJournalWithoutChangingHistoricalFacts()
    {
        await using var support = await LegacyCatalogAsync(fixture);
        var committed = await support.CommitCreateAsync();
        await support.CommitAppendAsync(committed, valid: false);
        await InstallCatalogOnlyAsync(support.Case);
        var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case);
        var migration = MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 4);
        await using (var broken = new MigrationRunner(support.Case.MigrationConnectionString,
            [.. MigrationRunner.EmbeddedMigrations.Take(3), new MigrationScript(4, migration.Sql + "\nSELECT 1/0;\n")]))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => broken.MigrateAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        Assert.Equal(before, await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case));
        Assert.False(await support.Case.ScalarAsync<bool>("SELECT has_schema_privilege('weft_catalog_editor','weft','USAGE')"));
        Assert.False(await support.Case.ScalarAsync<bool>("SELECT has_column_privilege('weft_catalog_editor','weft.mappings','name','UPDATE')"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => support.Store.GetReceiptAsync(
            support.Actor, support.Project, committed.Facts.OperationId));
        await support.Case.MigrateAsync();
        Assert.Equal(before.RetainedFactsJson,
            (await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case)).RetainedFactsJson);
        Assert.Equal(committed.CommittedRevision,
            (await support.Store.GetReceiptAsync(support.Actor, support.Project, committed.Facts.OperationId))!.CommittedRevision);
    }

    [PostgresFact]
    public async Task MissingEditorRoleFailsAtomicallyThenExplicitProvisioningPermitsTheSameUpgrade()
    {
        // Role absence belongs to a separate exact-owned cluster, never a role
        // removed from the shared fixture or another active case database.
        var isolated = new PostgresFixture(provisionMetadataEditor: false);
        await isolated.InitializeAsync();
        try
        {
            await using var support = await LegacyCatalogAsync(isolated);
            var committed = await support.CommitCreateAsync();
            await InstallCatalogOnlyAsync(support.Case);
            var before = await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case);
            var error = await Assert.ThrowsAsync<PostgresException>(support.Case.MigrateAsync);
            Assert.Equal(PostgresErrorCodes.UndefinedObject, error.SqlState);
            Assert.Equal(before, await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case));
            var password = new NpgsqlConnectionStringBuilder(support.Case.MetadataConnectionString).Password!;
            Assert.True(Guid.TryParseExact(password, "N", out _));
            await support.Case.BootstrapAsync($"""
                CREATE ROLE weft_catalog_editor LOGIN PASSWORD '{password}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                GRANT CONNECT ON DATABASE {support.Case.Name} TO weft_catalog_editor;
                """);
            await support.Case.MigrateAsync();
            Assert.Equal(before.RetainedFactsJson,
                (await HistoricalIngestionSeed.CaptureSnapshotAsync(support.Case)).RetainedFactsJson);
            var target = PostgresCatalogTestSupport.Target(committed);
            await using var database = new PostgresMappingMetadataDatabase(support.Case.MetadataConnectionString);
            var result = await new PostgresMappingMetadataWriter(database).ReplaceAsync(support.Actor, target,
                new MappingMetadataVersion(target, 1), new MappingMetadataEdit("Explicitly provisioned editor", ""));
            Assert.Equal(MappingMetadataWriteOutcome.Updated, result.Outcome);
            Assert.Equal(committed.Facts.FutureRevision,
                (await support.Reader.GetMappingAsync(support.Actor, target))!.Summary.Head.Scope);
        }
        finally { await isolated.DisposeAsync(); }
    }

    private static Task<PostgresCatalogTestSupport> LegacyCatalogAsync(PostgresFixture owner) =>
        PostgresCatalogTestSupport.CreateAsync(owner, installCatalog: false);

    private static void AssertHistoricalRowsPreserved(string beforeJson, string afterJson)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        foreach (var table in before.RootElement.EnumerateObject())
        {
            if (table.Name == "mappings")
                continue; // Head and metadata changes are checked against the public read above.
            var current = after.RootElement.GetProperty(table.Name);
            foreach (var row in table.Value.EnumerateArray())
                Assert.Single(current.EnumerateArray(), candidate => JsonElement.DeepEquals(row, candidate));
            if (table.Name is "revision_ingestions" or "revision_ingestion_attempts" or "mapping_revisions")
                Assert.Equal(table.Value.GetArrayLength() + 1, current.GetArrayLength());
            else
                Assert.True(JsonElement.DeepEquals(table.Value, current), $"Historical {table.Name} changed.");
        }
    }

    private static async Task InstallCatalogOnlyAsync(PostgresCase test)
    {
        await using var runner = new MigrationRunner(test.MigrationConnectionString,
            MigrationRunner.EmbeddedMigrations.Take(3).ToArray());
        await runner.MigrateAsync();
    }

    private static Task<string> OlderGrantsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'tables', (SELECT jsonb_agg(to_jsonb(g) ORDER BY grantee,table_name,privilege_type)
            FROM information_schema.role_table_grants g WHERE table_schema='weft'
            AND grantee IN ('weft_runtime','weft_ingest','weft_catalog_reader')),
          'columns', (SELECT jsonb_agg(to_jsonb(g) ORDER BY grantee,table_name,column_name,privilege_type)
            FROM information_schema.role_column_grants g WHERE table_schema='weft'
            AND grantee IN ('weft_runtime','weft_ingest','weft_catalog_reader')),
          'schema', (SELECT jsonb_agg(jsonb_build_object('role',r.name,
            'usage',has_schema_privilege(r.name,'weft','USAGE'),
            'create',has_schema_privilege(r.name,'weft','CREATE')) ORDER BY r.name)
            FROM (VALUES ('weft_runtime'),('weft_ingest'),('weft_catalog_reader')) r(name)))::text
        """);
}
