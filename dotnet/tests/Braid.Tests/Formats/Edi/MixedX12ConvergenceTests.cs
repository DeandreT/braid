using System.Text;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTestSupport;

namespace Braid.Tests;

[Collection(FerruleServiceProcessCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MixedX12ConvergenceTests
{
    [ServiceCompileTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Explicit_interchange_profile_preserves_two_records_and_cross_role_composites_through_native_and_compiled_execution(bool configured, bool output)
    {
        var root = TemporaryRoot();
        try
        {
            var readerBytes = DocumentPreviewTestData.InterchangeNative(configured);
            var bytes = readerBytes;
            await using var preview = new ProcessWorkerRunner(new(Executable!, DockerWorkerProfile.Preview, root));
            byte[]? outputInput = null;
            if (output)
            {
                var read = await new FerrulePreviewBackend(preview).PreviewAsync(new(NativeArtifact.Admit(Scope, readerBytes, PreviewPolicy.MaximumNativeBytes), PreviewTargetSelection.Primary,
                    new(null, Encoding.UTF8.GetBytes(DocumentPreviewTestData.MixedX12(false))), [], [], "2026-10-25T12:00:00Z", PreviewLimits.Default)
                { Profile = PreviewExecutionProfile.Document });
                Assert.Equal(MappingPreviewStatus.Succeeded, read.Status);
                outputInput = Assert.Single(read.Artifacts).Bytes.ToArray();
                var writer = JsonNode.Parse(readerBytes)!;
                writer["source_options"] = new JsonObject { ["json_document"] = true };
                var options = new JsonObject { ["edi_kind"] = "x12", ["x12_interchange_version"] = "00501" };
                writer["target_options"] = options;
                writer["extra_targets"]![0]!["options"] = options.DeepClone();
                bytes = Encoding.UTF8.GetBytes(writer.ToJsonString());
            }
            await using var compilerRunner = new ProcessCompileRunner(CompileOptions(root));
            var actor = new ActorContext(new(Guid.NewGuid()));
            var nativeRef = new PrivateObjectReference(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(bytes), bytes.Length);
            var source = new FerruleServiceCompileTests.NativeSource(actor, nativeRef, bytes);
            var compiler = new FerruleBuildCompiler(compilerRunner, new(actor, source, source));
            BuildIntent Intent(BuildRuntimeProfile profile) => new(Guid.NewGuid(), Scope, nativeRef.Digest, compiler.DocumentBackend, profile,
                [new(BuildBoundaryKind.PrimaryInput, null, output ? "json" : "x12"), new(BuildBoundaryKind.PrimaryOutput, null, output ? "x12" : "json"), new(BuildBoundaryKind.NamedOutput, "copy", output ? "x12" : "json")],
                new(new(Scope.OrganizationId, Scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            var single = Intent(FerruleBuildCompiler.DocumentProfile);
            Assert.Equal(BuildPreflightOutcome.Unsupported, new BuildPreflightService().Evaluate(single, await compiler.InspectAsync(single)).Outcome);
            var intent = Intent(FerruleBuildCompiler.X12InterchangesProfile);
            var capability = await compiler.InspectAsync(intent);
            Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
            var artifact = await compiler.BuildAsync(intent, new(bytes, bytes.Length));
            var reference = BuildArtifactReference.Create(intent, capability, artifact.Digest, artifact.Length);
            await using var child = new ProcessCompiledRuntimeRunner(FerruleCompiledServiceTests.Options(root));
            var runtime = new FerruleCompiledBuildRuntime(child, compiler.Backend);
            foreach (var crossRole in configured || output ? new[] { false } : new[] { false, true })
                foreach (var target in new[] { PreviewTargetSelection.All, PreviewTargetSelection.Primary, PreviewTargetSelection.Named("copy") })
                {
                    var input = new PreviewInputDocument(null, outputInput ?? Encoding.UTF8.GetBytes(DocumentPreviewTestData.MixedX12(crossRole)));
                    var replay = new RegressionReplay(target, "2026-10-25T12:00:00Z", PreviewLimits.Default, input, [], []);
                    var interpreted = await new FerrulePreviewBackend(preview).PreviewAsync(new(NativeArtifact.Admit(Scope, bytes, PreviewPolicy.MaximumNativeBytes), target, input, [], [], replay.CurrentDateTime, replay.Limits) { Profile = PreviewExecutionProfile.Document });
                    Assert.Equal(MappingPreviewStatus.Succeeded, interpreted.Status);
                    var compiled = await runtime.ExecuteAsync(reference, artifact, replay);
                    Assert.Equal(CompiledBuildOutcome.Completed, compiled.Outcome);
                    Assert.Equal(interpreted.Artifacts.Count, compiled.Outputs!.Outputs.Count);
                    for (var i = 0; i < interpreted.Artifacts.Count; i++)
                    {
                        Assert.Equal(interpreted.Artifacts[i].Bytes, compiled.Outputs.Outputs[i].CopyBytes());
                        if (!output) AssertRecords(compiled.Outputs.Outputs[i].CopyBytes());
                        else
                        {
                            var roundtrip = await new FerrulePreviewBackend(preview).PreviewAsync(new(NativeArtifact.Admit(Scope, readerBytes, PreviewPolicy.MaximumNativeBytes), PreviewTargetSelection.Primary,
                                new(null, compiled.Outputs.Outputs[i].CopyBytes()), [], [], replay.CurrentDateTime, replay.Limits)
                            { Profile = PreviewExecutionProfile.Document });
                            Assert.Equal(MappingPreviewStatus.Succeeded, roundtrip.Status);
                            AssertRecords(Assert.Single(roundtrip.Artifacts).Bytes.ToArray());
                        }
                    }
                }
            if (configured)
            {
                var input = new PreviewInputDocument(null, Encoding.UTF8.GetBytes(DocumentPreviewTestData.MixedX12()));
                var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-25T12:00:00Z", PreviewLimits.Default, input, [], []);
                var interpreted = await new FerrulePreviewBackend(preview).PreviewAsync(new(NativeArtifact.Admit(Scope, bytes, PreviewPolicy.MaximumNativeBytes), replay.Target, input, [], [], replay.CurrentDateTime, replay.Limits) { Profile = PreviewExecutionProfile.Document });
                Assert.Equal(MappingPreviewStatus.InvalidInput, interpreted.Status);
                Assert.Empty(interpreted.Artifacts);
                var rejected = await runtime.ExecuteAsync(reference, artifact, replay);
                Assert.Equal(CompiledBuildOutcome.ExecutionFailed, rejected.Outcome);
                Assert.Null(rejected.Outputs);
            }
            var oversized = new PreviewInputDocument(null, Encoding.UTF8.GetBytes((output ? Encoding.UTF8.GetString(outputInput!) : DocumentPreviewTestData.MixedX12(false)).Replace("SECOND", new string('X', 20_000), StringComparison.Ordinal)));
            var limitedReplay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-25T12:00:00Z", PreviewLimits.Default, oversized, [], []);
            var limited = await new FerrulePreviewBackend(preview).PreviewAsync(new(NativeArtifact.Admit(Scope, bytes, PreviewPolicy.MaximumNativeBytes), limitedReplay.Target, oversized, [], [], limitedReplay.CurrentDateTime, limitedReplay.Limits) { Profile = PreviewExecutionProfile.Document });
            Assert.Equal(MappingPreviewStatus.ResourceLimit, limited.Status);
            Assert.Empty(limited.Artifacts);
            var compiledLimited = await runtime.ExecuteAsync(reference, artifact, limitedReplay);
            Assert.Equal(output ? CompiledBuildOutcome.ResourceLimit : CompiledBuildOutcome.InvalidInput, compiledLimited.Outcome);
            Assert.Null(compiledLimited.Outputs);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static void AssertRecords(byte[] bytes)
    {
        var values = JsonNode.Parse(bytes)!["Interchanges"]!.AsArray();
        Assert.Equal(2, values.Count);
        foreach (var (index, id) in new[] { "BRAID-330", "SECOND" }.Index())
        {
            var body = values[index]!["Groups"]![0]!["Messages"]![0]!["BEG"]!;
            Assert.Equal(id, (string?)body["e3"]);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{"c1":"A","c2":"B"},{"c1":"C","c2":"D"}]"""), body["e4"]));
        }
    }
}
