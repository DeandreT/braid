using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class XmlPreviewPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Schema_42_conserves_legacy_rows_fences_history_and_grants_then_repeats_with_populated_xml()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, XmlPreviewWorkflowTests.Native, migrationCount: 41);
        var oldSubmission = test.Submission();
        var oldRecord = new PreviewRunRecord(oldSubmission, PreviewRequestDocument.Encode(oldSubmission, PreviewLimits.Default), "2026-11-02T12:00:00Z", 3, TimeSpan.FromHours(24));
        var legacy = (await HistoricalPreviewAccess.SubmitAsync(test.Case, 41, test.Alice, oldRecord)).RunId!.Value;
        var token = await HistoricalPreviewAccess.ClaimAsync(test.Case, 41, test.Revision.OrganizationId, legacy);
        Assert.True(await HistoricalPreviewAccess.CompleteAsync(test.Case, 41, test.Revision.OrganizationId, legacy, token,
            PreviewPolicy.Profile, "application/json", "{\"owned\":true}"u8.ToArray(), "Order"));
        var rows = await SnapshotAsync();
        var history = await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM weft.schema_migrations m");
        var functions = await test.Case.ScalarAsync<string>("SELECT jsonb_agg(jsonb_build_array(proname,prosrc,proacl::text,proowner,proconfig) ORDER BY proname,oid)::text FROM pg_proc WHERE pronamespace='weft'::regnamespace");
        await test.Case.MigrateAsync();
        Assert.Equal(rows, await SnapshotAsync());
        Assert.Equal(history, await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM weft.schema_migrations m WHERE version<=41"));
        Assert.Equal(functions, await test.Case.ScalarAsync<string>("SELECT jsonb_agg(jsonb_build_array(proname,prosrc,proacl::text,proowner,proconfig) ORDER BY proname,oid)::text FROM pg_proc WHERE pronamespace='weft'::regnamespace AND NOT proname=ANY($1)", (object)XmlPreviewSchemaAdmission.Functions));
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, await test.Case.ScalarAsync<string>("SELECT checksum FROM weft.schema_migrations WHERE version=42"));
        var service = test.Service(new() { XmlEnabled = true });
        var submission = new PreviewRunSubmission(test.Revision, Guid.NewGuid(), PreviewTargetSelection.All,
            new(null, XmlPreviewWorkflowTests.Input, xmlMetadata: XmlDocumentMetadata.WithoutHints), [], [])
        { Profile = PreviewExecutionProfile.XmlDocument };
        var run = (await service.SubmitAsync(test.Alice, submission)).RunId!.Value;
        Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(new Backend(), new() { XmlEnabled = true }).ExecuteAsync(test.Revision.OrganizationId, run, Guid.NewGuid()));
        var populated = await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal)::text FROM weft.xml_preview_documents d");
        await test.Case.MigrateAsync();
        Assert.Equal(populated, await test.Case.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal)::text FROM weft.xml_preview_documents d"));
        Assert.Equal(XmlPreviewWorkflowTests.Input, (await service.ReadOutputAsync(test.Alice, test.Revision.OrganizationId, run, 0))!.Content);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1 AND project_id=$2", test.Alice.PrincipalId.Value, test.Revision.ProjectId);
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await service.SubmitAsync(test.Alice, submission)).Outcome);
        Assert.Equal(PreviewSubmitOutcome.NotAuthorizedOrNotFound, (await service.SubmitAsync(test.Alice, submission with { RequestKey = Guid.NewGuid() })).Outcome);

        Task<string> SnapshotAsync() => test.Case.ScalarAsync<string>("SELECT jsonb_build_array((SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM weft.preview_runs r),(SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal) FROM weft.preview_documents d))::text");
    }

    [PostgresFact]
    public async Task Xml_roundtrips_its_private_envelope_replay_authority_and_independent_failures()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, XmlPreviewWorkflowTests.Native);
        var submission = new PreviewRunSubmission(test.Revision, Guid.NewGuid(), PreviewTargetSelection.All,
            new(null, XmlPreviewWorkflowTests.Input, xmlMetadata: XmlDocumentMetadata.WithoutHints), [], [])
        { Profile = PreviewExecutionProfile.XmlDocument };
        await Assert.ThrowsAsync<PreviewRunException>(() => test.Service().SubmitAsync(test.Alice, submission));
        var service = test.Service(new() { XmlEnabled = true });
        var created = await service.SubmitAsync(test.Alice, submission);
        Assert.Equal(new PreviewSubmitResult(PreviewSubmitOutcome.Replayed, created.RunId), await service.SubmitAsync(test.Alice, submission));
        Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(new Backend(), new() { XmlEnabled = true })
            .ExecuteAsync(test.Revision.OrganizationId, created.RunId!.Value, Guid.NewGuid()));
        var saved = (await test.Reader.ReadAsync(test.Alice, test.Revision.OrganizationId, created.RunId.Value))!;
        Assert.Equal(XmlDocumentMetadata.WithoutHints, Assert.Single(saved.Outputs).XmlMetadata);
        var input = (await test.Reader.ReadInputAsync(test.Alice, test.Revision.OrganizationId, created.RunId.Value, 0))!;
        Assert.Equal(XmlPreviewWorkflowTests.Input, input.Content);
        Assert.Equal(XmlDocumentMetadata.WithoutHints, input.XmlMetadata);
        var output = (await test.Reader.ReadOutputAsync(test.Alice, test.Revision.OrganizationId, created.RunId.Value, 0))!;
        Assert.Equal(input.Content, output.Content);
        Assert.Null(await test.Reader.ReadOutputAsync(new(new PrincipalId(test.Catalog.Seed.Bob)), test.Revision.OrganizationId, created.RunId.Value, 0));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_documents WHERE run_id=$1", created.RunId.Value));
        foreach (var fail in new[] { true, false })
        {
            var run = (await service.SubmitAsync(test.Alice, submission with { RequestKey = Guid.NewGuid() })).RunId!.Value;
            Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(new Backend(fail), new() { XmlEnabled = true }).ExecuteAsync(test.Revision.OrganizationId, run, Guid.NewGuid()));
            Assert.Equal(fail ? PreviewRunOutcome.InvalidInput : PreviewRunOutcome.Succeeded, (await service.ReadAsync(test.Alice, test.Revision.OrganizationId, run))!.Outcome);
        }
    }

    [PostgresFact]
    public async Task Xml_schema_tampering_refuses_even_legacy_preview_before_private_reads()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("ALTER TABLE weft.xml_preview_documents DISABLE TRIGGER guard_xml_preview_document");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Service().SubmitAsync(test.Alice, test.Submission()));
    }

    internal sealed class Backend(bool fail = false) : IMappingPreviewBackend
    {
        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
        {
            var identity = new PreviewProvenance(request.Artifact.Scope, request.Artifact.Digest, "ferrule", "synthetic", "synthetic", "synthetic", true);
            return Task.FromResult(new MappingPreviewResult(fail ? MappingPreviewStatus.InvalidInput : MappingPreviewStatus.Succeeded, identity,
                XmlPreviewAdmission.Profile, fail ? 0 : 1, fail ? [] : [new("primary-output", "Order", false, "application/xml", 1, request.Primary.Bytes, xmlMetadata: XmlDocumentMetadata.WithoutHints)],
                fail ? new("preview.synthetic", "Synthetic isolated refusal.", false, null, false) : null));
        }
    }
}
