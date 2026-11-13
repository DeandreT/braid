using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests;

/// <summary>Actual creation profiles through the verified Linux service-child runner, not portable protocol examples.</summary>
[Collection(FerruleServiceProcessCollection.Name)]
public sealed class CreationSandboxQualificationTests
{
    private static readonly string[] Formats = ["json", "xml", "csv", "tsv", "x12", "edifact"];
    private const string X12 = "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *261112*1200*U*00401*000000001*0*P*:~GS*PO*SENDER*RECEIVER*20261112*1200*1*X*004010~ST*850*0001~BEG*00*SA*alpha*beta~SE*3*0001~GE*1*1~IEA*1*000000001~";
    private const string Edifact = "UNB+UNOC:3+SENDER+RECEIVER+261112:1200+1'UNH+1+ORDERS:D:96A:UN'BGM+220+alpha+beta'UNT+3+1'UNZ+1+1'";

    [ServiceWorkerFact]
    public async Task All_36_pairs_author_and_preview_exact_bytes_with_the_same_sandboxed_build()
    {
        var root = TemporaryRoot();
        try
        {
            await using var author = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Authoring));
            await using var preview = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.AuthoredPreview));
            var identity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Executable!)));
            Assert.Equal(identity, author.BuildIdentity); Assert.Equal(identity, preview.BuildIdentity);
            foreach (var sourceFormat in Formats)
            foreach (var targetFormat in Formats)
            {
                var source = Fixture(sourceFormat); var target = Fixture(targetFormat);
                var plan = new MappingCreationPlan(new CreationPlanV2(source.Boundary, target.Boundary,
                    TargetScope(target.Boundary.Schema, target.Constants, source.Paths, target.Paths, [])));
                var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid()); var authoring = Guid.NewGuid();
                var compiled = await new FerruleInspectionBackend(author).AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes());
                Assert.True(compiled.Failure is null, $"{sourceFormat}->{targetFormat}: {JsonSerializer.Serialize(compiled.Failure)}");
                var checkpoint = Assert.IsType<MappingAuthoringCheckpoint>(compiled.Checkpoint);
                Assert.True(checkpoint.Validation.IsValid, JsonSerializer.Serialize(checkpoint.Validation));
                Assert.NotEmpty(checkpoint.Projection.Fields);
                Assert.Equal(project, checkpoint.Project); Assert.Equal(authoring, checkpoint.AuthoringId);
                Assert.Equal(plan.CompilerDigest, checkpoint.PlanDigest); Assert.Equal(identity, checkpoint.WorkerBuild);
                Assert.Equal(FerruleInspectionBackend.EngineRevision, checkpoint.EngineRevision);
                var native = checkpoint.CopyNativeBytes(); Assert.Equal(Sha256Digest.FromBytes(native), checkpoint.NativeDigest);
                var replay = await new FerruleInspectionBackend(author).AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes());
                Assert.Null(replay.Failure); Assert.Equal(native, replay.Checkpoint!.CopyNativeBytes());
                foreach (var changed in new[] { false, true })
                {
                    var sample = Encoding.UTF8.GetBytes(changed ? source.Sample.Replace("alpha", "changedA").Replace("beta", "changedB") : source.Sample);
                    var subject = new MappingAuthoredPreviewSubject(project, authoring, plan.Digest, checkpoint.NativeDigest, Sha256Digest.FromBytes(sample));
                    var result = await new FerruleInspectionBackend(preview).PreviewAuthoredAsync(subject, native, sample);
                    Assert.True(result.Failure is null, $"{sourceFormat}->{targetFormat}: {JsonSerializer.Serialize(result.Failure)}");
                    var evidence = Assert.IsType<MappingAuthoredPreviewEvidence>(result.Evidence);
                    Assert.Equal(subject, evidence.Subject); Assert.Equal(identity, evidence.WorkerBuild);
                    Assert.Equal(targetFormat, evidence.Format); Assert.Equal(FerruleInspectionBackend.EngineRevision, evidence.EngineRevision);
                    var output = evidence.CopyOutput(); Assert.Equal(output.Length, evidence.OutputByteCount);
                    Assert.Equal(Sha256Digest.FromBytes(output), evidence.OutputDigest);
                    AssertOutput(targetFormat, Encoding.UTF8.GetString(output), changed ? "changedA" : "alpha", changed ? "changedB" : "beta");
                    AssertSettled(root);
                }
            }
            Assert.True(author.Available); Assert.True(preview.Available);
        }
        finally { RemoveSettledRoot(root); }
    }

    [ServiceWorkerFact]
    public async Task Fixed_profiles_refuse_cross_profile_dispatch_and_bound_invalid_inputs()
    {
        var root = TemporaryRoot();
        try
        {
            var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid()); var authoring = Guid.NewGuid();
            var plan = Simple(); var sample = "{\"code\":\"alpha\",\"other\":\"beta\"}"u8.ToArray();
            await using var author = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Authoring));
            await using var preview = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.AuthoredPreview));
            var compiled = await new FerruleInspectionBackend(author).AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes());
            Assert.Null(compiled.Failure); var checkpoint = Assert.IsType<MappingAuthoringCheckpoint>(compiled.Checkpoint);
            var native = checkpoint.CopyNativeBytes();
            var subject = new MappingAuthoredPreviewSubject(project, authoring, plan.Digest, checkpoint.NativeDigest, Sha256Digest.FromBytes(sample));
            var authoringHeader = HeaderFrame(FerruleInspectionBackend.AuthoringProtocolVersion);
            var previewHeader = HeaderFrame(FerruleInspectionBackend.AuthoredPreviewProtocolVersion);
            foreach (var profile in new[] { DockerWorkerProfile.Inspection, DockerWorkerProfile.Edit, DockerWorkerProfile.Authoring,
                DockerWorkerProfile.AuthoredPreview, DockerWorkerProfile.Preview, DockerWorkerProfile.Build, DockerWorkerProfile.Interchange })
            {
                await using var wrong = new ProcessWorkerRunner(Options(root, profile));
                var backend = new FerruleInspectionBackend(wrong);
                Assert.Equal(profile == DockerWorkerProfile.Authoring, AuthoringFrameAdmission.Allows(profile, authoringHeader));
                Assert.Equal(profile == DockerWorkerProfile.AuthoredPreview, AuthoredPreviewFrameAdmission.Allows(profile, previewHeader));
                if (profile != DockerWorkerProfile.Authoring)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => backend.AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes()));
                if (profile != DockerWorkerProfile.AuthoredPreview)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PreviewAuthoredAsync(subject, native, sample));
                AssertSettled(root);
            }
            var authorBackend = new FerruleInspectionBackend(author); var previewBackend = new FerruleInspectionBackend(preview);
            var invalidPlan = await authorBackend.AuthorAsync(project, authoring, "{}"u8.ToArray());
            Assert.Null(invalidPlan.Checkpoint); Assert.Equal(IngestionBackendStatus.InvalidMapping, invalidPlan.Failure!.Status);
            var oversized = await authorBackend.AuthorAsync(project, authoring, new byte[MappingAuthoringPolicy.MaximumPlanBytes + 1]);
            Assert.Null(oversized.Checkpoint); Assert.Equal(IngestionBackendStatus.ResourceLimit, oversized.Failure!.Status);
            var malformed = "not JSON"u8.ToArray();
            var invalidSample = await previewBackend.PreviewAuthoredAsync(subject with { SampleDigest = Sha256Digest.FromBytes(malformed) }, native, malformed);
            Assert.Null(invalidSample.Evidence); Assert.Equal("invalidSample", invalidSample.Failure!.Code);
            var mismatch = await previewBackend.PreviewAuthoredAsync(subject, native, malformed);
            Assert.Null(mismatch.Evidence); Assert.Equal("digestMismatch", mismatch.Failure!.Code);
            var hugeSample = await previewBackend.PreviewAuthoredAsync(subject, native, new byte[MappingAuthoredPreviewPolicy.MaximumSampleBytes + 1]);
            Assert.Null(hugeSample.Evidence); Assert.Equal(MappingOperationStatus.ResourceLimit, hugeSample.Failure!.Status);
            AssertSettled(root);
        }
        finally { RemoveSettledRoot(root); }
    }

    [ServiceWorkerFact]
    public async Task Cancellation_and_deadline_join_live_children_for_both_creation_profiles()
    {
        var root = TemporaryRoot();
        try
        {
            var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid()); var authoring = Guid.NewGuid(); var plan = Simple();
            await using var author = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Authoring));
            var compiled = await new FerruleInspectionBackend(author).AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes());
            Assert.Null(compiled.Failure); var checkpoint = Assert.IsType<MappingAuthoringCheckpoint>(compiled.Checkpoint);
            var native = checkpoint.CopyNativeBytes(); var sample = "{\"code\":\"alpha\",\"other\":\"beta\"}"u8.ToArray();
            var subject = new MappingAuthoredPreviewSubject(project, authoring, plan.Digest, checkpoint.NativeDigest, Sha256Digest.FromBytes(sample));
            foreach (var profile in new[] { DockerWorkerProfile.Authoring, DockerWorkerProfile.AuthoredPreview })
            foreach (var cancel in new[] { true, false })
            {
                // Only EOF is withheld: the real worker applies and proves all normal sandbox controls.
                await using var held = new ProcessWorkerRunner(Options(root, profile, TimeSpan.FromSeconds(cancel ? 10 : 3), withhold: true));
                using var cancellation = new CancellationTokenSource();
                var backend = new FerruleInspectionBackend(held);
                async Task CheckInterruption()
                {
                    if (profile == DockerWorkerProfile.Authoring)
                    {
                        var result = await backend.AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes(), cancellation.Token);
                        Assert.Null(result.Checkpoint);
                        Assert.Equal(cancel ? IngestionBackendStatus.Canceled : IngestionBackendStatus.TimedOut, result.Failure!.Status);
                    }
                    else
                    {
                        var result = await backend.PreviewAuthoredAsync(subject, native, sample, cancellation.Token);
                        Assert.Null(result.Evidence);
                        Assert.Equal(cancel ? MappingOperationStatus.Canceled : MappingOperationStatus.TimedOut, result.Failure!.Status);
                    }
                }
                var running = CheckInterruption();
                try
                {
                    var child = Assert.Single(await WaitForWorkerChildren(1, sandboxReady: true));
                    if (cancel) await cancellation.CancelAsync();
                    await running.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.False(Directory.Exists($"/proc/{child}")); Assert.True(held.Available); AssertSettled(root);
                }
                finally { await held.StopAsync(); await running; }
                await using var next = new ProcessWorkerRunner(Options(root, profile));
                var nextBackend = new FerruleInspectionBackend(next);
                if (profile == DockerWorkerProfile.Authoring)
                    Assert.Null((await nextBackend.AuthorAsync(project, authoring, plan.CopyCompilerPlanBytes())).Failure);
                else
                    Assert.NotNull((await nextBackend.PreviewAuthoredAsync(subject, native, sample)).Evidence);
                AssertSettled(root);
            }
        }
        finally { RemoveSettledRoot(root); }
    }

    private static void AssertSettled(string root)
    {
        Assert.Empty(WorkerChildren()); Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    private static void RemoveSettledRoot(string root)
    {
        AssertSettled(root);
        Directory.Delete(root); // Never hide leftover worker evidence with recursive cleanup.
    }

    private static byte[] HeaderFrame(string protocol)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = protocol });
        var frame = new byte[4 + header.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)header.Length); header.CopyTo(frame, 4);
        return frame;
    }

    // Small fixture/scope/output helpers mirror MappingCreationFormatsTests without widening its private API.
    private static MappingCreationPlan Simple() => new(new CreationPlanV2(Fixture("json").Boundary, Fixture("json").Boundary,
        new(null, [new("code", new CreationSourceValue(["code"])), new("other", new CreationSourceValue(["other"]))])));
    private static CreationSchemaNode Scalar(string name, CreationXmlMetadata? xml = null) => new(name, CreationValueType.String, xml: xml);
    private static CreationSchemaNode Group(string name, IEnumerable<CreationSchemaNode> children, CreationXmlMetadata? xml = null) => new(name, null, children, xml: xml);
    private sealed record FormatFixture(CreationBoundary Boundary, string Sample, string[][] Paths, Dictionary<string, string> Constants);
    private static FormatFixture Fixture(string format)
    {
        if (format is not ("x12" or "edifact"))
        {
            CreationFormatOptions options = format switch
            {
                "json" => new CreationJsonOptions(), "xml" => new CreationXmlOptions(),
                "csv" => new CreationCsvOptions(true, true, false, true), _ => new CreationTsvOptions(true, true, false, true)
            };
            var schema = Group("Document", [Scalar("code", format == "xml" ? new("", Attribute: true) : null), Scalar("other")],
                format == "xml" ? new("urn:creation") : null);
            var sample = format switch
            {
                "json" => "{\"code\":\"alpha\",\"other\":\"beta\"}",
                "xml" => "<Document xmlns=\"urn:creation\" code=\"alpha\"><other>beta</other></Document>",
                "csv" => "code,other\nalpha,beta\n", _ => "code\tother\nalpha\tbeta\n"
            };
            return new(new(schema, options), sample, [["code"], ["other"]], []);
        }
        var wire = format == "x12" ? X12 : Edifact; var separator = format == "x12" ? '*' : '+'; var terminator = format == "x12" ? '~' : '\'';
        var constants = new Dictionary<string, string>(); var groups = new List<CreationSchemaNode>();
        foreach (var segment in wire.TrimEnd(terminator).Split(terminator))
        {
            var parts = segment.Split(separator); var elements = new List<CreationSchemaNode>();
            for (var i = 1; i < parts.Length; i++)
            {
                var name = "element_" + i;
                if (format == "edifact" && parts[i].Contains(':'))
                {
                    var components = parts[i].Split(':');
                    elements.Add(Group(name, components.Select((_, index) => Scalar("part_" + (index + 1)))));
                    for (var c = 0; c < components.Length; c++) constants[$"{parts[0]}/{name}/part_{c + 1}"] = components[c];
                }
                else { elements.Add(Scalar(name)); constants[$"{parts[0]}/{name}"] = parts[i]; }
            }
            groups.Add(Group(parts[0], elements));
        }
        return new(new(Group("Envelope", groups), format == "x12" ? new CreationX12Options("00401") : new CreationEdifactOptions()), wire,
            format == "x12" ? [["BEG", "element_3"], ["BEG", "element_4"]] : [["BGM", "element_2"], ["BGM", "element_3"]], constants);
    }

    private static CreationScope TargetScope(CreationSchemaNode node, Dictionary<string, string> constants, string[][] sources, string[][] targets, string[] path)
    {
        var bindings = new List<CreationFieldBinding>(); var children = new List<CreationScope>();
        foreach (var child in node.Children)
        {
            string[] location = [.. path, child.Name];
            if (child.ScalarType is null) children.Add(TargetScope(child, constants, sources, targets, location));
            else
            {
                var index = Array.FindIndex(targets, target => target.SequenceEqual(location));
                bindings.Add(new(child.Name, index >= 0 ? new CreationSourceValue(sources[index]) : new CreationConstantValue(CreationValueType.String, constants[string.Join('/', location)])));
            }
        }
        return new(path.Length == 0 ? null : node.Name, bindings, children);
    }

    private static void AssertOutput(string format, string output, string first, string second)
    {
        switch (format)
        {
            case "json":
                using (var json = JsonDocument.Parse(output))
                { Assert.Equal(first, json.RootElement.GetProperty("code").GetString()); Assert.Equal(second, json.RootElement.GetProperty("other").GetString()); }
                break;
            case "xml":
                var root = XDocument.Parse(output).Root!; Assert.Equal("urn:creation", root.Name.NamespaceName);
                Assert.Equal(first, root.Attribute("code")!.Value); Assert.Equal(second, root.Element(XName.Get("other", "urn:creation"))!.Value); break;
            case "csv": Assert.Equal($"code,other\n{first},{second}\n", output); break;
            case "tsv": Assert.Equal($"code\tother\n{first}\t{second}\n", output); break;
            case "x12": Assert.Equal(X12.Replace("alpha", first).Replace("beta", second), output.Replace("\r", "").Replace("\n", "")); break;
            case "edifact": Assert.Equal(Edifact.Replace("alpha", first).Replace("beta", second), output.Replace("\r", "").Replace("\n", "")); break;
        }
    }
}
