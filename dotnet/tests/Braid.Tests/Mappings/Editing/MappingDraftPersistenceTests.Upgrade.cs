using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed partial class MappingDraftPersistenceTests
{
    // Version of 0037_mapping_drafts.sql; 0038_mapping_draft_previews.sql follows it.
    private const int DraftMigration = 37;

    [PostgresFact]
    public async Task PopulatedPreDraftPrefixPreservesRevisionsAndRevisionPreviewBytesAcrossBothMigrations()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await prefix.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(DraftMigration - 1).ToArray()))
            await prefix.MigrateAsync();
        await using var writer = new PostgresPreviewDatabase(test.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var reader = new PostgresPreviewDatabase(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var previews = new PreviewRunService(new PostgresPreviewRunStore(writer), new PostgresPreviewRunReader(reader), TimeProvider.System);
        var submission = new PreviewRunSubmission(receipt.Facts.FutureRevision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var created = await HistoricalPreviewAccess.SubmitAsync(test, 36, actor,
            new(submission, PreviewRequestDocument.Encode(submission, PreviewLimits.Default), "2026-11-12T00:00:00Z", 3, TimeSpan.FromHours(24)));
        Assert.Equal(PreviewSubmitOutcome.Created, created.Outcome);
        var before = await HistoricalPreviewAccess.ReadAsync(test, 36, actor, seed.OrganizationA, created.RunId!.Value);
        var revisions = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY id)::text FROM braid.mapping_revisions r");
        var documents = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY direction,ordinal)::text FROM braid.preview_documents d");
        var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m");
        await test.MigrateAsync();
        Assert.Equal(revisions, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY id)::text FROM braid.mapping_revisions r"));
        Assert.Equal(documents, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d)-'csv_input'-'csv_dialect' ORDER BY direction,ordinal)::text FROM braid.preview_documents d"));
        Assert.True(await test.ScalarAsync<bool>("SELECT bool_and(NOT csv_input AND csv_dialect IS NULL) FROM braid.preview_documents"));
        Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m WHERE version<" + DraftMigration));
        var after = (await previews.ReadAsync(actor, seed.OrganizationA, created.RunId.Value))!;
        Assert.Equal(before.RequestFingerprint, after.RequestFingerprint);
        Assert.Equal(PreviewRunState.Queued, after.State);
        Assert.Equal(submission.Subject, after.Subject);
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await previews.SubmitAsync(actor, submission)).Outcome);
        Assert.Equal((long)MigrationRunner.EmbeddedMigrations.Count, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        Assert.False(await test.ScalarAsync<bool>("SELECT has_table_privilege('braid_draft_writer','braid.mapping_drafts','SELECT,INSERT,UPDATE,DELETE')"));
        Assert.False(await test.ScalarAsync<bool>("SELECT has_function_privilege('braid_draft_reader','braid.draft_checkpoint_write(uuid,uuid,uuid,bigint,bytea,bytea,bytea,bytea,boolean,integer,text,text,text)','EXECUTE')"));
    }

    [PostgresTheory]
    [InlineData(40)]
    [InlineData(41)]
    public async Task CsvAndXmlSuffixesPreservePopulatedDraftCheckpointsAndAdmitNewDraftPreviews(int prefixVersion)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await prefix.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(prefixVersion).ToArray()))
            await prefix.MigrateAsync();
        await using var draftReader = new PostgresMappingDraftDatabase(test.RegressionConnectionString("braid_draft_reader"), MappingDraftDatabaseProfile.Reader);
        await using var draftWriter = new PostgresMappingDraftDatabase(test.RegressionConnectionString("braid_draft_writer"), MappingDraftDatabaseProfile.Writer);
        var drafts = new PostgresMappingDraftStore(draftReader, draftWriter);
        var revision = receipt.Facts.FutureRevision;
        var mapping = new MappingScope(revision.OrganizationId, revision.ProjectId, revision.MappingId);
        var basis = (await drafts.SelectBaseAsync(actor, mapping, revision.RevisionId, false))!;
        var checkpoint = Checkpoint(revision);
        var draft = (await drafts.CreateAsync(actor, mapping, Guid.NewGuid(), basis, checkpoint)).Draft!;
        var subject = new DraftPreviewSubject(revision, draft.Id, draft.Version, checkpoint.Artifact.Digest, checkpoint.Artifact.ByteCount);
        await using var writer = new PostgresPreviewDatabase(test.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var reader = new PostgresPreviewDatabase(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var previews = new PreviewRunService(new PostgresPreviewRunStore(writer), new PostgresPreviewRunReader(reader), TimeProvider.System);
        var submission = new PreviewRunSubmission(subject, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var original = await HistoricalPreviewAccess.SubmitAsync(test, prefixVersion, actor,
            new(submission, PreviewRequestDocument.Encode(submission, PreviewLimits.Default), "2026-11-12T00:00:00Z", 3, TimeSpan.FromHours(24)));
        Assert.Equal(PreviewSubmitOutcome.Created, original.Outcome);
        var before = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY direction,ordinal)::text FROM braid.preview_documents d");
        var history = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m");
        await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string>(prefixVersion == 40
            ? "SELECT jsonb_agg(to_jsonb(d)-'csv_input'-'csv_dialect' ORDER BY direction,ordinal)::text FROM braid.preview_documents d"
            : "SELECT jsonb_agg(to_jsonb(d) ORDER BY direction,ordinal)::text FROM braid.preview_documents d"));
        Assert.True(await test.ScalarAsync<bool>("SELECT bool_and(NOT csv_input AND csv_dialect IS NULL) FROM braid.preview_documents"));
        Assert.Equal(history, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m WHERE version<=" + prefixVersion));
        Assert.Equal(subject, (await previews.ReadAsync(actor, revision.OrganizationId, original.RunId!.Value))!.Subject);
        Assert.Equal("{}"u8.ToArray(), (await previews.ReadInputAsync(actor, revision.OrganizationId, original.RunId.Value, 0))!.Content);
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await previews.SubmitAsync(actor, submission)).Outcome);
        var added = await previews.SubmitAsync(actor, submission with { RequestKey = Guid.NewGuid() });
        Assert.Equal(PreviewSubmitOutcome.Created, added.Outcome);
        Assert.Equal(checkpoint.Artifact.CopyBytes(), await test.ScalarAsync<byte[]>("SELECT content FROM braid.preview_documents WHERE run_id=$1 AND direction=3", added.RunId!.Value));
        Assert.Equal(subject, (await previews.ReadAsync(actor, revision.OrganizationId, added.RunId.Value))!.Subject);
    }
}
