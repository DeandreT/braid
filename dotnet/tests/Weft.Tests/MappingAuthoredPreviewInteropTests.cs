using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class MappingAuthoredPreviewInteropTests
{
    [NativeAuthoredPreviewFact]
    public async Task Actual_creation_generation_and_preview_preserve_exact_proposal_without_catalog_identity()
    {
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid()); var actor = new ActorContext(new(Guid.NewGuid()));
        var bundle = new SpecBundleScope(project, Guid.NewGuid());
        var source = new SpecCapturedSource(new(bundle, Guid.NewGuid(), 1), SpecSourceFormat.Text, "Map code to item and qty to quantity."u8);
        var specification = new CreationSpecification(bundle, [source], [SpecSourceExtractor.Extract(source)]);
        var plan = new MappingCreationPlan([new("code", CreationScalarType.Text, true), new("qty", CreationScalarType.Number, true)],
            [new("item", CreationScalarType.Text, true), new("quantity", CreationScalarType.Number, true)], [new("code", "item"), new("qty", "quantity")]);
        var compiler = new FerruleMappingCreationCompiler(new(new NativeRunner("WEFT_AUTHORING_TEST_EXECUTABLE", MappingAuthoringPolicy.MaximumResponseBytes)));
        var proposal = await new MappingCreationGenerationService(compiler, new Current()).GenerateAsync(actor, Guid.NewGuid(), Guid.NewGuid(),
            specification, plan, new([new(source.Identity, 0)], [], []), new("Created from specification", "Verified sample execution"));
        var runner = new FerruleMappingCreationPreview(new(new NativeRunner("WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE", MappingAuthoredPreviewPolicy.MaximumResponseBytes)));
        var sample = "{\"code\":\"A42\",\"qty\":2}"u8.ToArray();
        var result = await new MappingCreationPreviewService(runner, new Current()).PreviewAsync(actor, proposal, sample);
        Assert.NotNull(result); Assert.Equal(CreationPreviewOutcome.Succeeded, result.Outcome);
        Assert.Equal(proposal.Fingerprint, result.Subject.Proposal); Assert.Equal(proposal.Compilation.NativeDigest, result.Subject.Native);
        Assert.Equal(Sha256Digest.FromBytes(sample), result.Subject.Sample);
        Assert.Equal("json", result.Format); Assert.Equal("application/json", result.MediaType);
        var output = JsonNode.Parse(result.CopyOutput())!;
        Assert.Equal("A42", (string?)output["item"]); Assert.Equal(2.0, (double?)output["quantity"]);
        var refused = await runner.RunAsync(proposal, "invalid"u8.ToArray(), default);
        Assert.Equal(CreationPreviewOutcome.Failed, refused.Outcome); Assert.Empty(refused.CopyOutput());
        Assert.Null(refused.Format); Assert.Null(refused.MediaType);
    }

    [NativeAuthoredPreviewFact]
    public async Task Actual_compiler_to_actual_preview_executes_json_csv_tsv_and_xml_with_exact_context()
    {
        const string plan = """
            {"format":"weft.authoring.v1",
             "source":{"schema":{"name":"Input","shape":{"kind":"group","children":[{"name":"value","shape":{"kind":"scalar","scalar":"string"}}]}},"options":{"kind":"json"}},
             "target":{"schema":{"name":"Output","shape":{"kind":"group","children":[{"name":"value","shape":{"kind":"scalar","scalar":"string"}}]}},"options":{"kind":"json"}},
             "edit":{"format":"ferrule.edit.v1","operations":[{"op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"value"},"expression":{"kind":"source","id":"1","path":["value"]}}]}}
            """;
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid()); var authoringId = Guid.NewGuid();
        var compiler = new FerruleInspectionBackend(new NativeRunner("WEFT_AUTHORING_TEST_EXECUTABLE", MappingAuthoringPolicy.MaximumResponseBytes));
        var preview = new FerruleInspectionBackend(new NativeRunner("WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE", MappingAuthoredPreviewPolicy.MaximumResponseBytes));
        foreach (var format in new[] { "json", "csv", "tsv", "xml" })
        {
            var authoredPlan = JsonNode.Parse(plan)!;
            authoredPlan["target"]!["options"] = format is "csv" or "tsv" ? new JsonObject
            {
                ["kind"] = format,
                ["header"] = true,
                ["quote"] = true,
                ["bom"] = false,
                ["preserve_empty_strings"] = true
            } : new JsonObject { ["kind"] = format };
            var compiled = await compiler.AuthorAsync(project, authoringId, Encoding.UTF8.GetBytes(authoredPlan.ToJsonString()));
            Assert.Null(compiled.Failure); var checkpoint = Assert.IsType<MappingAuthoringCheckpoint>(compiled.Checkpoint);
            Assert.True(checkpoint.Validation.IsValid);
            var native = checkpoint.CopyNativeBytes(); var sample = "{\"value\":\"a,<é>\"}"u8.ToArray();
            var subject = new MappingAuthoredPreviewSubject(project, authoringId, Sha256Digest.FromBytes("reviewed proposal"u8),
                checkpoint.NativeDigest, Sha256Digest.FromBytes(sample));
            var result = await preview.PreviewAuthoredAsync(subject, native, sample);
            Assert.Null(result.Failure); var evidence = Assert.IsType<MappingAuthoredPreviewEvidence>(result.Evidence);
            Assert.Equal(subject, evidence.Subject); Assert.Equal(format, evidence.Format);
            Assert.Equal(Sha256Digest.FromBytes(evidence.CopyOutput()), evidence.OutputDigest);
            var text = Encoding.UTF8.GetString(evidence.CopyOutput());
            switch (format)
            {
                case "json": Assert.Equal("a,<é>", (string?)JsonNode.Parse(text)!["value"]); break;
                case "xml": Assert.Equal("a,<é>", System.Xml.Linq.XDocument.Parse(text).Root!.Element("value")!.Value); break;
                case "csv": Assert.Equal("value\n\"a,<é>\"\n", text); break;
                case "tsv": Assert.Equal("value\na,<é>\n", text); break;
            }
            var malformed = "not valid JSON"u8.ToArray();
            var refused = await preview.PreviewAuthoredAsync(subject with { SampleDigest = Sha256Digest.FromBytes(malformed) }, native, malformed);
            Assert.Null(refused.Evidence); Assert.Equal("invalidSample", refused.Failure!.Code);
        }
    }

    private sealed class NativeRunner(string setting, int maximumOutput) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "portable-native-interop-not-isolation";
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable(setting)!)
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            async Task<byte[]> Drain(Stream stream, int maximum)
            {
                using var result = new MemoryStream(); var buffer = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, timeout.Token); if (count == 0) return result.ToArray();
                    if (result.Length + count > maximum) { timeout.Cancel(); throw new InvalidOperationException("Worker response exceeded fixture bound."); }
                    result.Write(buffer, 0, count);
                }
            }
            try
            {
                var output = Drain(process.StandardOutput.BaseStream, maximumOutput); var stderr = Drain(process.StandardError.BaseStream, 65536);
                await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token); process.StandardInput.Close();
                await Task.WhenAll(output, stderr, process.WaitForExitAsync(timeout.Token));
                Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr);
                return new(await output, WorkerProcessFailure.None);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
    }

    private sealed class Current : IMappingCreationAuthority
    {
        public Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct) => Task.FromResult(CreationAuthority.Current);
    }

    public sealed class NativeAuthoredPreviewFactAttribute : FactAttribute
    {
        public NativeAuthoredPreviewFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WEFT_AUTHORING_TEST_EXECUTABLE") is null ||
                Environment.GetEnvironmentVariable("WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE") is null)
                Skip = "Set both portable Rust protocol executables for actual compiler/preview interoperability; not sandbox qualification.";
        }
    }
}
