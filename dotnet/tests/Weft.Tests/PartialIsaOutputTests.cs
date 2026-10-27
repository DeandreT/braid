using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests;

[Collection(FerruleServiceProcessCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PartialIsaOutputTests
{
    [ServiceCompileFact]
    public async Task Native_partial_isa_outputs_use_the_mapping_terminator_and_publish_no_oversized_artifacts()
        => await NativeHeaderlessOutput('~');

    [ServiceCompileFact]
    public async Task Native_unicode_headerless_output_preserves_small_segments_and_refuses_oversized_segments()
        => await NativeHeaderlessOutput('\u2020');

    private static async Task NativeHeaderlessOutput(char terminator)
    {
        var root = TemporaryRoot();
        try
        {
            var schema = JsonNode.Parse("""{"name":"Document","kind":{"kind":"group","children":[{"name":"ISA","kind":{"kind":"group","children":[{"name":"data","kind":{"kind":"scalar","ty":"string"}}]}}]}}""")!;
            var scope = JsonNode.Parse("""{"target_field":"","bindings":[],"children":[{"target_field":"ISA","bindings":[{"target_field":"data","node":1}],"children":[]}]}""")!;
            var options = new JsonObject { ["edi_kind"] = "x12", ["x12_interchange_version"] = "00501" };
            if (terminator != '~') options["x12_separators"] = new JsonObject
            { ["element"] = "*", ["component"] = ":", ["segment"] = terminator.ToString(), ["repetition"] = "^", ["release"] = null };
            var mapping = new JsonObject
            {
                ["source"] = schema.DeepClone(),
                ["target"] = schema.DeepClone(),
                ["source_options"] = new JsonObject { ["json_document"] = true },
                ["target_options"] = options.DeepClone(),
                ["graph"] = JsonNode.Parse("""{"nodes":{"1":{"kind":"source_field","path":["ISA","data"]}}}"""),
                ["root"] = scope,
                ["extra_targets"] = new JsonArray(new JsonObject { ["name"] = "copy", ["schema"] = schema.DeepClone(), ["root"] = scope.DeepClone(), ["options"] = options.DeepClone() })
            };
            var native = NativeArtifact.Admit(Scope, Encoding.UTF8.GetBytes(mapping.ToJsonString()), PreviewPolicy.MaximumNativeBytes);
            await using var runner = new ProcessWorkerRunner(new(Executable!, DockerWorkerProfile.Preview, root));
            var backend = new FerrulePreviewBackend(runner);
            var small = terminator == '~' ? "OK" : "X X";
            var oversized = terminator == '~' ? new string('X', 20_000) : string.Concat(Enumerable.Repeat("X ", 10_000));
            foreach (var value in new[] { small, oversized })
                foreach (var target in new[] { PreviewTargetSelection.All, PreviewTargetSelection.Primary, PreviewTargetSelection.Named("copy") })
                {
                    var input = JsonSerializer.SerializeToUtf8Bytes(new { ISA = new { data = value } });
                    var result = await backend.PreviewAsync(new(native, target, new(null, input), [], [], "2026-10-25T12:00:00Z", PreviewLimits.Default)
                    { Profile = PreviewExecutionProfile.Document });
                    Assert.Equal(value.Length > 16_384 ? MappingPreviewStatus.ResourceLimit : MappingPreviewStatus.Succeeded, result.Status);
                    if (value.Length > 16_384) Assert.Empty(result.Artifacts);
                    else Assert.All(result.Artifacts, artifact => Assert.Equal("ISA*" + small + terminator, Encoding.UTF8.GetString(artifact.Bytes).TrimEnd()));
                }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [ServiceCompileFact]
    public async Task Actual_compiled_runtime_refuses_partial_and_malformed_isa_bytes_from_an_emitter()
    {
        var root = TemporaryRoot();
        try
        {
            // A deterministic boundary probe stands in for a faulty emitter. It is
            // compiled with the pinned SDK and runs through the actual restricted
            // host; it does not replace any production mapping or compiler policy.
            var compile = Path.Combine(root, "compile");
            Directory.CreateDirectory(compile);
            var files = new[] { ("GeneratedMapping.cs", ProbeSource), ("GeneratedTargetBuilder.cs", "// probe\n"), ("Runtime/Probe.cs", "// probe\n") };
            var reply = new BuildWorkerProtocol.Reply(new(BuildWorkerProtocol.Protocol, null, null, null, null,
                BuildWorkerProtocol.Engine, BuildWorkerProtocol.DocumentProfile, "supported", [],
                files.Select(file => new BuildWorkerProtocol.File(file.Item1, Encoding.UTF8.GetByteCount(file.Item2), Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(file.Item2)).Value)).ToArray()),
                files.Select(file => Encoding.UTF8.GetBytes(file.Item2)).ToArray());
            await BuildCompilation.PrepareAsync(compile, Toolchain, reply, default);
            var start = new ProcessStartInfo(BuildCompilation.Host(Toolchain)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in BuildCompilation.CompilerArguments(Toolchain, compile)) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
            }
            var artifact = new BuildBytes(await File.ReadAllBytesAsync(Path.Combine(compile, BuildCompilation.Output)), BuildWorkerProtocol.MaximumSourceBytes);
            Directory.Delete(compile, true);
            await using var runner = new ProcessCompiledRuntimeRunner(FerruleCompiledServiceTests.Options(root));
            var backend = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, runner.BuildIdentity, FerrulePreviewBackend.DocumentAdapterBuild);
            var intent = new BuildIntent(Guid.NewGuid(), Scope, Sha256Digest.FromBytes("boundary probe"u8), backend, FerruleBuildCompiler.DocumentProfile,
                [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "x12"), new(BuildBoundaryKind.NamedOutput, "copy", "x12")],
                new(new(Scope.OrganizationId, Scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            var reference = BuildArtifactReference.Restore(intent, artifact.Digest, artifact.Length);
            var runtime = new FerruleCompiledBuildRuntime(runner, backend);
            var partial = "ISA*" + new string('X', 20_000) + "~";
            var invalid = DocumentPreviewTestData.X12[..106].ToCharArray(); invalid[6] = 'X'; invalid[105] = '!';
            var malformed = new string(invalid) + string.Concat(Enumerable.Repeat("X!", 10_000)) + "~";
            var complete = string.Concat(Enumerable.Repeat(DocumentPreviewTestData.X12.Replace('*', '|').Replace('~', '!'), 100));
            foreach (var (primary, named, oversized) in new[] { (partial, partial, true), (malformed, malformed, true), ("ISA*OK~", partial, true), (complete, complete, false), ("ISA*OK~", "ISA*OK~", false) })
                foreach (var target in new[] { PreviewTargetSelection.All, PreviewTargetSelection.Primary, PreviewTargetSelection.Named("copy") })
                {
                    var expectedFailure = oversized && (target.Kind != PreviewTargetKind.Primary || primary != "ISA*OK~");
                    var replay = new RegressionReplay(target, "2026-10-25T12:00:00Z", PreviewLimits.Default,
                        new(null, JsonSerializer.SerializeToUtf8Bytes(new { primary, named })), [], []);
                    var result = await runtime.ExecuteAsync(reference, artifact, replay);
                    Assert.Equal(expectedFailure ? CompiledBuildOutcome.ResourceLimit : CompiledBuildOutcome.Completed, result.Outcome);
                    if (expectedFailure) Assert.Null(result.Outputs);
                    else Assert.All(result.Outputs!.Outputs, output => Assert.Equal(output.Key.IsPrimary ? primary : named, Encoding.UTF8.GetString(output.CopyBytes())));
                }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private const string ProbeSource = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Text;
        using System.Text.Json;
        namespace Ferrule.Runtime {
            public sealed class FerruleValue {
                public static FerruleValue FromString(string value) => new();
                public static FerruleValue FromBoolean(bool value) => new();
                public static FerruleValue FromInt64(long value) => new();
                public static FerruleValue FromDouble(double value) => new();
            }
            public sealed class FerruleRuntimeParameters { public FerruleRuntimeParameters(IEnumerable<KeyValuePair<string, FerruleValue>> values) { } }
            public sealed class FerruleExecutionContext { public FerruleExecutionContext(string a, string b, string c, FerruleRuntimeParameters d) { } }
            public sealed record FerruleTargetSelection(bool IsPrimary) {
                public static FerruleTargetSelection Primary() => new(true);
                public static FerruleTargetSelection Named(string name) => new(false);
            }
        }
        namespace Ferrule.Generated {
            using Ferrule.Runtime;
            public sealed record NamedDocumentBytesInput(string Name, byte[] Document);
            public enum Format { X12 }
            public sealed record NamedOutput(string Name, byte[] Document, Format Format);
            public static class GeneratedMapping {
                private static byte[] Body(byte[] input, string field) { using var json = JsonDocument.Parse(input); return Encoding.UTF8.GetBytes(json.RootElement.GetProperty(field).GetString()!); }
                public static object ExecuteDocumentBytesOutputsWithHost(byte[] input, IReadOnlyList<NamedDocumentBytesInput> sources, FerruleExecutionContext context)
                    => new { Primary = Body(input, "primary"), PrimaryFormat = Format.X12, Extras = new[] { new NamedOutput("copy", Body(input, "named"), Format.X12) } };
                public static object ExecuteDocumentBytesSelectedTargetWithHost(byte[] input, FerruleTargetSelection target, IReadOnlyList<NamedDocumentBytesInput> sources, FerruleExecutionContext context)
                    => target.IsPrimary ? (object)new { Document = Body(input, "primary"), Format = Format.X12 } : new { Output = new NamedOutput("copy", Body(input, "named"), Format.X12) };
            }
        }
        namespace Weft.Generated { public static class DocumentSyntax { public static int OutputSegment(string? name) => 126; } }
        """;
}
