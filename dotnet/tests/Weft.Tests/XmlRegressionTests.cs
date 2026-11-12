using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

/// <summary>XML regression is exact bytes with complete role-correct metadata; no canonical comparison is involved.</summary>
public sealed class XmlRegressionTests
{
    private static readonly byte[] Input = XmlPreviewWorkflowTests.Input;
    private static readonly XmlDocumentMetadata Metadata = XmlDocumentMetadata.WithoutHints;
    private static readonly XmlDocumentMetadata Hinted = new(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile,
        new(null, [new("urn:orders", "orders.xsd")]));
    private static readonly RegressionRuntimeIdentity Runtime = new("ferrule", FerruleInspectionBackend.EngineRevision,
        "sha256:" + new string('a', 64), FerrulePreviewBackend.XmlAdapterBuild, XmlPreviewAdmission.Profile);

    private static byte[] TwoOutputNative(JsonNode? copyOptions = null)
    {
        var native = JsonNode.Parse(XmlPreviewWorkflowTests.Native)!;
        native["extra_sources"] = new JsonArray(new JsonObject
        {
            ["name"] = "lookup",
            ["schema"] = native["source"]!.DeepClone(),
            ["options"] = new JsonObject { ["xml_document"] = true }
        });
        native["extra_targets"] = new JsonArray(new JsonObject
        {
            ["name"] = "copy",
            ["schema"] = native["target"]!.DeepClone(),
            ["root"] = native["root"]!.DeepClone(),
            ["options"] = copyOptions ?? new JsonObject
            {
                ["xml_document"] = true,
                ["xml_schema_hints"] = new JsonObject
                { ["no_namespace_location"] = null, ["locations"] = new JsonArray(new JsonObject { ["namespace"] = "urn:orders", ["location"] = "orders.xsd" }) }
            }
        });
        return Encoding.UTF8.GetBytes(native.ToJsonString());
    }

    private static RegressionReplay Replay(PreviewTargetSelection? target = null) => new(target ?? PreviewTargetSelection.All, "2026-11-07T12:00:00Z",
        PreviewLimits.Default, new PreviewInputDocument(null, Input, xmlMetadata: Metadata),
        [new PreviewInputDocument("lookup", "<Order><code>B</code><quantity>1</quantity></Order>"u8, xmlMetadata: Metadata)], []);

    private static RegressionOutputSet Outputs(byte[]? primary = null, XmlDocumentMetadata? copy = null) => RegressionOutputSet.Capture([
        RegressionOutput.Capture(RegressionOutputKey.Primary, primary ?? Input, mediaType: XmlDocumentPolicy.MediaType, xmlMetadata: Metadata),
        RegressionOutput.Capture(RegressionOutputKey.Named("copy"), Input, mediaType: XmlDocumentPolicy.MediaType, xmlMetadata: copy ?? Hinted)]);

    private static RegressionCaseVersion Case(RevisionScope scope, byte[] native, RegressionOutputSet? expected = null, RegressionReplay? replay = null) =>
        new(Guid.NewGuid(), 1, new PrincipalId(Guid.NewGuid()), "XML case", scope, Sha256Digest.FromBytes(native), Runtime,
            replay ?? Replay(), RegressionComparisonService.ExactBytesPolicy, expected ?? Outputs());

    [Fact]
    public void Envelopes_roundtrip_exact_bytes_and_metadata_under_their_own_schema()
    {
        var native = TwoOutputNative();
        var value = Case(XmlPreviewWorkflowTests.Scope, native);
        var codec = new RegressionCaseEnvelopeCodec();
        var envelope = codec.Encode(value);
        var bytes = envelope.CopyBytes();
        Assert.Contains(RegressionCaseEnvelopeCodec.XmlSchema, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var decoded = codec.Decode(envelope.Reference, bytes);
        Assert.Equal(PreviewExecutionProfile.XmlDocument, decoded.Replay.Profile);
        Assert.All(decoded.Replay.NamedInputs.Prepend(decoded.Replay.Primary), input => Assert.Equal(Metadata, input.XmlMetadata));
        Assert.Equal(Hinted, decoded.Expected.Outputs.Single(output => output.Key.Name == "copy").XmlMetadata);
        Assert.Equal(Input, decoded.Expected.Outputs[0].CopyBytes());
        Assert.Equal(bytes, codec.Encode(decoded).CopyBytes());

        // A re-digested envelope with an unknown metadata member is still refused.
        var text = Encoding.Latin1.GetString(bytes);
        var altered = text.Replace("\"schemaHints\":null", "\"schemaHints\":null,\"extra\":1", StringComparison.Ordinal);
        var tampered = Encoding.Latin1.GetBytes(altered);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(tampered,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes) + altered.Length - text.Length);
        var reference = new RegressionCaseReference(value.CaseId, 1, value.Owner, value.Revision, value.NativeDigest,
            Sha256Digest.FromBytes(tampered), tampered.Length);
        Assert.Equal(RegressionCaseFailure.StorageIntegrity, Assert.Throws<RegressionCaseException>(() => codec.Decode(reference, tampered)).Failure);
    }

