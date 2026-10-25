using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed partial class MappingDraftPersistenceTests
{
    [PostgresFact]
    public async Task PopulatedPreDraftPrefixPreservesRevisionsAndRevisionPreviewBytesAcrossBothMigrations()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await prefix.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(25).ToArray()))
            await prefix.MigrateAsync();
        await using var writer = new PostgresPreviewDatabase(test.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var reader = new PostgresPreviewDatabase(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var previews = new PreviewRunService(new PostgresPreviewRunStore(writer), new PostgresPreviewRunReader(reader), TimeProvider.System);
        var submission = new PreviewRunSubmission(receipt.Facts.FutureRevision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var created = await previews.SubmitAsync(actor, submission);
        Assert.Equal(PreviewSubmitOutcome.Created, created.Outcome);
        var before = JsonSerializer.Serialize(await previews.ReadAsync(actor, seed.OrganizationA, created.RunId!.Value));
        var revisions = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY id)::text FROM weft.mapping_revisions r");
        var documents = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY direction,ordinal)::text FROM weft.preview_documents d");
        var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM weft.schema_migrations m");
        await test.MigrateAsync();
        Assert.Equal(revisions, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY id)::text FROM weft.mapping_revisions r"));
        Assert.Equal(documents, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY direction,ordinal)::text FROM weft.preview_documents d"));
        Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM weft.schema_migrations m WHERE version<=25"));
        Assert.Equal(before, JsonSerializer.Serialize(await previews.ReadAsync(actor, seed.OrganizationA, created.RunId.Value)));
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await previews.SubmitAsync(actor, submission)).Outcome);
        Assert.Equal(27L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.False(await test.ScalarAsync<bool>("SELECT has_table_privilege('weft_draft_writer','weft.mapping_drafts','SELECT,INSERT,UPDATE,DELETE')"));
        Assert.False(await test.ScalarAsync<bool>("SELECT has_function_privilege('weft_draft_reader','weft.draft_checkpoint_write(uuid,uuid,uuid,bigint,bytea,bytea,bytea,bytea,boolean,integer,text,text,text)','EXECUTE')"));
    }
}
