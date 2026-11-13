using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DocumentJsonPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Recursive_json_contract_preserves_duplicates_until_every_object_is_checked()
    {
        await using var test = await fixture.CreateCaseAsync();
        foreach (var (json, expected) in new (string, bool)[]
        {
            ("{}", true), ("[]", true), ("null", true), ("42", true), ("true", true), ("\"text\"", true),
            ("{\"a\":1,\"b\":2}", true), ("[{\"a\":1},{\"a\":2}]", true),
            ("{\"a\":{\"x\":[null,{},[],{\"b\":false}]}}", true),
            ("{\"a\":1,\"a\":2}", false), ("{\"a\":1,\"\\u0061\":2}", false),
            ("{\"nested\":{\"a\":1,\"a\":2}}", false),
            ("[{\"a\":1},[{\"nested\":{\"x\":1,\"x\":2}}]]", false)
        })
            Assert.Equal(expected, await test.ScalarAsync<bool>("SELECT weft.document_json_unique($1::json)", json));
        Assert.True(await test.ScalarAsync<bool>("SELECT weft.document_json_unique(NULL::json)"));
        Assert.Equal(DocumentJsonSchemaAdmission.MigrationChecksum,
            await test.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=47"));
        foreach (var role in new[] { "weft_preview_reader", "weft_preview_writer" })
            Assert.False(await test.ScalarAsync<bool>("SELECT has_function_privilege($1,'weft.document_json_unique(json)','EXECUTE')", role));
    }

    [PostgresTheory]
    [InlineData("CREATE OR REPLACE FUNCTION weft.document_json_unique(value json) RETURNS boolean LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp AS 'BEGIN RETURN true; END;'")]
    [InlineData("DROP FUNCTION weft.document_json_unique(json)")]
    [InlineData("CREATE FUNCTION weft.document_json_unique(value jsonb) RETURNS boolean LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp AS 'SELECT true'; REVOKE ALL ON FUNCTION weft.document_json_unique(jsonb) FROM PUBLIC")]
    [InlineData("GRANT EXECUTE ON FUNCTION weft.document_json_unique(json) TO PUBLIC")]
    [InlineData("GRANT EXECUTE ON FUNCTION weft.document_json_unique(json) TO weft_preview_reader")]
    [InlineData("ALTER FUNCTION weft.document_json_unique(json) SECURITY DEFINER")]
    [InlineData("ALTER FUNCTION weft.document_json_unique(json) STABLE")]
    [InlineData("ALTER FUNCTION weft.document_json_unique(json) SET search_path=pg_catalog,weft")]
    [InlineData("ALTER FUNCTION weft.document_json_unique(json) OWNER TO weft_runtime")]
    public async Task Helper_drift_refuses_xml_and_legacy_operations_before_private_io(string mutation)
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var revision = (await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native)).Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var reads = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var store = new PostgresPreviewRunStore(writer);
        var reader = new PostgresPreviewRunReader(reads);
        var xml = Submission(revision);
        var legacy = new PreviewRunSubmission(revision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var run = (await store.SubmitAsync(catalog.Actor, Record(xml))).RunId!.Value;
        await catalog.Case.BootstrapAsync(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitAsync(catalog.Actor, Record(xml with { RequestKey = Guid.NewGuid() })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitAsync(catalog.Actor, Record(legacy)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadInputAsync(catalog.Actor, revision.OrganizationId, run, 0));
        var objects = System.Reflection.DispatchProxy.Create<IPrivateObjectStore, XmlPreviewWorkflowTests.RefuseIo>();
        var backend = System.Reflection.DispatchProxy.Create<IMappingPreviewBackend, XmlPreviewWorkflowTests.RefuseIo>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PreviewRunExecutor(store, objects, backend, TimeProvider.System)
            .ExecuteAsync(revision.OrganizationId, run, Guid.NewGuid()));
        Assert.Empty(((XmlPreviewWorkflowTests.RefuseIo)(object)objects).Calls);
        Assert.Empty(((XmlPreviewWorkflowTests.RefuseIo)(object)backend).Calls);
        Assert.Equal(1L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
    }

    [PostgresFact]
    public async Task Xml_submission_and_document_trigger_refuse_nested_duplicate_fields()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var revision = (await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native)).Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        var store = new PostgresPreviewRunStore(writer);
        var submission = Submission(revision);
        var record = Record(submission);
        var duplicate = record.RequestDocument.Replace("\"schemaHints\":null", "\"schemaHints\":null,\"schemaHints\":null", StringComparison.Ordinal);
        Assert.NotEqual(record.RequestDocument, duplicate);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => store.SubmitAsync(catalog.Actor, record with { RequestDocument = duplicate }));
        Assert.Equal("22023", failure.SqlState);
        Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
        var run = (await store.SubmitAsync(catalog.Actor, record)).RunId!.Value;
        failure = await Assert.ThrowsAsync<PostgresException>(() => catalog.Case.ExecuteAsync("""
            INSERT INTO weft.xml_preview_documents(organization_id,run_id,direction,ordinal,name,metadata,content,digest,byte_count,metadata_digest)
            SELECT organization_id,run_id,1,1,'extra',m,content,digest,byte_count,encode(sha256(convert_to(m,'UTF8')),'hex')
            FROM weft.xml_preview_documents d CROSS JOIN LATERAL
                (SELECT replace(d.metadata,'"schemaHints":null','"schemaHints":null,"schemaHints":null') AS m) changed
            WHERE run_id=$1 AND direction=1 AND ordinal=0
            """, run));
        Assert.Equal("22023", failure.SqlState);
        Assert.Equal(1L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM weft.xml_preview_documents"));
    }

    [PostgresFact]
    public async Task Populated_46_upgrade_adds_only_the_helper_and_repeats_with_populated_xml()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture, installCatalog: false);
        var receipt = await catalog.CommitCreateAsync(body: XmlPreviewWorkflowTests.Native);
        var revision = receipt.Facts.FutureRevision;
        await using (var prefix = new MigrationRunner(catalog.Case.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(41).ToArray()))
            await prefix.MigrateAsync();
        var legacy = new PreviewRunSubmission(revision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var legacyRun = (await HistoricalPreviewAccess.SubmitAsync(catalog.Case, 41, catalog.Actor, Record(legacy))).RunId!.Value;
        await using (var prefix = new MigrationRunner(catalog.Case.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(46).ToArray()))
            await prefix.MigrateAsync();
        var rows = await SnapshotAsync();
        var functions = await FunctionsAsync();
        var relations = await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(c) ORDER BY c.oid)::text FROM pg_class c WHERE relnamespace='weft'::regnamespace");
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        var store = new PostgresPreviewRunStore(writer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitAsync(catalog.Actor, Record(Submission(revision))));
        await catalog.Case.MigrateAsync();
        Assert.Equal(rows, await SnapshotAsync());
        Assert.Equal(functions, await FunctionsAsync());
        Assert.Equal(relations, await catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(c) ORDER BY c.oid)::text FROM pg_class c WHERE relnamespace='weft'::regnamespace"));
        Assert.Equal(PreviewSubmitOutcome.Replayed, (await store.SubmitAsync(catalog.Actor, Record(legacy))).Outcome);
        var xml = Submission(revision);
        var run = (await store.SubmitAsync(catalog.Actor, Record(xml))).RunId!.Value;
        var claim = (await store.ClaimAsync(revision.OrganizationId, run, Guid.NewGuid(), TimeSpan.FromSeconds(90))).Claim!;
        Assert.True(await store.CompleteAsync(claim, new(PreviewRunOutcome.Succeeded, null,
            new(true, "authored", "authored", "authored", XmlPreviewAdmission.Profile), 1,
            [new("primary-output", "result", false, XmlDocumentPolicy.MediaType, 1, XmlPreviewWorkflowTests.Input, xmlMetadata: XmlDocumentMetadata.WithoutHints)])));
        var populated = await SnapshotAsync();
        await catalog.Case.MigrateAsync();
        Assert.Equal(populated, await SnapshotAsync());
        await using var readDatabase = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        var reader = new PostgresPreviewRunReader(readDatabase);
        Assert.Equal(XmlPreviewWorkflowTests.Input, (await reader.ReadOutputAsync(catalog.Actor, revision.OrganizationId, run, 0))!.Content);
        Assert.Equal("{}"u8.ToArray(), (await reader.ReadInputAsync(catalog.Actor, revision.OrganizationId, legacyRun, 0))!.Content);

        Task<string> FunctionsAsync() => catalog.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(p) ORDER BY p.oid)::text FROM pg_proc p WHERE pronamespace='weft'::regnamespace AND proname<>'document_json_unique'");
        Task<string> SnapshotAsync() => catalog.Case.ScalarAsync<string>("""
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM weft.mapping_revisions r),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM weft.preview_runs r),
                (SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal) FROM weft.preview_documents d),
                (SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal) FROM weft.xml_preview_documents d),
                (SELECT jsonb_agg(to_jsonb(m) ORDER BY version) FROM weft.schema_migrations m WHERE version<=46))::text
            """);
    }

    private static PreviewRunSubmission Submission(RevisionScope revision) => new(revision, Guid.NewGuid(), PreviewTargetSelection.All,
        new(null, XmlPreviewWorkflowTests.Input, xmlMetadata: XmlDocumentMetadata.WithoutHints), [], [])
    { Profile = PreviewExecutionProfile.XmlDocument };
    private static PreviewRunRecord Record(PreviewRunSubmission submission) => new(submission,
        PreviewRequestDocument.Encode(submission, PreviewLimits.Default), "2026-11-12T00:00:00Z", 3, TimeSpan.FromHours(24));
}
