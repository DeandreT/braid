using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Xunit.Abstractions;

namespace Weft.Tests;

public sealed class CompiledRuntimeLiveFactAttribute : FactAttribute
{
    public CompiledRuntimeLiveFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("WEFT_COMPILED_RUNTIME_IMAGE") is null ||
            Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE") is null)
            Skip = "Provide the qualified Linux compiler and compiled-runtime images.";
    }
}

public sealed class CompiledRuntimeDispatchFactAttribute : FactAttribute
{
    public CompiledRuntimeDispatchFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || Environment.GetEnvironmentVariable("WEFT_COMPILED_RUNTIME_IMAGE") is null ||
            Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE") is null || Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE") is null)
            Skip = "Provide actual PostgreSQL and the qualified compiler, native and compiled-runtime images.";
    }
}

/// <summary>Actual compiler, interpreter and sandbox runtime; source-admission metadata here is explicitly synthetic.</summary>
public sealed class CompiledRuntimeLiveTests(ITestOutputHelper output)
{
    [CompiledRuntimeLiveFact]
    public async Task Compiled_json_targets_match_interpreted_bytes_and_failures_do_not_poison_later_invocations()
    {
        var source = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "preview-orders.json")))!;
        source["source_options"] = new JsonObject { ["json_document"] = true };
        source["target_options"] = new JsonObject { ["json_document"] = true };
        // Both the primary and selected named target require the same complete input set.
        // Native preview rejects unused named inputs; the generated API requires all declared inputs.
        source["extra_targets"]![0]!["schema"]!["kind"]!["children"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "currency",
            ["kind"] = new JsonObject { ["kind"] = "scalar", ["ty"] = "string" },
        });
        source["extra_targets"]![0]!["root"]!["bindings"]!.AsArray().Add(new JsonObject { ["target_field"] = "currency", ["node"] = 4 });
        var nativeBytes = JsonSerializer.SerializeToUtf8Bytes(source);
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var actor = new ActorContext(new(Guid.NewGuid()));
        var native = NativeArtifact.Admit(scope, nativeBytes, InspectionPolicy.MaximumNativeBytes);
        var compilerImage = Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE")!;
        var runtimeImage = Environment.GetEnvironmentVariable("WEFT_COMPILED_RUNTIME_IMAGE")!;
        await using var compilerRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", compilerImage, profile: DockerWorkerProfile.Build));
        var diagnostics = new RuntimeDiagnosticLauncher();
        await using var runtimeRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", runtimeImage, profile: DockerWorkerProfile.Compiled), diagnostics);
        await using var previewRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", compilerImage, profile: DockerWorkerProfile.Preview));
        var sourcePort = new Source(actor, native);
        var compiler = new FerruleBuildCompiler(compilerRunner, new(actor, sourcePort, sourcePort));
        var intent = new BuildIntent(Guid.NewGuid(), scope, native.Digest, compiler.Backend, FerruleBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.NamedInput, "rates", "json"),
             new(BuildBoundaryKind.PrimaryOutput, null, "json"), new(BuildBoundaryKind.NamedOutput, "summary", "json")],
            new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        var capability = await compiler.InspectAsync(intent);
        Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
        var artifact = await compiler.BuildAsync(intent, new(nativeBytes, nativeBytes.Length));
        var reference = BuildArtifactReference.Create(intent, capability, artifact.Digest, artifact.Length);
        var runtime = new FerruleCompiledBuildRuntime(runtimeRunner, compiler.Backend);
        var preview = new FerrulePreviewBackend(previewRunner);
        const string clock = "2026-10-20T01:02:03.500000001+02:00";
        var primary = new PreviewInputDocument(null, "{\"id\":\"SO-1\",\"lines\":[{\"sku\":\"A\",\"qty\":2},{\"sku\":\"B\",\"qty\":9007199254740993}]}"u8);
        PreviewInputDocument[] inputs = [new("rates", "{\"currency\":\"EUR\"}"u8)];
        PreviewParameter[] parameters = [new("region", PreviewParameterKind.String, "west"), new("express", PreviewParameterKind.Boolean, "true")];
        RegressionReplay Replay(PreviewTargetSelection selection, PreviewInputDocument? input = null, PreviewParameter[]? values = null) =>
            new(selection, clock, PreviewLimits.Default, input ?? primary, inputs, values ?? parameters);

        foreach (var target in new[] { PreviewTargetSelection.All, PreviewTargetSelection.Primary, PreviewTargetSelection.Named("summary") })
        {
            var replay = Replay(target);
            var interpreted = await preview.PreviewAsync(new(native, target, primary, inputs, parameters, clock, replay.Limits));
            Assert.True(interpreted.Status == MappingPreviewStatus.Succeeded, $"Interpreted {target.Kind}: {interpreted.Status}/{interpreted.Failure?.Code}");
            var expected = RegressionOutputSet.Capture(interpreted.Artifacts.Select(value => RegressionOutput.Capture(
                value.BoundaryId == "primary-output" ? RegressionOutputKey.Primary : RegressionOutputKey.Named(value.TargetName), value.Bytes)).ToArray());
            var actual = await runtime.ExecuteAsync(reference, artifact, replay);
            output.WriteLine("Compiled invocation transport: " + diagnostics.Summary);
            Assert.True(actual.Outcome == CompiledBuildOutcome.Completed, $"Compiled invocation: {actual.Outcome}; {diagnostics.Summary}");
            var comparison = new RegressionComparisonService().Compare(expected, new(Weft.Contracts.Operations.OperationStatusV1.Succeeded, actual.Outputs!));
            Assert.Equal(RegressionComparisonOutcome.Passed, comparison.Outcome);
            if (target.Kind != PreviewTargetKind.Named)
            {
                var body = JsonNode.Parse(actual.Outputs!.Outputs.Single(value => value.Key.IsPrimary).CopyBytes())!;
                Assert.Equal("SO-1", (string?)body["orderId"]); Assert.Equal("west", (string?)body["region"]);
                Assert.Equal(clock, (string?)body["generatedAt"]); Assert.Equal("EUR", (string?)body["currency"]);
                Assert.Equal(9007199254740993, (long)body["items"]![1]!["quantity"]!);
            }
            if (target.Kind != PreviewTargetKind.Primary)
            {
                var summary = JsonNode.Parse(actual.Outputs!.Outputs.Single(value => value.Key.Name == "summary").CopyBytes())!;
                Assert.Equal(2, (int)summary["lineCount"]!); Assert.Equal("west", (string?)summary["region"]);
                Assert.Equal("EUR", (string?)summary["currency"]);
            }
        }
        var malformed = await runtime.ExecuteAsync(reference, artifact, Replay(PreviewTargetSelection.All, new(null, "{"u8)));
        Assert.NotEqual(CompiledBuildOutcome.Completed, malformed.Outcome); Assert.Null(malformed.Outputs);
        var invalidParameter = await runtime.ExecuteAsync(reference, artifact, Replay(PreviewTargetSelection.All, values:
            [new("region", PreviewParameterKind.String, "west"), new("express", PreviewParameterKind.Boolean, "TRUE")]));
        Assert.Equal(CompiledBuildOutcome.InvalidParameter, invalidParameter.Outcome); Assert.Null(invalidParameter.Outputs);
        Assert.Equal(CompiledBuildOutcome.Completed, (await runtime.ExecuteAsync(reference, artifact, Replay(PreviewTargetSelection.All))).Outcome);
    }

    private sealed class Source(ActorContext actor, NativeArtifact native) : IBuildAdmissionReader, IPrivateObjectStore
    {
        private readonly PrivateObjectReference reference = new(PrivateObjectId.Restore(native.Scope, Guid.NewGuid(), PrivateObjectClass.NativeArtifact), native.Digest, native.ByteCount);
        public Task<BuildAdmission?> ReadAsync(ActorContext requested, BuildIntent intent, CancellationToken token = default) =>
            Task.FromResult<BuildAdmission?>(requested == actor && intent.NativeDigest == native.Digest
                ? new(actor.PrincipalId, intent.Fingerprint, reference) : null);
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token = default) => Task.FromResult(native.CopyBytes());
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes, CancellationToken token = default) => throw new NotSupportedException();
    }
}
