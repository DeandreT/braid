using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.FerruleService;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class MappingAuthoringBackendTests
{
    private static readonly ProjectScope Project = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Authoring = Guid.NewGuid();
    private const string Plan = """
        {"format":"weft.authoring.v1",
         "source":{"schema":{"name":"Source","shape":{"kind":"group","children":[{"name":"code","shape":{"kind":"scalar","scalar":"string"}}]}},"options":{"kind":"json"}},
         "target":{"schema":{"name":"Target","shape":{"kind":"group","children":[{"name":"code","shape":{"kind":"scalar","scalar":"string"}}]}},"options":{"kind":"json"}},
         "edit":{"format":"ferrule.edit.v1","operations":[{"op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"code"},"expression":{"kind":"source","id":"1","path":["code"]}}]}}
        """;

    [Fact]
    public async Task SendsExactPlanAndProjectIdentityAndRetainsCompilerRefusal()
    {
        var plan = Encoding.UTF8.GetBytes(Plan);
        var result = await new FerruleInspectionBackend(new Fake(frame =>
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(frame);
            var header = JsonNode.Parse(frame.AsSpan(4, length))!;
            Assert.Null(header["revision"]);
            Assert.Equal(Project.ProjectId.ToString("D"), (string?)header["projectId"]);
            Assert.Equal(Authoring.ToString("D"), (string?)header["authoringId"]);
            Assert.Equal(Sha256Digest.FromBytes(plan).Value, (string?)header["planDigest"]);
            Assert.Equal(plan.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4 + length)));
            Assert.Equal(plan, frame[(8 + length)..]);
            return Failure(header);
        })).AuthorAsync(Project, Authoring, plan);
        Assert.Null(result.Checkpoint);
        Assert.Equal("unsupportedSemantics", result.Failure!.Code);
        Assert.Equal(IngestionBackendStatus.InvalidMapping, result.Failure.Status);
    }

    [Fact]
    public async Task MismatchedRequestAndTrailingResponseBytesAreRefused()
    {
        foreach (var tamper in new[] { "identity", "trailing" })
        {
            var result = await new FerruleInspectionBackend(new Fake(frame =>
            {
                var header = JsonNode.Parse(frame.AsSpan(4, (int)BinaryPrimitives.ReadUInt32BigEndian(frame)))!;
                if (tamper == "identity") header["authoringId"] = Guid.NewGuid().ToString("D");
                var reply = Failure(header);
                return tamper == "trailing" ? new([.. reply.Output, 0], WorkerProcessFailure.None) : reply;
            })).AuthorAsync(Project, Authoring, Encoding.UTF8.GetBytes(Plan));
            Assert.Equal(IngestionBackendStatus.ProtocolFailure, result.Failure!.Status);
        }
    }

    [Fact]
    public async Task OversizedAndCanceledPlansNeverDispatch()
    {
        var backend = new FerruleInspectionBackend(new Fake(_ => throw new InvalidOperationException("must not dispatch")));
        var oversized = await backend.AuthorAsync(Project, Authoring, new byte[MappingAuthoringPolicy.MaximumPlanBytes + 1]);
        Assert.Equal(IngestionBackendStatus.ResourceLimit, oversized.Failure!.Status);
        var canceled = await backend.AuthorAsync(Project, Authoring, "{}"u8.ToArray(), new CancellationToken(true));
        Assert.Equal(IngestionBackendStatus.Canceled, canceled.Failure!.Status);
    }

    [Fact]
    public async Task DigestMatchedNativeWithoutReportsIsAProtocolFailure()
    {
        var backend = new FerruleInspectionBackend(new Fake(frame =>
        {
            var request = JsonNode.Parse(frame.AsSpan(4, (int)BinaryPrimitives.ReadUInt32BigEndian(frame)))!;
            var native = "{}"u8.ToArray();
            var header = new JsonObject
            {
                ["protocolVersion"] = FerruleInspectionBackend.AuthoringProtocolVersion,
                ["request"] = request,
                ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
                ["failure"] = null,
                ["result"] = new JsonObject
                {
                    ["nativeDigest"] = Sha256Digest.FromBytes(native).Value,
                    ["nativeByteCount"] = native.Length,
                    ["validation"] = null,
                    ["projection"] = null
                }
            };
            return new([.. Frame(Encoding.UTF8.GetBytes(header.ToJsonString())), .. Frame(native)], WorkerProcessFailure.None);
        }));
        var result = await backend.AuthorAsync(Project, Authoring, Encoding.UTF8.GetBytes(Plan));
        Assert.Null(result.Checkpoint);
        Assert.Equal(IngestionBackendStatus.ProtocolFailure, result.Failure!.Status);
    }

    [Fact]
    public void AuthoringCannotBypassItsFlagThroughAnotherProfile()
    {
        var frame = Frame(Encoding.UTF8.GetBytes("{\"protocolVersion\":\"weft.ferrule-authoring.v1\"}"));
        Assert.True(AuthoringFrameAdmission.Allows(DockerWorkerProfile.Authoring, frame));
        foreach (var profile in new[] { DockerWorkerProfile.Inspection, DockerWorkerProfile.Edit, DockerWorkerProfile.Preview, DockerWorkerProfile.Build, DockerWorkerProfile.Interchange })
            Assert.False(AuthoringFrameAdmission.Allows(profile, frame));
        Assert.False(AuthoringFrameAdmission.Allows(DockerWorkerProfile.Authoring, Frame("{}"u8.ToArray())));
        var config = new Dictionary<string, string?>
        {
            [FerruleServiceSettings.Listen] = "127.0.0.1:8080",
            [FerruleServiceSettings.Authentication] = "platformSandbox"
        };
        Assert.False(FerruleServiceSettings.Read(key => config.GetValueOrDefault(key)).Authoring);
        config[FerruleServiceSettings.AuthoringEnabled] = "true";
        Assert.True(FerruleServiceSettings.Read(key => config.GetValueOrDefault(key)).Authoring);
        config[FerruleServiceSettings.AuthoringEnabled] = "yes";
        Assert.ThrowsAny<Exception>(() => FerruleServiceSettings.Read(key => config.GetValueOrDefault(key)));
    }

    [NativeAuthoringFact]
    public async Task ActualPortableRustCompilerProducesVerifiedNativeAndTypedReports()
    {
        var runner = new NativeRunner();
        var backend = new FerruleInspectionBackend(runner);
        var first = await backend.AuthorAsync(Project, Authoring, Encoding.UTF8.GetBytes(Plan));
        Assert.Null(first.Failure);
        var checkpoint = Assert.IsType<MappingAuthoringCheckpoint>(first.Checkpoint);
        Assert.True(checkpoint.Validation.IsValid);
        Assert.NotEmpty(checkpoint.Projection.Fields);
        Assert.Equal(Project, checkpoint.Project);
        Assert.Equal(Authoring, checkpoint.AuthoringId);
        Assert.Equal(Sha256Digest.FromBytes(checkpoint.CopyNativeBytes()), checkpoint.NativeDigest);
        var copy = checkpoint.CopyNativeBytes();
        copy[0] ^= 1;
        Assert.NotEqual(copy, checkpoint.CopyNativeBytes());
        var second = await backend.AuthorAsync(Project, Authoring, Encoding.UTF8.GetBytes(Plan));
        Assert.Null(second.Failure);
        Assert.Equal(checkpoint.CopyNativeBytes(), second.Checkpoint!.CopyNativeBytes());
    }

    private static WorkerProcessResult Failure(JsonNode request) => new(Frame(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["protocolVersion"] = FerruleInspectionBackend.AuthoringProtocolVersion,
        ["request"] = request,
        ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
        ["result"] = null,
        ["failure"] = new JsonObject { ["code"] = "unsupportedSemantics", ["message"] = "Unsupported", ["messageTruncated"] = false, ["operationIndex"] = null }
    }.ToJsonString())), WorkerProcessFailure.None);

    private static byte[] Frame(byte[] bytes)
    {
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }

    private sealed class Fake(Func<byte[], WorkerProcessResult> reply) : IInspectionWorkerRunner
    {
        public string BuildIdentity => "authoring-transcript-test";
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) => Task.FromResult(reply(frame.ToArray()));
    }

    private sealed class NativeRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "portable-authoring-test-not-isolation";
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("WEFT_AUTHORING_TEST_EXECUTABLE")!)
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            try
            {
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                using var output = new MemoryStream();
                var drain = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token);
                process.StandardInput.Close();
                await Task.WhenAll(drain, process.WaitForExitAsync(timeout.Token));
                Assert.Equal(0, process.ExitCode);
                Assert.Empty(await stderr);
                return new(output.ToArray(), WorkerProcessFailure.None);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
    }

    public sealed class NativeAuthoringFactAttribute : FactAttribute
    {
        public NativeAuthoringFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WEFT_AUTHORING_TEST_EXECUTABLE") is null)
                Skip = "Set WEFT_AUTHORING_TEST_EXECUTABLE to the portable Rust qualification example; not sandbox qualification.";
        }
    }
}
