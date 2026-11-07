using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class CompiledRuntimeProtocolTests
{
    [Theory]
    [InlineData("json", "application/json")]
    [InlineData("x12", "application/edi-x12")]
    [InlineData("edifact", "application/edifact")]
    public void Document_protocol_binds_declared_input_and_output_media_without_relabelling(string format, string media)
    {
        var value = Fixture(format);
        var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        Assert.Equal(CompiledRuntimeProtocol.DocumentProtocol, request.ProtocolVersion);
        Assert.Equal(media, Assert.Single(request.Replay.Inputs).MediaType);
        var invocation = CompiledRuntimeProtocol.DecodeRequest(CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, value.Replay));
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, value.Replay.Primary.CopyBytes(), mediaType: media)]);
        var encoded = CompiledRuntimeProtocol.EncodeResponse(invocation, "succeeded", outputs);
        var restored = CompiledRuntimeProtocol.DecodeResponse(encoded, request, value.Intent);
        Assert.Equal(media, Assert.Single(restored.Outputs!.Outputs).MediaType);
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.EncodeResponse(invocation, "succeeded",
            RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, value.Replay.Primary.CopyBytes(),
                mediaType: media == "application/json" ? "application/edi-x12" : "application/json")])));
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.EncodeRequest(request with { ProtocolVersion = CompiledRuntimeProtocol.Protocol },
            value.Intent, value.Artifact, value.Replay));
    }

    [Fact]
    public void Legacy_protocol_omits_media_extensions_and_keeps_its_protocol_identity()
    {
        var value = Fixture(); var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        Assert.Equal(CompiledRuntimeProtocol.Protocol, request.ProtocolVersion);
        var bytes = CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, value.Replay);
        Assert.DoesNotContain("mediaType", Encoding.UTF8.GetString(bytes, 4, BinaryPrimitives.ReadInt32BigEndian(bytes)));
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes);
        var old = Encoding.UTF8.GetString(bytes, 4, count);
        var raw = Encoding.UTF8.GetBytes(old.Replace("\"inputs\":[{", "\"inputs\":[{\"mediaType\":null,", StringComparison.Ordinal));
        var changed = new byte[4 + raw.Length + bytes.Length - 4 - count];
        BinaryPrimitives.WriteInt32BigEndian(changed, raw.Length);
        raw.CopyTo(changed, 4); bytes.AsSpan(4 + count).CopyTo(changed.AsSpan(4 + raw.Length));
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.DecodeRequest(changed));
    }
    [Fact]
    public void Request_round_trip_preserves_exact_artifact_input_parameter_and_clock_bytes()
    {
        var value = Fixture();
        var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var bytes = CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, value.Replay);
        var restored = CompiledRuntimeProtocol.DecodeRequest(bytes);
        Assert.Equal(value.Intent.Fingerprint, restored.Intent.Fingerprint);
        Assert.Equal(value.Artifact.Copy(), restored.Artifact.Copy());
        Assert.Equal(value.Replay.Primary.CopyBytes(), restored.Replay.Primary.CopyBytes());
        Assert.Equal(value.Replay.CurrentDateTime, restored.Replay.CurrentDateTime);
        Assert.Equal(value.Replay.Parameters, restored.Replay.Parameters);
        Assert.DoesNotContain("9007199254740993", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("9007199254740993", restored.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("input")]
    [InlineData("trailing")]
    [InlineData("truncated")]
    [InlineData("negative-length")]
    public void Forged_or_incomplete_frames_are_refused(string mutation)
    {
        var value = Fixture();
        var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var bytes = CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, value.Replay);
        var headerEnd = 4 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4));
        var intentEnd = headerEnd + 4 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(headerEnd, 4));
        if (mutation == "artifact") bytes[intentEnd + 4] ^= 1;
        if (mutation == "input") bytes[^1] ^= 1;
        if (mutation == "trailing") bytes = [.. bytes, 0];
        if (mutation == "truncated") bytes = bytes[..^1];
        if (mutation == "negative-length") BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), -1);
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.DecodeRequest(bytes));
    }

    [Fact]
    public void Successful_output_binds_request_artifact_intent_and_every_exact_output_byte()
    {
        var value = Fixture();
        var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var invocation = CompiledRuntimeProtocol.DecodeRequest(CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, value.Replay));
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, "{\"ok\":true}\n"u8)]);
        var response = CompiledRuntimeProtocol.EncodeResponse(invocation, "succeeded", outputs);
        var accepted = CompiledRuntimeProtocol.DecodeResponse(response, request, value.Intent);
        Assert.Equal("succeeded", accepted.Header.Status);
        Assert.Equal(outputs.Outputs[0].CopyBytes(), accepted.Outputs!.Outputs[0].CopyBytes());
        response[^1] ^= 1;
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.DecodeResponse(response, request, value.Intent));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("succeeded")]
    public void Unknown_status_and_missing_success_outputs_cannot_pass(string status)
    {
        var value = Fixture(); var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var invocation = new CompiledRuntimeProtocol.Invocation(request, value.Intent, value.Artifact, value.Replay);
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.EncodeResponse(invocation, status));
    }

    [Fact]
    public void A_failed_runtime_cannot_return_partial_outputs_or_claim_a_passing_comparison()
    {
        var value = Fixture(); var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var invocation = new CompiledRuntimeProtocol.Invocation(request, value.Intent, value.Artifact, value.Replay);
        var bytes = CompiledRuntimeProtocol.EncodeResponse(invocation, "executionFailed");
        Assert.Null(CompiledRuntimeProtocol.DecodeResponse(bytes, request, value.Intent).Outputs);
        var output = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, [1])]);
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.EncodeResponse(invocation, "executionFailed", output));
    }

    [Fact]
    public void Duplicate_or_unknown_header_fields_are_not_silently_deserialized()
    {
        var value = Fixture(); var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var bytes = CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, value.Replay);
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4));
        var original = Encoding.UTF8.GetString(bytes, 4, count);
        foreach (var extra in new[] { ",\"entrypoint\":\"arbitrary\"}", ",\"buildId\":\"" + value.Intent.BuildId.ToString("D") + "\"}" })
        {
            var header = Encoding.UTF8.GetBytes(original[..^1] + extra);
            var changed = new byte[4 + header.Length + bytes.Length - 4 - count];
            BinaryPrimitives.WriteInt32BigEndian(changed, header.Length);
            header.CopyTo(changed, 4); bytes.AsSpan(4 + count).CopyTo(changed.AsSpan(4 + header.Length));
            Assert.ThrowsAny<Exception>(() => CompiledRuntimeProtocol.DecodeRequest(changed));
        }
    }

    [Fact]
    public void Header_input_aggregate_limits_are_enforced_before_reading_payload_frames()
    {
        var value = Fixture(); var request = CompiledRuntimeProtocol.For(value.Reference, value.Replay);
        var inputs = Enumerable.Range(0, 2).Select(index => new CompiledRuntimeProtocol.Document(index == 0 ? null : "secondary", PreviewPolicy.MaximumInputBytes, new string('a', 64))).ToArray();
        var body = request.Replay with { Inputs = inputs };
        var changed = request with { Replay = body, ReplayDigest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))).Value };
        Assert.Throws<InvalidOperationException>(() => CompiledRuntimeProtocol.EncodeRequest(changed, value.Intent, value.Artifact, value.Replay));
    }

    [Fact]
    public void Maximum_escaped_parameter_values_fit_the_bounded_header_without_normalization()
    {
        var value = Fixture();
        var parameters = Enumerable.Range(0, PreviewPolicy.MaximumParameters)
            .Select(index => new PreviewParameter("parameter-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PreviewParameterKind.String, new string('\0', PreviewPolicy.MaximumParameterTextBytes))).ToArray();
        var replay = new RegressionReplay(value.Replay.Target, value.Replay.CurrentDateTime, value.Replay.Limits, value.Replay.Primary, [], parameters);
        var request = CompiledRuntimeProtocol.For(value.Reference, replay);
        var bytes = CompiledRuntimeProtocol.EncodeRequest(request, value.Intent, value.Artifact, replay);
        Assert.InRange(BinaryPrimitives.ReadInt32BigEndian(bytes), 192 * 1024 + 1, CompiledRuntimeProtocol.MaximumHeaderBytes);
        Assert.Equal(parameters.OrderBy(item => item.Name, StringComparer.Ordinal), CompiledRuntimeProtocol.DecodeRequest(bytes).Replay.Parameters);
    }

    private static (BuildIntent Intent, BuildBytes Artifact, BuildArtifactReference Reference, RegressionReplay Replay) Fixture(string? format = null)
    {
        var revision = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var intent = new BuildIntent(Guid.NewGuid(), revision, Sha256Digest.FromBytes([1]),
            new("ferrule", BuildWorkerProtocol.Engine, "sha256:" + new string('a', 64), "adapter"),
            new(format is null ? BuildWorkerProtocol.Profile : BuildWorkerProtocol.DocumentProfile, 1, "dotnet", BuildWorkerProtocol.RuntimeVersion, "weft.dotnet-assembly.v1", BuildWorkerProtocol.MaximumSourceBytes),
            [new(BuildBoundaryKind.PrimaryInput, null, format ?? "json"), new(BuildBoundaryKind.PrimaryOutput, null, format ?? "json")],
            new(new(revision.OrganizationId, revision.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        var artifact = new BuildBytes([1, 2, 3, 4], intent.Profile.MaximumArtifactBytes);
        var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-20T12:00:00.123456789-07:00", PreviewLimits.Default,
            new(null, format is "x12" or "edifact" ? Encoding.UTF8.GetBytes(format == "x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact) : "{\"exact\":true}\n"u8.ToArray()),
            [], [new("integer", PreviewParameterKind.Integer, "9007199254740993")]);
        return (intent, artifact, BuildArtifactReference.Restore(intent, artifact.Digest, artifact.Length), replay);
    }
}