    [Fact]
    public void Versions_require_exact_bytes_and_metadata_on_every_boundary()
    {
        var scope = XmlPreviewWorkflowTests.Scope;
        var native = TwoOutputNative();
        var value = Case(scope, native);
        Assert.Throws<RegressionCaseException>(() => new RegressionCaseVersion(value.CaseId, 1, value.Owner, value.Name, scope, value.NativeDigest,
            Runtime, value.Replay, RegressionComparisonService.CsvRowsPolicy, value.Expected));
        Assert.Throws<RegressionCaseException>(() => new RegressionCaseVersion(value.CaseId, 1, value.Owner, value.Name, scope, value.NativeDigest,
            Runtime, value.Replay, RegressionComparisonService.EdiSegmentsPolicy, value.Expected, new(true, false, false)));
        var json = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{}"u8, mediaType: "application/json")]);
        Assert.Throws<RegressionCaseException>(() => Case(scope, native, json));
        // XML metadata cannot ride on another profile, and XML inputs cannot omit it.
        Assert.Throws<RegressionCaseException>(() => new RegressionCaseVersion(value.CaseId, 1, value.Owner, value.Name, scope, value.NativeDigest,
            new RegressionRuntimeIdentity("ferrule", "pin", "worker", "adapter", DocumentPreviewPolicy.Profile), value.Replay,
            RegressionComparisonService.ExactBytesPolicy, value.Expected));
        Assert.Throws<RegressionCaseException>(() => new RegressionReplay(PreviewTargetSelection.All, "2026-11-07T12:00:00Z", PreviewLimits.Default,
            new PreviewInputDocument(null, Input, xmlMetadata: Metadata), [new PreviewInputDocument("lookup", Input)], []));
        Assert.Throws<RegressionCaseException>(() => new RegressionReplay(PreviewTargetSelection.All, "2026-11-07T12:00:00Z", PreviewLimits.Default,
            new PreviewInputDocument(null, Input, xmlMetadata: Metadata), [], [], profile: PreviewExecutionProfile.LegacyJson));
        Assert.Throws<RegressionCaseException>(() => new RegressionReplay(PreviewTargetSelection.All, "2026-11-07T12:00:00Z", PreviewLimits.Default,
            new PreviewInputDocument(null, [], xmlMetadata: Metadata), [], []));
        // Input-role metadata never carries hints; expected outputs must pass the safe physical XML check.
        Assert.Throws<ArgumentException>(() => new PreviewInputDocument(null, Input, xmlMetadata: Hinted));
        Assert.Throws<RegressionComparisonException>(() => RegressionOutput.Capture(RegressionOutputKey.Primary, "<!DOCTYPE Order><Order/>"u8,
            mediaType: XmlDocumentPolicy.MediaType, xmlMetadata: Metadata));
        Assert.Throws<RegressionComparisonException>(() => RegressionOutput.Capture(RegressionOutputKey.Primary, Input, mediaType: XmlDocumentPolicy.MediaType));
        Assert.Throws<RegressionComparisonException>(() => RegressionOutput.Capture(RegressionOutputKey.Primary, Input, mediaType: "application/json", xmlMetadata: Metadata));
    }

