using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

/// <summary>Authored transcripts test managed trust checks, not execution or canonicalizer qualification.</summary>
public sealed class XmlPreviewWorkflowTests
{
    internal static readonly byte[] Input = "<Order><code>A</code><quantity>2</quantity></Order>"u8.ToArray();
    internal static byte[] Native => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "xml-closed.native.json"));
    internal static readonly RevisionScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    internal static MappingPreviewRequest Request(byte[]? native = null) => new(NativeArtifact.Admit(Scope, native ?? Native, PreviewPolicy.MaximumNativeBytes),
        PreviewTargetSelection.All, new(null, Input, xmlMetadata: XmlDocumentMetadata.WithoutHints), [], [], "2026-11-02T12:00:00Z", PreviewLimits.Default)
    { Profile = PreviewExecutionProfile.XmlDocument };

    [Fact]
    public async Task Default_off_and_per_request_dispatch_cannot_bypass_trusted_admission()
    {
        var runner = new Transcript();
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(Request());
        Assert.Equal(MappingPreviewStatus.UnsupportedCapability, result.Status);
        Assert.Equal("preview.xmlDisabled", result.Failure!.Code);
        Assert.Equal(0, runner.Calls);
        Assert.False(new CatalogPreviewOptions().XmlEnabled);
        Assert.False(new PreviewRunOptions().XmlEnabled);
        Assert.Throws<ArgumentException>(() => new CatalogPreviewOptions(xmlEnabled: true));
        Assert.Throws<RegressionCaseException>(() => new RegressionReplay(PreviewTargetSelection.All, "2026-11-02T12:00:00Z", PreviewLimits.Default,
            Request().Primary, [], [], profile: PreviewExecutionProfile.LegacyJson));
    }

    [Fact]
    public async Task Disabled_service_never_queues_and_disabled_executor_never_reads_input_or_native_bytes()
    {
        var store = DispatchProxy.Create<IPreviewRunStore, RefuseIo>();
        var state = (RefuseIo)(object)store;
        var submission = new PreviewRunSubmission(Scope, Guid.NewGuid(), PreviewTargetSelection.All, Request().Primary, [], []) { Profile = PreviewExecutionProfile.XmlDocument };
        var reader = DispatchProxy.Create<IPreviewRunReader, RefuseIo>();
        await Assert.ThrowsAsync<PreviewRunException>(() => new PreviewRunService(store, reader, TimeProvider.System).SubmitAsync(new(new PrincipalId(Guid.NewGuid())), submission));
        Assert.Empty(state.Calls);
        state.Claim = new(Scope.OrganizationId, Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), Scope, new PrincipalId(Guid.NewGuid()),
            PreviewRequestDocument.Encode(submission, PreviewLimits.Default), "2026-11-02T12:00:00Z",
            new StoredPreviewNative(new(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(Native), Native.Length)));
        var executor = new PreviewRunExecutor(store, DispatchProxy.Create<IPrivateObjectStore, RefuseIo>(), DispatchProxy.Create<IMappingPreviewBackend, RefuseIo>(), TimeProvider.System);
        Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(Scope.OrganizationId, state.Claim.RunId, Guid.NewGuid()));
        Assert.Equal(new[] { "ClaimAsync", "CompleteAsync" }, state.Calls);
        Assert.Equal(PreviewRunOutcome.ProtocolFailure, state.Completion!.Outcome);
        Assert.Empty(state.Completion.Artifacts);
    }

    public class RefuseIo : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public PreviewRunClaim? Claim { get; set; }
        public PreviewRunCompletion? Completion { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Calls.Add(method!.Name);
            if (method.Name == "ClaimAsync" && Claim is not null) return Task.FromResult(new PreviewClaimResult(PreviewClaimOutcome.Claimed, Claim));
            if (method.Name == "CompleteAsync") { Completion = (PreviewRunCompletion)arguments![1]!; return Task.FromResult(true); }
            throw new InvalidOperationException("Disabled XML must not access this port.");
        }
    }

    [Fact]
    public async Task Versioned_xml_protocol_keeps_original_frames_and_authoritative_output_metadata()
    {
        var runner = new Transcript();
        var request = Request();
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.LegacyJson, xmlEnabled: true).PreviewAsync(request);
        Assert.Equal(MappingPreviewStatus.Succeeded, result.Status);
        Assert.Equal(XmlPreviewAdmission.Profile, result.Profile);
        Assert.Equal(FerrulePreviewBackend.XmlAdapterBuild, result.Provenance.AdapterBuild);
        Assert.Equal(Input, Assert.Single(result.Artifacts).CopyBytes());
        Assert.Equal(XmlDocumentMetadata.WithoutHints, result.Artifacts[0].XmlMetadata);
        var frames = Frames(runner.Request!);
        Assert.Equal(request.Artifact.CopyBytes(), frames[1]);
        Assert.Equal(Input, frames[2]);
        using var header = JsonDocument.Parse(frames[0]);
        Assert.Equal(XmlPreviewAdmission.Profile, header.RootElement.GetProperty("protocolVersion").GetString());
        Assert.False(header.RootElement.GetProperty("inputs")[0].TryGetProperty("xmlMetadata", out _));
    }

    [Theory]
    [InlineData("<!DOCTYPE Order [<!ENTITY x SYSTEM 'file:///private-sentinel'>]><Order>&x;</Order>")]
    [InlineData("<Order>")]
    [InlineData("")]
    [InlineData("<?xml version='1.1'?><Order/>")]
    public async Task Physical_input_refusals_do_not_dispatch(string input)
    {
        var runner = new Transcript();
        var request = Request() with { Primary = new(null, Encoding.UTF8.GetBytes(input), xmlMetadata: XmlDocumentMetadata.WithoutHints) };
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.XmlDocument, xmlEnabled: true).PreviewAsync(request);
        Assert.NotEqual(MappingPreviewStatus.Succeeded, result.Status);
        Assert.Equal(0, runner.Calls);
        Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("unknownMetadata")]
    [InlineData("missingHints")]
    [InlineData("nullMetadata")]
    [InlineData("boundary")]
    [InlineData("target")]
    [InlineData("csv")]
    [InlineData("profile")]
    [InlineData("digest")]
    [InlineData("unsafeOutput")]
    [InlineData("trailing")]
    public async Task Forged_outputs_never_publish_a_partial_set(string forge)
    {
        var runner = new Transcript(forge);
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.XmlDocument, xmlEnabled: true).PreviewAsync(Request());
        Assert.Equal(MappingPreviewStatus.ProtocolFailure, result.Status);
        Assert.Empty(result.Artifacts);
        Assert.False(result.Provenance.Confirmed);
    }

    [Fact]
    public async Task Unselected_native_metadata_and_repeated_hints_are_charged_before_dispatch()
    {
        var native = JsonNode.Parse(Native)!;
        native["extra_targets"] = new JsonArray(Enumerable.Range(0, 8).Select(index => (JsonNode)new JsonObject
        {
            ["name"] = "copy" + index,
            ["schema"] = native["target"]!.DeepClone(),
            ["root"] = native["root"]!.DeepClone(),
            ["options"] = new JsonObject { ["xml_document"] = true, ["xml_schema_hints"] = new JsonObject { ["no_namespace_location"] = new string('x', 4096), ["locations"] = new JsonArray() } }
        }).ToArray());
        var runner = new Transcript();
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.XmlDocument, xmlEnabled: true).PreviewAsync(Request(Encoding.UTF8.GetBytes(native.ToJsonString())) with { Target = PreviewTargetSelection.Primary });
        Assert.Equal(MappingPreviewStatus.ResourceLimit, result.Status);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Canonical_requests_roundtrip_metadata_and_refuse_tampering_without_legacy_fallback()
    {
        var request = Request();
        var submission = new PreviewRunSubmission(Scope, Guid.NewGuid(), request.Target, request.Primary, [], []) { Profile = PreviewExecutionProfile.XmlDocument };
        var encoded = PreviewRequestDocument.Encode(submission, PreviewLimits.Default);
        var description = PreviewRequestDocument.Decode(encoded);
        Assert.Equal(PreviewExecutionProfile.XmlDocument, description.Profile);
        Assert.Equal(XmlDocumentMetadata.WithoutHints, description.Inputs[0].XmlMetadata);
        foreach (var altered in new[]
        {
            encoded.Replace("\"schemaHints\":null", "\"schemaHints\":null,\"unknown\":true", StringComparison.Ordinal),
            encoded.Replace("\"schemaHints\":null", "\"schemaHints\":null,\"schemaHints\":null", StringComparison.Ordinal),
            encoded.Replace("\"schemaHints\":null", "\"schemaHints\":{}", StringComparison.Ordinal),
            encoded.Replace("\"maximumArtifacts\":64", "\"maximumArtifacts\":65", StringComparison.Ordinal),
            encoded.Replace("\"name\":null", "\"name\":null,\"present\":true", StringComparison.Ordinal),
            encoded.Replace("\"byteLength\":", "\"unknown\":1,\"byteLength\":", StringComparison.Ordinal),
            encoded + " "
        })
        {
            Assert.Throws<PreviewRunException>(() => PreviewRequestDocument.Decode(altered));
            Assert.NotEqual(PreviewRequestDocument.Fingerprint(encoded), PreviewRequestDocument.Fingerprint(altered));
        }
        Assert.Throws<PreviewRunException>(() => PreviewRequestDocument.Encode(submission with { Profile = PreviewExecutionProfile.Document }, PreviewLimits.Default));
    }

    [Fact]
    public void Metadata_is_an_immutable_format_discriminant()
    {
        var input = new PreviewInputDocument(null, Input, xmlMetadata: XmlDocumentMetadata.WithoutHints);
        var copied = input.CopyBytes(); copied[0] = 0;
        Assert.Equal(Input, input.CopyBytes());
        Assert.Throws<ArgumentException>(() => new PreviewInputDocument(null, Input, csvDialect: CsvPreviewTestData.Dialect, xmlMetadata: XmlDocumentMetadata.WithoutHints));
        Assert.Throws<ArgumentException>(() => new PreviewOutputArtifact("primary-output", "Order", false, "application/json", 1, Input, xmlMetadata: XmlDocumentMetadata.WithoutHints));
        Assert.Throws<ArgumentException>(() => new PreviewOutputArtifact("primary-output", "Order", false, "application/xml", 1, Input));
    }

    [Fact]
    public void All_and_named_selection_require_the_complete_saved_boundary_order_and_metadata()
    {
        var native = JsonNode.Parse(Native)!;
        native["extra_targets"] = new JsonArray(new JsonObject
        {
            ["name"] = "copy",
            ["schema"] = native["target"]!.DeepClone(),
            ["root"] = native["root"]!.DeepClone(),
            ["options"] = new JsonObject { ["xml_document"] = true }
        });
        var request = Request(Encoding.UTF8.GetBytes(native.ToJsonString()));
        var primary = new PreviewOutputArtifact("primary-output", "Order", false, "application/xml", 1, Input, xmlMetadata: XmlDocumentMetadata.WithoutHints);
        var named = new PreviewOutputArtifact("named-output:0", "copy", false, "application/xml", 1, Input, xmlMetadata: XmlDocumentMetadata.WithoutHints);
        XmlPreviewAdmission.VerifyOutputs(request, [primary, named]);
        Assert.Throws<XmlDocumentException>(() => XmlPreviewAdmission.VerifyOutputs(request with { Limits = PreviewLimits.Default with { MaximumOutputBytes = Input.Length } }, [primary, named]));
        Assert.Throws<XmlDocumentException>(() => XmlPreviewAdmission.VerifyOutputs(request with { Limits = PreviewLimits.Default with { MaximumArtifacts = 1 } }, [primary, named]));
        Assert.Throws<XmlDocumentException>(() => XmlPreviewAdmission.VerifyOutputs(request, [primary]));
        Assert.Throws<XmlDocumentException>(() => XmlPreviewAdmission.VerifyOutputs(request, [named, primary]));
        XmlPreviewAdmission.VerifyOutputs(request with { Target = PreviewTargetSelection.Named("copy") }, [named]);
        Assert.Throws<XmlDocumentException>(() => XmlPreviewAdmission.VerifyOutputs(request with { Target = PreviewTargetSelection.Named("copy") }, [primary]));
    }

    private sealed class Transcript(string? forge = null) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('a', 64);
        public int Calls { get; private set; }
        public byte[]? Request { get; private set; }
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            Calls++; Request = input.ToArray();
            var request = JsonNode.Parse(Frames(Request)[0])!;
            var bytes = forge == "unsafeOutput" ? "<!DOCTYPE Order><Order/>"u8.ToArray() : Input;
            var item = new JsonObject
            {
                ["boundaryId"] = "primary-output",
                ["targetName"] = "Order",
                ["targetNameTruncated"] = false,
                ["mediaType"] = "application/xml",
                ["recordsWritten"] = 1,
                ["byteLength"] = bytes.Length,
                ["sha256"] = Sha256Digest.FromBytes(bytes).Value,
                ["xmlMetadata"] = JsonNode.Parse(XmlDocumentMetadataJson.Encode(XmlDocumentMetadata.WithoutHints, XmlDocumentRole.Output))
            };
            switch (forge)
            {
                case "metadata": item["xmlMetadata"]!["safetyProfile"] = "unknown"; break;
                case "unknownMetadata": item["xmlMetadata"]!["unknown"] = true; break;
                case "missingHints": item["xmlMetadata"]!.AsObject().Remove("schemaHints"); break;
                case "nullMetadata": item["xmlMetadata"] = null; break;
                case "boundary": item["boundaryId"] = "named-output:0"; break;
                case "target": item["targetName"] = "forged"; break;
                case "csv": item["csvDialect"] = new JsonObject(); break;
                case "digest": item["sha256"] = new string('b', 64); break;
            }
            var response = new JsonObject
            {
                ["protocolVersion"] = XmlPreviewAdmission.Profile,
                ["requestId"] = request["requestId"]!.DeepClone(),
                ["revision"] = request["revision"]!.DeepClone(),
                ["nativeDigest"] = request["nativeDigest"]!.DeepClone(),
                ["documentId"] = "entry.native.json",
                ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
                ["profile"] = forge == "profile" ? PreviewPolicy.Profile : XmlPreviewAdmission.Profile,
                ["result"] = new JsonObject { ["recordsWritten"] = 1, ["artifacts"] = new JsonArray(item) },
                ["failure"] = null
            };
            using var output = new MemoryStream();
            foreach (var frame in new[] { Encoding.UTF8.GetBytes(response.ToJsonString()), bytes })
            {
                var length = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)frame.Length); output.Write(length); output.Write(frame);
            }
            if (forge == "trailing") output.WriteByte(1);
            return Task.FromResult(new WorkerProcessResult(output.ToArray(), WorkerProcessFailure.None));
        }
    }

    private static List<byte[]> Frames(byte[] input)
    {
        var result = new List<byte[]>(); var offset = 0;
        while (offset < input.Length) { var size = (int)BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset)); result.Add(input.AsSpan(offset + 4, size).ToArray()); offset += size + 4; }
        return result;
    }
}
