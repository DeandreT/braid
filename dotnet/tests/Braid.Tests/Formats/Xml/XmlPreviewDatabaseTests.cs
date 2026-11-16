using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual restricted SQL ports with authored completion facts, not native execution qualification.</summary>
[Collection(PostgresCollection.Name)]
public sealed class XmlPreviewDatabaseTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Schema_42_conserves_prefix_41_rows_functions_and_grants_then_repeats_with_populated_xml()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture, installCatalog: false);
        var receipt = await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native);
        await using (var prefix = new MigrationRunner(catalog.Case.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(41).ToArray()))
            await prefix.MigrateAsync();
        var revision = receipt.Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var readerDatabase = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var store = new PostgresPreviewRunStore(writer); var reader = new PostgresPreviewRunReader(readerDatabase);
        var service = new PreviewRunService(store, reader, TimeProvider.System, new() { XmlEnabled = true });
        var legacy = XmlSubmission(revision) with { Profile = PreviewExecutionProfile.LegacyJson, Primary = new(null, "{\"owned\":true}"u8) };
        var oldRecord = new PreviewRunRecord(legacy, PreviewRequestDocument.Encode(legacy, PreviewLimits.Default), "2026-11-02T12:00:00Z", 3, TimeSpan.FromHours(24));
        var oldRun = (await HistoricalPreviewAccess.SubmitAsync(catalog.Case, 41, catalog.Actor, oldRecord)).RunId!.Value;
        var oldToken = await HistoricalPreviewAccess.ClaimAsync(catalog.Case, 41, revision.OrganizationId, oldRun);
        Assert.True(await HistoricalPreviewAccess.CompleteAsync(catalog.Case, 41, revision.OrganizationId, oldRun, oldToken,
            PreviewPolicy.Profile, "application/json", "{\"owned\":true}"u8.ToArray(), "Order"));
        Assert.Equal(3, (await HistoricalPreviewAccess.ReadAsync(catalog.Case, 41, catalog.Actor, revision.OrganizationId, oldRun)).State);
        var before = await SnapshotAsync();
        var functions = await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(jsonb_build_array(proname,prosrc,proacl::text,proowner,proconfig) ORDER BY proname,oid)::text FROM pg_proc WHERE pronamespace='braid'::regnamespace");
        var grants = await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(jsonb_build_array(relname,relacl::text,(SELECT jsonb_agg(jsonb_build_array(attname,attacl::text) ORDER BY attnum) FROM pg_attribute WHERE attrelid=c.oid AND attnum>0)) ORDER BY relname)::text FROM pg_class c WHERE relnamespace='braid'::regnamespace");
        await MigrateXmlAsync();
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(functions, await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(jsonb_build_array(proname,prosrc,proacl::text,proowner,proconfig) ORDER BY proname,oid)::text FROM pg_proc WHERE pronamespace='braid'::regnamespace AND NOT proname=ANY($1)", (object)XmlPreviewSchemaAdmission.Functions));
        Assert.Equal(grants, await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(jsonb_build_array(relname,relacl::text,(SELECT jsonb_agg(jsonb_build_array(attname,attacl::text) ORDER BY attnum) FROM pg_attribute WHERE attrelid=c.oid AND attnum>0)) ORDER BY relname)::text FROM pg_class c WHERE relnamespace='braid'::regnamespace AND relname NOT IN ('xml_preview_documents','xml_preview_documents_pkey')"));
        // 0042 conservation is independent of the corrective helper supplied in
        // 0047. Current admission refuses that incomplete historical XML suffix.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(catalog.Actor, XmlSubmission(revision)));
        await catalog.Case.MigrateAsync();
        var submission = XmlSubmission(revision);
        var run = (await service.SubmitAsync(catalog.Actor, submission)).RunId!.Value;
        Assert.Equal(new PreviewSubmitResult(PreviewSubmitOutcome.Replayed, run), await service.SubmitAsync(catalog.Actor, submission));
        var claim = (await store.ClaimAsync(revision.OrganizationId, run, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        Assert.Equal(XmlPreviewWorkflowTests.Input, Assert.Single(await store.ReadInputsAsync(claim)).Content);
        Assert.True(await store.CompleteAsync(claim, Completion()));
        var result = (await reader.ReadAsync(catalog.Actor, revision.OrganizationId, run))!;
        Assert.Equal(XmlDocumentMetadata.WithoutHints, Assert.Single(result.Outputs).XmlMetadata);
        Assert.Equal(XmlPreviewWorkflowTests.Input, (await reader.ReadOutputAsync(catalog.Actor, revision.OrganizationId, run, 0))!.Content);
        Assert.Equal(XmlDocumentMetadata.WithoutHints, (await reader.ReadInputAsync(catalog.Actor, revision.OrganizationId, run, 0))!.XmlMetadata);
        Assert.Null(await reader.ReadOutputAsync(new(new PrincipalId(catalog.Seed.Bob)), revision.OrganizationId, run, 0));
        var populated = await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal)::text FROM braid.xml_preview_documents d");
        await catalog.Case.MigrateAsync();
        Assert.Equal(populated, await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal)::text FROM braid.xml_preview_documents d"));
        await catalog.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1 AND project_id=$2", catalog.Actor.PrincipalId.Value, revision.ProjectId);
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await service.SubmitAsync(catalog.Actor, submission)).Outcome);
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await service.SubmitAsync(catalog.Actor, submission with { RequestKey = Guid.NewGuid() })).Outcome);

        Task<string> SnapshotAsync() => catalog.Case.ScalarAsync<string>("SELECT jsonb_build_array((SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM braid.preview_runs r),(SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal) FROM braid.preview_documents d),(SELECT jsonb_agg(to_jsonb(m) ORDER BY version) FROM braid.schema_migrations m WHERE version<=41))::text");
        async Task MigrateXmlAsync()
        {
            // This conservation assertion belongs to 0042 only. Later proposal
            // migrations intentionally replace chat functions and grants.
            await using var runner = new MigrationRunner(catalog.Case.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(42).ToArray());
            await runner.MigrateAsync();
        }
    }

    [PostgresTheory]
    [InlineData("ALTER TABLE braid.xml_preview_documents DISABLE TRIGGER guard_xml_preview_document")]
    [InlineData("ALTER TABLE braid.xml_preview_documents DISABLE ROW LEVEL SECURITY")]
    [InlineData("ALTER POLICY xml_preview_documents_organization_scope ON braid.xml_preview_documents USING (true)")]
    [InlineData("GRANT SELECT ON braid.xml_preview_documents TO braid_preview_reader")]
    [InlineData("ALTER TABLE braid.xml_preview_documents ADD COLUMN unexpected integer")]
    [InlineData("CREATE OR REPLACE FUNCTION braid.xml_metadata_valid(value jsonb,input_role boolean) RETURNS boolean LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp AS 'BEGIN RETURN true; END;'")]
    public async Task Exact_schema_admission_refuses_tampering_before_preview_operations(string mutation)
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var receipt = await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native);
        await catalog.Case.ExecuteAsync(mutation);
        await using var database = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        var submission = XmlSubmission(receipt.Facts.FutureRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresPreviewRunStore(database).SubmitAsync(catalog.Actor,
            new(submission, PreviewRequestDocument.Encode(submission, PreviewLimits.Default), "2026-11-02T12:00:00.0000000Z", 3, TimeSpan.FromHours(24))));
    }

    [PostgresFact]
    public async Task Complete_xml_suffix_removal_with_recorded_42_refuses_legacy_writes_and_execution_before_private_io()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var receipt = await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native);
        var revision = receipt.Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var reads = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var store = new PostgresPreviewRunStore(writer);
        var reader = new PostgresPreviewRunReader(reads);
        var service = new PreviewRunService(store, reader, TimeProvider.System);
        var legacy = new PreviewRunSubmission(revision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var run = (await service.SubmitAsync(catalog.Actor, legacy)).RunId!.Value;
        var before = await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id)::text FROM braid.preview_runs r");
        await catalog.Case.ExecuteAsync("DROP TABLE braid.xml_preview_documents");
        // Remove the entire additive suffix, not just one easily detected object.
        await catalog.Case.ExecuteAsync("""
            DO $drop$ DECLARE signature text; BEGIN
                FOR signature IN SELECT p.oid::regprocedure::text FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND p.proname=ANY(ARRAY['xml_metadata_valid','guard_xml_preview_document',
                        'preview_submit_xml','preview_complete_xml','preview_xml_inputs','preview_xml_documents'])
                LOOP EXECUTE 'DROP FUNCTION '||signature; END LOOP;
            END; $drop$;
            """);
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, await catalog.Case.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=42"));
        Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM pg_proc WHERE pronamespace='braid'::regnamespace AND proname=ANY($1)", (object)XmlPreviewSchemaAdmission.Functions));
        foreach (var role in new[] { "braid_preview_writer", "braid_preview_reader" })
            Assert.False(await catalog.Case.ScalarAsync<bool>("SELECT has_any_column_privilege($1,'braid.schema_migrations','SELECT')", role));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(catalog.Actor, legacy with { RequestKey = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(catalog.Actor, revision.OrganizationId, run));
        var objects = System.Reflection.DispatchProxy.Create<IPrivateObjectStore, XmlPreviewWorkflowTests.RefuseIo>();
        var backend = System.Reflection.DispatchProxy.Create<IMappingPreviewBackend, XmlPreviewWorkflowTests.RefuseIo>();
        var executor = new PreviewRunExecutor(store, objects, backend, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(revision.OrganizationId, run, Guid.NewGuid()));
        Assert.Empty(((XmlPreviewWorkflowTests.RefuseIo)(object)objects).Calls);
        Assert.Empty(((XmlPreviewWorkflowTests.RefuseIo)(object)backend).Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HistoricalPreviewAccess.SubmitAsync(catalog.Case, 41, catalog.Actor,
            new(legacy, PreviewRequestDocument.Encode(legacy, PreviewLimits.Default), "2026-11-02T12:00:00Z", 3, TimeSpan.FromHours(24))));
        Assert.Equal(before, await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id)::text FROM braid.preview_runs r"));
    }

    [PostgresFact]
    public async Task Private_xml_content_or_metadata_drift_cannot_be_downloaded_and_tables_have_no_reader_access()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var receipt = await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native);
        var revision = receipt.Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var readerDatabase = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var store = new PostgresPreviewRunStore(writer); var reader = new PostgresPreviewRunReader(readerDatabase);
        var service = new PreviewRunService(store, reader, TimeProvider.System, new() { XmlEnabled = true });
        Assert.False(await catalog.Case.ScalarAsync<bool>("SELECT has_table_privilege('braid_preview_reader','braid.xml_preview_documents','SELECT')"));
        foreach (var mutation in new[] { "content=convert_to('<changed/>','UTF8')", "target_name='forged'", "metadata=replace(metadata,'braid.xml-safe.v1','braid.xml-safe.v9')" })
        {
            var run = (await service.SubmitAsync(catalog.Actor, XmlSubmission(revision))).RunId!.Value;
            var claim = (await store.ClaimAsync(revision.OrganizationId, run, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
            Assert.True(await store.CompleteAsync(claim, Completion()));
            await catalog.Case.ExecuteAsync("ALTER TABLE braid.xml_preview_documents DISABLE TRIGGER guard_xml_preview_document");
            await catalog.Case.ExecuteAsync("UPDATE braid.xml_preview_documents SET " + mutation + " WHERE direction=2 AND run_id=$1", run);
            await catalog.Case.ExecuteAsync("ALTER TABLE braid.xml_preview_documents ENABLE TRIGGER guard_xml_preview_document");
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadOutputAsync(catalog.Actor, revision.OrganizationId, run, 0));
        }
        var inputRun = (await service.SubmitAsync(catalog.Actor, XmlSubmission(revision))).RunId!.Value;
        var inputClaim = (await store.ClaimAsync(revision.OrganizationId, inputRun, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        await catalog.Case.ExecuteAsync("ALTER TABLE braid.xml_preview_documents DISABLE TRIGGER guard_xml_preview_document");
        await catalog.Case.ExecuteAsync("UPDATE braid.xml_preview_documents SET metadata=replace(metadata,'braid.xml-safe.v1','braid.xml-safe.v9') WHERE direction=1 AND run_id=$1", inputRun);
        await catalog.Case.ExecuteAsync("ALTER TABLE braid.xml_preview_documents ENABLE TRIGGER guard_xml_preview_document");
        await Assert.ThrowsAsync<PreviewRunException>(() => store.ReadInputsAsync(inputClaim));
    }

    [PostgresTheory]
    [InlineData("canceled")]
    [InlineData("authorityRevoked")]
    [InlineData("stale")]
    public async Task Xml_completion_cannot_publish_after_cancellation_revocation_or_lease_loss(string fence)
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var revision = (await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native)).Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var readerDatabase = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var store = new PostgresPreviewRunStore(writer);
        var service = new PreviewRunService(store, new PostgresPreviewRunReader(readerDatabase), TimeProvider.System, new() { XmlEnabled = true });
        var run = (await service.SubmitAsync(catalog.Actor, XmlSubmission(revision))).RunId!.Value;
        var claim = (await store.ClaimAsync(revision.OrganizationId, run, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        if (fence == "authorityRevoked")
            await catalog.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1 AND project_id=$2", catalog.Actor.PrincipalId.Value, revision.ProjectId);
        else if (fence == "canceled")
            await store.CancelAsync(catalog.Actor, revision.OrganizationId, run);
        else
            await catalog.Case.ExecuteAsync("UPDATE braid.preview_runs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE run_id=$1", run);
        Assert.Equal(fence != "stale", await store.CompleteAsync(claim, Completion()));
        Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.xml_preview_documents WHERE run_id=$1 AND direction=2", run));
        if (fence != "stale")
        {
            Assert.Equal(fence, await catalog.Case.ScalarAsync<string>("SELECT outcome FROM braid.preview_runs WHERE run_id=$1", run));
            Assert.False(await store.CompleteAsync(claim, Completion()));
        }
    }

    private static PreviewRunSubmission XmlSubmission(RevisionScope revision) => new(revision, Guid.NewGuid(), PreviewTargetSelection.All,
        new(null, XmlPreviewWorkflowTests.Input, xmlMetadata: XmlDocumentMetadata.WithoutHints), [], [])
    { Profile = PreviewExecutionProfile.XmlDocument };
    private static PreviewRunCompletion Completion() => new(PreviewRunOutcome.Succeeded, null, new(true, "authored", "authored", "authored", XmlPreviewAdmission.Profile), 1,
        [new("primary-output", "Order", false, XmlDocumentPolicy.MediaType, 1, XmlPreviewWorkflowTests.Input, xmlMetadata: XmlDocumentMetadata.WithoutHints)]);
}
