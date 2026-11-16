using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Braid.Domain;
using Braid.FerruleService;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class MappingAuthoredPreviewBackendTests
{
    private static readonly byte[] Native = "retained native fixture"u8.ToArray();
    private static readonly byte[] Sample = "sample fixture"u8.ToArray();
    private static MappingAuthoredPreviewSubject Subject() => new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(),
        Sha256Digest.FromBytes("exact proposal"u8), Sha256Digest.FromBytes(Native), Sha256Digest.FromBytes(Sample));

    [Fact]
    public async Task Frames_exact_owned_bytes_and_verifies_ephemeral_project_output()
    {
        var subject = Subject(); var output = "a,b\r\nx,y\r\n"u8.ToArray();
        var backend = new FerruleInspectionBackend(new Fake(frame =>
        {
            var offset = 0; var request = JsonNode.Parse(ReadFrame(frame, ref offset))!;
            Assert.Equal(Native, ReadFrame(frame, ref offset)); Assert.Equal(Sample, ReadFrame(frame, ref offset));
            Assert.Equal(frame.Length, offset);
            Assert.Null(request["revision"]); Assert.Null(request["mappingId"]);
            Assert.Equal(subject.ProposalDigest.Value, (string?)request["proposalDigest"]);
            Assert.Equal(subject.SampleDigest.Value, (string?)request["sampleDigest"]);
            return Reply(request, output);
        }));
        var result = await backend.PreviewAuthoredAsync(subject, Native, Sample);
        Assert.Null(result.Failure); var evidence = Assert.IsType<MappingAuthoredPreviewEvidence>(result.Evidence);
        Assert.Equal(subject, evidence.Subject); Assert.Equal(output, evidence.CopyOutput());
        Assert.Equal("text/csv", evidence.MediaType); Assert.Equal(Sha256Digest.FromBytes(output), evidence.OutputDigest);
        var copy = evidence.CopyOutput(); copy[0] ^= 1; Assert.Equal(output, evidence.CopyOutput());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("authoring")]
    [InlineData("proposal")]
    [InlineData("sample")]
    [InlineData("native")]
    [InlineData("engine")]
    [InlineData("digest")]
    [InlineData("type")]
    [InlineData("unknown")]
    [InlineData("missing-count")]
    [InlineData("missing-failure")]
    [InlineData("both")]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    public async Task Substituted_or_ambiguous_response_never_becomes_execution_evidence(string mutation)
    {
        var backend = new FerruleInspectionBackend(new Fake(frame =>
        {
            var offset = 0; var request = JsonNode.Parse(ReadFrame(frame, ref offset))!;
            var result = SuccessHeader(request, []);
            switch (mutation)
            {
                case "project": request["projectId"] = Guid.NewGuid().ToString("D"); break;
                case "authoring": request["authoringId"] = Guid.NewGuid().ToString("D"); break;
                case "proposal": request["proposalDigest"] = new string('0', 64); break;
                case "sample": request["sampleDigest"] = new string('0', 64); break;
                case "native": request["nativeDigest"] = new string('0', 64); break;
                case "engine": result["engineRevision"] = "other"; break;
                case "digest": result["result"]!["outputDigest"] = new string('0', 64); break;
                case "type": result["result"]!["mediaType"] = "text/html"; break;
                case "unknown": result["result"]!["extra"] = true; break;
                case "missing-count": result["result"]!.AsObject().Remove("outputByteCount"); break;
                case "missing-failure": result.Remove("failure"); break;
                case "both": result["failure"] = new JsonObject { ["code"] = "executionFailed" }; break;
            }
            var text = result.ToJsonString();
            if (mutation == "duplicate") text = text.Insert(1, "\"failure\":null,");
            byte[] response = [.. Frame(Encoding.UTF8.GetBytes(text)), .. Frame([])];
            if (mutation == "trailing") response = [.. response, 0];
            return new(response, WorkerProcessFailure.None);
        }));
        var actual = await backend.PreviewAuthoredAsync(Subject(), Native, Sample);
        Assert.Null(actual.Evidence); Assert.Equal(MappingOperationStatus.ProtocolFailure, actual.Failure!.Status);
    }

    [Fact]
    public async Task Invalid_inputs_limits_and_cancellation_do_not_dispatch()
    {
        var backend = new FerruleInspectionBackend(new Fake(_ => throw new Exception("Unexpected dispatch")));
        Assert.Equal("digestMismatch", (await backend.PreviewAuthoredAsync(Subject(), "other"u8.ToArray(), Sample)).Failure!.Code);
        Assert.Equal(MappingOperationStatus.ResourceLimit, (await backend.PreviewAuthoredAsync(Subject(), Native, new byte[MappingAuthoredPreviewPolicy.MaximumSampleBytes + 1])).Failure!.Status);
        Assert.Equal(MappingOperationStatus.ResourceLimit, (await backend.PreviewAuthoredAsync(Subject(), new byte[MappingAuthoredPreviewPolicy.MaximumNativeBytes + 1], Sample)).Failure!.Status);
        Assert.Equal(MappingOperationStatus.Canceled, (await backend.PreviewAuthoredAsync(Subject(), Native, Sample, new(true))).Failure!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Categorical_failure_requires_no_output_frame(bool addsOutput)
    {
        var backend = new FerruleInspectionBackend(new Fake(frame =>
        {
            var offset = 0; var request = JsonNode.Parse(ReadFrame(frame, ref offset))!;
            var header = SuccessHeader(request, []); header["result"] = null;
            header["failure"] = new JsonObject { ["code"] = "invalidSample" };
            var response = Frame(Encoding.UTF8.GetBytes(header.ToJsonString()));
            return new(addsOutput ? [.. response, .. Frame([])] : response, WorkerProcessFailure.None);
        }));
        var result = await backend.PreviewAuthoredAsync(Subject(), Native, Sample);
        Assert.Null(result.Evidence);
        Assert.Equal(addsOutput ? MappingOperationStatus.ProtocolFailure : MappingOperationStatus.BackendFailure, result.Failure!.Status);
    }

    [Fact]
    public void Dedicated_profile_cannot_bypass_disabled_feature_through_legacy_profiles()
    {
        var frame = Frame(Encoding.UTF8.GetBytes("{\"protocolVersion\":\"braid.ferrule-authored-preview.v1\"}"));
        Assert.True(AuthoredPreviewFrameAdmission.Allows(DockerWorkerProfile.AuthoredPreview, frame));
        foreach (var profile in new[] { DockerWorkerProfile.Inspection, DockerWorkerProfile.Authoring, DockerWorkerProfile.Edit,
            DockerWorkerProfile.Preview, DockerWorkerProfile.Interchange, DockerWorkerProfile.Build, DockerWorkerProfile.Compiled })
            Assert.False(AuthoredPreviewFrameAdmission.Allows(profile, frame));
        Assert.False(AuthoredPreviewFrameAdmission.Allows(DockerWorkerProfile.AuthoredPreview, Frame("{}"u8.ToArray())));
        var duplicate = Frame(Encoding.UTF8.GetBytes("{\"protocolVersion\":\"braid.ferrule-authored-preview.v1\",\"protocolVersion\":\"braid.ferrule-host.v1\"}"));
        Assert.False(AuthoredPreviewFrameAdmission.Allows(DockerWorkerProfile.AuthoredPreview, duplicate));
        Assert.False(AuthoredPreviewFrameAdmission.Allows(DockerWorkerProfile.Inspection, duplicate));
        var settings = new Dictionary<string, string?>
        {
            [FerruleServiceSettings.Listen] = "127.0.0.1:8080",
            [FerruleServiceSettings.Authentication] = "platformSandbox"
        };
        Assert.False(FerruleServiceSettings.Read(settings.GetValueOrDefault).AuthoredPreview);
        settings[FerruleServiceSettings.AuthoredPreviewEnabled] = "true";
        Assert.True(FerruleServiceSettings.Read(settings.GetValueOrDefault).AuthoredPreview);
        settings[FerruleServiceSettings.AuthoredPreviewEnabled] = "yes";
        Assert.ThrowsAny<Exception>(() => FerruleServiceSettings.Read(settings.GetValueOrDefault));
    }

    private static JsonObject SuccessHeader(JsonNode request, byte[] output) => new()
    {
        ["protocolVersion"] = FerruleInspectionBackend.AuthoredPreviewProtocolVersion,
        ["request"] = request,
        ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
        ["failure"] = null,
        ["result"] = new JsonObject
        {
            ["outputDigest"] = Sha256Digest.FromBytes(output).Value,
            ["outputByteCount"] = output.Length,
            ["format"] = "csv",
            ["mediaType"] = "text/csv"
        }
    };
    private static WorkerProcessResult Reply(JsonNode request, byte[] output) =>
        new([.. Frame(Encoding.UTF8.GetBytes(SuccessHeader(request, output).ToJsonString())), .. Frame(output)], WorkerProcessFailure.None);
    private static byte[] Frame(byte[] bytes)
    {
        var frame = new byte[4 + bytes.Length]; BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)bytes.Length);
        bytes.CopyTo(frame, 4); return frame;
    }
    private static byte[] ReadFrame(byte[] frame, ref int offset)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(offset));
        var bytes = frame.AsSpan(offset + 4, length).ToArray(); offset += length + 4; return bytes;
    }
    private sealed class Fake(Func<byte[], WorkerProcessResult> reply) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "authored-preview-transcript-not-isolation";
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => Task.FromResult(reply(frame.ToArray()));
    }
}