    [Fact]
    public void Replay_requests_bind_only_to_the_revision_with_identical_declared_metadata()
    {
        var scope = XmlPreviewWorkflowTests.Scope;
        var native = TwoOutputNative();
        var artifact = NativeArtifact.Admit(scope, native, PreviewPolicy.MaximumNativeBytes);
        var request = Case(scope, native).CreatePreviewRequest(artifact, Runtime);
        Assert.Equal(PreviewExecutionProfile.XmlDocument, request.Profile);
        Assert.True(XmlPreviewAdmission.MatchesCase(artifact, Replay(), Outputs()));

        // Wrong hints, missing all-target output, or a revision whose output no longer carries the saved hints.
        Assert.False(XmlPreviewAdmission.MatchesCase(artifact, Replay(), Outputs(copy: Metadata)));
        var primaryOnly = RegressionOutputSet.Capture([Outputs().Outputs[0]]);
        Assert.False(XmlPreviewAdmission.MatchesCase(artifact, Replay(), primaryOnly));
        Assert.True(XmlPreviewAdmission.MatchesCase(artifact, Replay(PreviewTargetSelection.Primary), primaryOnly));
        var changed = TwoOutputNative(new JsonObject { ["xml_document"] = true });
        var changedArtifact = NativeArtifact.Admit(scope, changed, PreviewPolicy.MaximumNativeBytes);
        var stale = Case(scope, changed, Outputs());
        Assert.Equal(RegressionCaseFailure.InvalidInput,
            Assert.Throws<RegressionCaseException>(() => stale.CreatePreviewRequest(changedArtifact, Runtime)).Failure);
        // A non-XML revision is never adopted as an XML replay target.
        var json = NativeArtifact.Admit(scope, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(XmlPreviewWorkflowTests.Native)
            .Replace("{\"xml_document\":true}", "{}", StringComparison.Ordinal)), PreviewPolicy.MaximumNativeBytes);
        Assert.Throws<RegressionCaseException>(() => Case(scope, json.CopyBytes(), RegressionOutputSet.Capture([Outputs().Outputs[0]]),
            Replay(PreviewTargetSelection.Primary)).CreatePreviewRequest(json, Runtime));
    }

    [Fact]
    public void Results_compare_original_bytes_and_report_metadata_before_bytes()
    {
        var native = TwoOutputNative();
        var value = Case(XmlPreviewWorkflowTests.Scope, native);
        var codec = new RegressionRunResultCodec();
        var run = Guid.NewGuid();
        var passed = codec.EncodeObservation(run, value, MappingPreviewStatus.Succeeded, value.Expected);
        Assert.Contains(RegressionRunResultCodec.XmlSchema, Encoding.UTF8.GetString(passed), StringComparison.Ordinal);
        var decoded = codec.Decode(run, value, passed);
        Assert.Equal(RegressionComparisonOutcome.Passed, decoded.Comparison!.Outcome);
        Assert.Equal(Hinted, decoded.Outputs!.Outputs.Single(output => output.Key.Name == "copy").XmlMetadata);

        // Equivalent XML with different bytes is a byte difference: there is no canonical comparison.
        var reordered = "<Order><code>A</code><quantity>2</quantity></Order>\n"u8.ToArray();
        var bytes = codec.EncodeObservation(run, value, MappingPreviewStatus.Succeeded, Outputs(reordered));
        var difference = Assert.Single(codec.Decode(run, value, bytes).Comparison!.Differences);
        Assert.Equal(RegressionDifferenceKind.BytesDiffer, difference.Kind);
        Assert.Equal(Input.Length, difference.FirstDifferentByte);

        var comparison = new RegressionComparisonService().Compare(value.Expected,
            new(OperationStatusV1.Succeeded, Outputs(reordered, Metadata)));
        Assert.Equal([RegressionDifferenceKind.BytesDiffer, RegressionDifferenceKind.XmlMetadataDiffer], comparison.Differences.Select(item => item.Kind));

        // Tampered stored results never reopen.
        var text = Encoding.UTF8.GetString(passed);
        foreach (var altered in new[]
        {
            text.Replace(RegressionRunResultCodec.XmlSchema, RegressionRunResultCodec.CsvSchema, StringComparison.Ordinal),
            text.Replace("\"schemaHints\":null", "\"schemaHints\":null,\"extra\":1", StringComparison.Ordinal),
            text.Replace(",\"xmlMetadata\":", ",\"ignored\":", StringComparison.Ordinal),
        })
            Assert.Throws<InvalidOperationException>(() => codec.Decode(run, value, Encoding.UTF8.GetBytes(altered)));
    }
}
