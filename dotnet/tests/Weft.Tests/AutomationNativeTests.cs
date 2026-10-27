using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Automation;
using Weft.Application.Regression;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AutomationNativeTests(PostgresFixture fixture)
{
    [AutomationServiceFact]
    public async Task Native_headerless_success_is_not_a_durable_EDI_pass_and_other_cases_continue()
    {
        using var workerRoot = new PrivateStorageTestRoot();
        var root = workerRoot.NewDirectory("comparison-worker");
        await using var inspection = new ProcessWorkerRunner(new(FerruleServiceTestSupport.Executable!, DockerWorkerProfile.Inspection, root));
        await using var runner = new ProcessWorkerRunner(new(FerruleServiceTestSupport.Executable!, DockerWorkerProfile.Preview, root));
        await using var native = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture, serviceRunner: inspection);
        var schema = JsonNode.Parse("""{"name":"Document","kind":{"kind":"group","children":[{"name":"ISA","kind":{"kind":"group","children":[{"name":"data","kind":{"kind":"scalar","ty":"string"}}]}}]}}""")!;
        var mapping = new JsonObject
        {
            ["source"] = schema.DeepClone(),
            ["target"] = schema.DeepClone(),
            ["source_options"] = new JsonObject { ["json_document"] = true },
            ["target_options"] = new JsonObject { ["edi_kind"] = "x12", ["x12_interchange_version"] = "00501" },
            ["graph"] = JsonNode.Parse("""{"nodes":{"1":{"kind":"source_field","path":["ISA","data"]}}}"""),
            ["root"] = JsonNode.Parse("""{"target_field":"","bindings":[],"children":[{"target_field":"ISA","bindings":[{"target_field":"data","node":1}],"children":[]}]}""")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mapping);
        var ingestion = new RevisionIngestionService(native.Metadata, native.Native.Metadata, new PostgresProjectReader(native.Native.ReaderDatabase),
            new(native.Storage, Weft.Storage.FileSystem.FileSystemStorageOptions.NativeHardMaximum),
            new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(inspection)]));
        var created = await ingestion.CreateAsync(native.Actor, native.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var receipt = await native.ReceiptAsync(created); var revision = receipt.Facts.FutureRevision;
        var scope = new MappingScope(revision.OrganizationId, revision.ProjectId, revision.MappingId);
        var legacy = new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, inspection.BuildIdentity, FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile);
        var document = new RegressionRuntimeIdentity("ferrule", legacy.EngineRevision, legacy.BackendBuild, FerrulePreviewBackend.DocumentAdapterBuild, DocumentPreviewPolicy.Profile);
        var input = new PreviewInputDocument(null, """{"ISA":{"data":"OK"}}"""u8);
        var backend = new FerrulePreviewBackend(runner);
        var preview = await backend.PreviewAsync(new(NativeArtifact.Admit(revision, bytes, bytes.Length), PreviewTargetSelection.All, input, [], [], "2026-10-25T12:00:00Z", PreviewLimits.Default)
        { Profile = PreviewExecutionProfile.Document });
        Assert.Equal(MappingPreviewStatus.Succeeded, preview.Status);
        var output = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, Assert.Single(preview.Artifacts).Bytes, mediaType: "application/edi-x12")]);
        await using var writer = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
        await using var reader = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_reader"), RegressionCaseDatabaseProfile.Reader);
        foreach (var policy in new[] { RegressionComparisonService.EdiSegmentsPolicy, RegressionComparisonService.ExactBytesPolicy })
            await writer.SaveAsync(native.Actor, new(Guid.NewGuid(), 1, native.Actor.PrincipalId, "native comparison", revision, receipt.Facts.NativeObject.Digest,
                document, new(PreviewTargetSelection.All, "2026-10-25T12:00:00Z", PreviewLimits.Default, input, [], []), policy, output,
                policy == RegressionComparisonService.EdiSegmentsPolicy ? new(false, true, true) : null));
        await using var queue = new PostgresAutomationStore(native.Case.RegressionConnectionString("weft_run_writer"), true);
        var suite = Guid.NewGuid();
        Assert.True(await queue.WriteAsync(native.Actor, native.Project, "recheck", suite, JsonSerializer.SerializeToElement(new { mappingId = scope.MappingId, revisionId = revision.RevisionId })));
        var executor = new RegressionCaseExecutionService(reader, new PostgresProjectReader(native.Native.ReaderDatabase), native.ReportReader, native.Physical, backend, legacy, document);
        await using var verifier = new PostgresAutomationComparisonSettler(native.Case.RegressionConnectionString("weft_comparison_settler"), document);
        for (var i = 0; i < 2; i++)
        {
            var claim = Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
            var result = await executor.EvaluateAsync(native.Actor, claim.Original, claim.Target);
            Assert.Equal(RegressionCaseExecutionOutcome.Completed, result.Outcome);
            Assert.True(await queue.FinishEvaluationAsync(claim, result, default));
            Assert.True(await verifier.SettleNextAsync(default));
        }
        Assert.Null(await queue.ClaimEvaluationAsync(default));
        Assert.Equal(1L, await native.Case.ScalarAsync<long>("SELECT count(*) FROM weft.automation_evaluations WHERE suite_id=$1 AND state='passed'", suite));
        Assert.Equal(1L, await native.Case.ScalarAsync<long>("SELECT count(*) FROM weft.automation_evaluations WHERE suite_id=$1 AND comparison_outcome='notEvaluated'", suite));
        Assert.Equal("failed", await native.Case.ScalarAsync<string>("SELECT state FROM weft.automation_suites WHERE suite_id=$1", suite));
    }

    [AutomationDockerFact]
    public Task Actual_new_revision_failure_and_recovery_use_the_original_immutable_case() => QualifyAsync(false);

    [AutomationServiceFact]
    public Task Actual_service_revision_failure_and_recovery_use_the_original_immutable_case() => QualifyAsync(true);

    private async Task QualifyAsync(bool service)
    {
        await using var native = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var root = native.Root.NewDirectory("automation-worker");
        var executable = Environment.GetEnvironmentVariable("WEFT_FERRULE_HOST_EXECUTABLE");
        var image = Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!;
        var docker = Environment.GetEnvironmentVariable("WEFT_DOCKER_EXECUTABLE") ?? "docker";
        IInspectionWorkerRunner inspection = service ? new ProcessWorkerRunner(new(executable!, DockerWorkerProfile.Inspection, root)) :
            new DockerInspectionWorkerRunner(new(docker, image, profile: DockerWorkerProfile.Inspection));
        await using var inspectionLifetime = (IAsyncDisposable)inspection;
        IInspectionWorkerRunner runner = service ? new ProcessWorkerRunner(new(executable!, DockerWorkerProfile.Preview, root)) :
            new DockerInspectionWorkerRunner(new(docker, image, profile: DockerWorkerProfile.Preview));
        await using var previewLifetime = (IAsyncDisposable)runner;
        var ingestion = new RevisionIngestionService(native.Metadata, native.Native.Metadata, new PostgresProjectReader(native.Native.ReaderDatabase),
            new(native.Storage, Weft.Storage.FileSystem.FileSystemStorageOptions.NativeHardMaximum),
            new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(inspection)]));
        var mapping = JsonNode.Parse(RevisionIngestionTestSupport.Fixture("valid-basic"))!;
        mapping["source_options"] = new JsonObject { ["json_document"] = true };
        mapping["target_options"] = new JsonObject { ["json_document"] = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mapping);
        var created = await ingestion.CreateAsync(native.Actor, native.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
        Assert.True(created.Outcome == RevisionIngestionOutcome.CommittedValid, $"Initial native admission: {created.Outcome}; category: {created.Failure}");
        var original = await native.ReceiptAsync(created);
        var revision = original.Facts.FutureRevision;
        var scope = new MappingScope(revision.OrganizationId, revision.ProjectId, revision.MappingId);
        var runtime = new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision,
            inspection.BuildIdentity, FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile);
        var expected = Encoding.UTF8.GetBytes("{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n");
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, native.Actor.PrincipalId, "Exact revision sample", revision,
            original.Facts.NativeObject.Digest, runtime, new(PreviewTargetSelection.All, "2026-10-25T12:00:00Z", PreviewLimits.Default,
                new(null, "{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}"u8), [], []),
            RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, expected)]));
        await using var cases = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
        await cases.SaveAsync(native.Actor, value);
        var originalEnvelope = new RegressionCaseEnvelopeCodec().Encode(value).CopyBytes();
        await using var queue = new PostgresAutomationStore(native.Case.RegressionConnectionString("weft_run_writer"), true);
        Assert.True(await queue.WriteAsync(native.Actor, native.Project, "subscription", null, JsonSerializer.SerializeToElement(new
        { events = new[] { "regressionFailed", "regressionRecovered" }, inApp = true, email = false, webhooks = Array.Empty<string>() })));
        await using var caseReader = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_reader"), RegressionCaseDatabaseProfile.Reader);
        var executor = new RegressionCaseExecutionService(caseReader, new PostgresProjectReader(native.Native.ReaderDatabase), native.ReportReader, native.Physical, new FerrulePreviewBackend(runner), runtime);

        mapping["root"]!["bindings"]![0]!["node"] = 2;
        var changed = await ingestion.AppendAsync(native.Actor, scope, Guid.NewGuid(), new(), new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(mapping)));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, changed.Outcome);
        var failed = Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
        Assert.Equal((await native.ReceiptAsync(changed)).Facts.FutureRevision, failed.Target.Revision);
        Assert.Equal(revision, failed.Original.Revision);
        var result = await executor.EvaluateAsync(native.Actor, failed.Original, failed.Target);
        Assert.Equal(RegressionComparisonOutcome.Failed, result.Comparison!.Outcome);
        Assert.True(await queue.FinishEvaluationAsync(failed, result, default));

        var restored = await ingestion.AppendAsync(native.Actor, scope, Guid.NewGuid(), new(), new MemoryStream(bytes));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, restored.Outcome);
        var passed = Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
        Assert.Equal((await native.ReceiptAsync(restored)).Facts.FutureRevision, passed.Target.Revision);
        var recovery = await executor.EvaluateAsync(native.Actor, passed.Original, passed.Target);
        Assert.Equal(RegressionComparisonOutcome.Passed, recovery.Comparison!.Outcome);
        Assert.True(await queue.FinishEvaluationAsync(passed, recovery, default));
        Assert.Null(await queue.ClaimEvaluationAsync(default));
        Assert.Equal(originalEnvelope, new RegressionCaseEnvelopeCodec().Encode((await caseReader.ReadAsync(native.Actor, scope, value.CaseId, 1))!).CopyBytes());
        var inbox = await queue.ReadAsync(native.Actor, native.Project, "notifications", null, null, 10);
        Assert.Equal(new[] { "regressionFailed", "regressionRecovered" }, inbox!.Value.GetProperty("notifications").EnumerateArray().Select(n => n.GetProperty("event").GetString()!).Order());
        Assert.Equal(1L, await native.Case.ScalarAsync<long>("SELECT count(*) FROM weft.regression_case_versions"));

        // Revoke after the real executor's last authority check, before durable completion.
        var revokedSuite = Guid.NewGuid();
        Assert.True(await queue.WriteAsync(native.Actor, native.Project, "recheck", revokedSuite,
            JsonSerializer.SerializeToElement(new { mappingId = scope.MappingId, revisionId = passed.Target.Revision.RevisionId })));
        var revoked = Assert.IsType<AutomationClaim>(await queue.ClaimEvaluationAsync(default));
        var completedBeforeRevocation = await executor.EvaluateAsync(native.Actor, revoked.Original, revoked.Target);
        Assert.Equal(RegressionComparisonOutcome.Passed, completedBeforeRevocation.Comparison!.Outcome);
        await native.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            scope.OrganizationId, scope.ProjectId, native.Actor.PrincipalId.Value);
        Assert.False(await queue.FinishEvaluationAsync(revoked, completedBeforeRevocation, default));
        Assert.Equal("revoked", await native.Case.ScalarAsync<string>("SELECT state FROM weft.automation_evaluations WHERE suite_id=$1", revokedSuite));
        Assert.True(await native.Case.ScalarAsync<bool>("SELECT result_body IS NULL FROM weft.automation_evaluations WHERE suite_id=$1", revokedSuite));
        Assert.Equal("failed", await native.Case.ScalarAsync<string>("SELECT state FROM weft.automation_suites WHERE suite_id=$1", revokedSuite));
    }
}

public sealed class AutomationDockerFactAttribute : FactAttribute
{
    public AutomationDockerFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || Environment.GetEnvironmentVariable("WEFT_AUTOMATION_DOCKER_TESTS") != "true")
            Skip = "Set WEFT_AUTOMATION_DOCKER_TESTS=true on the actual Docker host; Podman is not an admitted Docker worker sandbox.";
    }
}

public sealed class AutomationServiceFactAttribute : FactAttribute
{
    public AutomationServiceFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || string.IsNullOrEmpty(FerruleServiceTestSupport.Executable))
            Skip = "Configure actual PostgreSQL and the pinned service worker executable for exact-revision qualification.";
    }
}
