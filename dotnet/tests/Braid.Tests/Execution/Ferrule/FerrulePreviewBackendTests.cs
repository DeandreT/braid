using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

/// <summary>Authored worker transcripts qualify adapter checks; they do not execute a mapping.</summary>
public sealed class FerrulePreviewBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Csv_direct_profile_preserves_empty_bytes_and_requires_matching_dialect(bool forged)
    {
        var runner = new TranscriptRunner(header => Reply(header, response =>
        {
            response["protocolVersion"] = CsvDocumentPolicy.Profile;
            response["profile"] = CsvDocumentPolicy.Profile;
            var artifact = Artifact("primary-output", "Row", []);
            artifact["mediaType"] = "text/tab-separated-values";
            artifact["csvDialect"] = JsonSerializer.SerializeToNode(new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema,
                forged ? "," : "\t", "\"", false, false, false, true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            response["result"]!["artifacts"] = new JsonArray(artifact);
        }, [[]]));
        var request = Request() with { Primary = new(null, []), NamedInputs = [], Profile = PreviewExecutionProfile.CsvDocument };
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(request);
        Assert.Equal(forged ? MappingPreviewStatus.ProtocolFailure : MappingPreviewStatus.Succeeded, result.Status);
        Assert.Empty(Frames(runner.LastRequest!)[2]);
        if (!forged)
        {
            var output = Assert.Single(result.Artifacts);
            Assert.Empty(output.CopyBytes());
            Assert.Equal("\t", output.CsvDialect!.Delimiter);
            Assert.Equal(FerrulePreviewBackend.CsvAdapterBuild, result.Provenance.AdapterBuild);
        }
    }

    [Fact]
    public async Task Csv_direct_profile_refuses_named_inputs_without_dispatch()
    {
        var runner = new TranscriptRunner(header => Reply(header));
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(Request() with { Profile = PreviewExecutionProfile.CsvDocument });
        Assert.Equal(MappingPreviewStatus.UnsupportedCapability, result.Status);
        Assert.Equal(0, runner.Calls);
    }

    private const string Image = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"), Guid.Parse("44444444-4444-4444-8444-444444444444"));
    private static readonly byte[] Primary = "{\"id\":\"SO-1\"}"u8.ToArray();
    private static readonly byte[] Rates = "{\"currency\":\"EUR\"}"u8.ToArray();
    private static readonly byte[] Shipment = "{\"orderId\":\"SO-1\"}"u8.ToArray();
    private static readonly byte[] Summary = "{\"lineCount\":2}"u8.ToArray();

    [Theory]
    [InlineData("application/edi-x12")]
    [InlineData("application/edifact")]
    [InlineData("application/json")]
    public async Task ExplicitDocumentProfilePreservesFramesAndSeparateProvenance(string mediaType)
    {
        var runner = new TranscriptRunner(header => Reply(header, response =>
        {
            response["protocolVersion"] = FerrulePreviewBackend.DocumentProtocolVersion;
            response["profile"] = DocumentPreviewPolicy.Profile;
            foreach (var artifact in response["result"]!["artifacts"]!.AsArray())
                artifact!["mediaType"] = mediaType;
        }));
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.Document).PreviewAsync(Request());
        Assert.Equal(MappingPreviewStatus.Succeeded, result.Status);
        Assert.Equal(DocumentPreviewPolicy.Profile, result.Profile);
        Assert.Equal(FerrulePreviewBackend.DocumentAdapterBuild, result.Provenance.AdapterBuild);
        Assert.Equal(FerrulePreviewBackend.DocumentProtocolVersion, (string?)runner.LastHeader!["protocolVersion"]);
        Assert.All(result.Artifacts, artifact => Assert.Equal(mediaType, artifact.MediaType));
        Assert.Equal(Shipment, result.Artifacts[0].CopyBytes());
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/plain")]
    [InlineData("application/edi-x12; charset=utf-8")]
    public async Task DocumentProfileRejectsUnadmittedMediaTypes(string mediaType)
    {
        var runner = new TranscriptRunner(header => Reply(header, response =>
        {
            response["protocolVersion"] = FerrulePreviewBackend.DocumentProtocolVersion;
            response["profile"] = DocumentPreviewPolicy.Profile;
            response["result"]!["artifacts"]![0]!["mediaType"] = mediaType;
        }));
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.Document).PreviewAsync(Request());
        Assert.Equal(MappingPreviewStatus.ProtocolFailure, result.Status);
        Assert.Equal(DocumentPreviewPolicy.Profile, result.Profile);
        Assert.False(result.Provenance.Confirmed);
    }

    [Fact]
    public async Task DocumentProfileRejectsLegacyResponseInsteadOfDowngrading()
    {
        var runner = new TranscriptRunner(header => Reply(header));
        var result = await new FerrulePreviewBackend(runner, PreviewExecutionProfile.Document).PreviewAsync(Request());
        Assert.Equal(MappingPreviewStatus.ProtocolFailure, result.Status);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task VerifiedTranscriptReturnsExactOutputsAndSendsTheExactRequest()
    {
        var runner = new TranscriptRunner(header => Reply(header));
        var request = Request();
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(request);

        Assert.Equal(MappingPreviewStatus.Succeeded, result.Status);
        Assert.Null(result.Failure);
        Assert.Equal(PreviewPolicy.Profile, result.Profile);
        Assert.True(result.Provenance.Confirmed);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, result.Provenance.EngineRevision);
        Assert.Equal(Image, result.Provenance.BackendBuild);
        Assert.Equal(FerrulePreviewBackend.AdapterBuild, result.Provenance.AdapterBuild);
        Assert.Equal(request.Artifact.Digest, result.Provenance.NativeDigest);
        Assert.Equal(3, result.RecordsWritten);
        Assert.Equal(["primary-output", "named-output:0"], result.Artifacts.Select(artifact => artifact.BoundaryId));
        Assert.Equal(Shipment, result.Artifacts[0].CopyBytes());
        Assert.Equal(Summary, result.Artifacts[1].CopyBytes());
        Assert.Equal(Sha256Digest.FromBytes(Summary), result.Artifacts[1].Digest);

        var header = runner.LastHeader!;
        Assert.Equal("braid.ferrule-preview.v1", (string?)header["protocolVersion"]);
        Assert.Equal(request.Artifact.Digest.Value, (string?)header["nativeDigest"]);
        Assert.Equal("entry.native.json", (string?)header["documentId"]);
        Assert.Equal("{\"kind\":\"all\"}", header["target"]!.ToJsonString());
        Assert.Equal("2026-10-20T01:02:03Z", (string?)header["currentDateTime"]);
        Assert.Equal(
            "[{\"name\":\"region\",\"value\":{\"kind\":\"string\",\"value\":\"west\"}},{\"name\":\"express\",\"value\":{\"kind\":\"boolean\",\"value\":true}},{\"name\":\"limit\",\"value\":{\"kind\":\"integer\",\"value\":\"9007199254740993\"}}]",
            header["parameters"]!.ToJsonString());
        Assert.Equal(
            $"[{{\"role\":\"primary\",\"byteLength\":{Primary.Length},\"sha256\":\"{Hex(Primary)}\"}},{{\"role\":\"named\",\"name\":\"rates\",\"byteLength\":{Rates.Length},\"sha256\":\"{Hex(Rates)}\"}}]",
            header["inputs"]!.ToJsonString());
        var frames = Frames(runner.LastRequest!);
        Assert.Equal(4, frames.Count);
        Assert.Equal(request.Artifact.CopyBytes(), frames[1]);
        Assert.Equal(Primary, frames[2]);
        Assert.Equal(Rates, frames[3]);
    }

    public static TheoryData<string> ForgedResponses() =>
    [
        "oldEngine", "previousEngine", "forgedEngine", "requestId", "revision", "digest", "documentId", "profile", "protocol", "unknownMember",
        "artifactDigest", "frameLength", "missingFrame", "trailingByte", "boundaryOrder", "duplicateBoundary", "mediaType",
        "outputLimit", "artifactLimit", "bothResultAndFailure", "unknownFailureCode", "failureWithFrames", "duplicateProperty", "emptyArtifacts"
    ];

    [Theory]
    [MemberData(nameof(ForgedResponses))]
    public async Task ForgedOrInconsistentTranscriptsAreUnconfirmedProtocolFailures(string forgery)
    {
        var limits = forgery is "outputLimit" or "artifactLimit"
            ? PreviewLimits.Default with { MaximumOutputBytes = forgery == "outputLimit" ? Shipment.Length : PreviewPolicy.MaximumOutputBytes, MaximumArtifacts = forgery == "artifactLimit" ? 1 : 64 }
            : PreviewLimits.Default;
        var runner = new TranscriptRunner(header => Forge(header, forgery));
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(Request() with { Limits = limits });

        Assert.Equal(MappingPreviewStatus.ProtocolFailure, result.Status);
        Assert.False(result.Provenance.Confirmed);
        Assert.Empty(result.Artifacts);
        Assert.Equal("preview.protocol", result.Failure!.Code);
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("invalidRequest", MappingPreviewStatus.InvalidRequest)]
    [InlineData("unknownTarget", MappingPreviewStatus.InvalidRequest)]
    [InlineData("unexpectedInput", MappingPreviewStatus.InvalidRequest)]
    [InlineData("invalidClock", MappingPreviewStatus.InvalidRequest)]
    [InlineData("invalidMapping", MappingPreviewStatus.InvalidMapping)]
    [InlineData("nativeDecodeFailed", MappingPreviewStatus.UnsupportedArtifact)]
    [InlineData("unsupportedCapability", MappingPreviewStatus.UnsupportedCapability)]
    [InlineData("missingInput", MappingPreviewStatus.MissingInput)]
    [InlineData("invalidInput", MappingPreviewStatus.InvalidInput)]
    [InlineData("missingParameter", MappingPreviewStatus.InvalidParameter)]
    [InlineData("unknownParameter", MappingPreviewStatus.InvalidParameter)]
    [InlineData("executionFailed", MappingPreviewStatus.ExecutionFailed)]
    [InlineData("resultLimitExceeded", MappingPreviewStatus.ResourceLimit)]
    [InlineData("inputLimitExceeded", MappingPreviewStatus.ResourceLimit)]
    [InlineData("digestMismatch", MappingPreviewStatus.ProtocolFailure)]
    public async Task WorkerFailuresKeepTheirBoundedCategoryAndSubject(string code, MappingPreviewStatus status)
    {
        var runner = new TranscriptRunner(header => Reply(header, response =>
        {
            response["result"] = null;
            response["failure"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = "bounded worker text",
                ["messageTruncated"] = false,
                ["subject"] = "rates",
                ["subjectTruncated"] = false
            };
        }, []));
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(Request());

        Assert.Equal(status, result.Status);
        Assert.True(result.Provenance.Confirmed);
        Assert.Empty(result.Artifacts);
        Assert.Equal(new MappingPreviewFailure("ferrule." + code, "bounded worker text", false, "rates", false), result.Failure);
    }

    public static TheoryData<string, MappingPreviewStatus> LocalRefusals() => new()
    {
        { "limits", MappingPreviewStatus.InvalidRequest },
        { "textLimit", MappingPreviewStatus.InvalidRequest },
        { "duplicateInput", MappingPreviewStatus.InvalidRequest },
        { "namedPrimary", MappingPreviewStatus.InvalidRequest },
        { "emptyInput", MappingPreviewStatus.InvalidRequest },
        { "tooManyInputs", MappingPreviewStatus.InvalidRequest },
        { "inputLimit", MappingPreviewStatus.ResourceLimit },
        { "duplicateParameter", MappingPreviewStatus.InvalidRequest },
        { "boolean", MappingPreviewStatus.InvalidParameter },
        { "longParameter", MappingPreviewStatus.InvalidParameter },
        { "clock", MappingPreviewStatus.InvalidRequest },
        { "target", MappingPreviewStatus.InvalidRequest },
        { "canceled", MappingPreviewStatus.Canceled }
    };

    [Theory]
    [MemberData(nameof(LocalRefusals))]
    public async Task ServerPolicyRefusesBeforeAnyWorkerStarts(string refusal, MappingPreviewStatus status)
    {
        var request = Request();
        request = refusal switch
        {
            "limits" => request with { Limits = PreviewLimits.Default with { MaximumInputBytes = PreviewPolicy.MaximumInputBytes + 1 } },
            "textLimit" => request with { Limits = PreviewLimits.Default with { MaximumTextBytes = 63 } },
            "duplicateInput" => request with { NamedInputs = [new("rates", Rates), new("rates", Rates)] },
            "namedPrimary" => request with { Primary = new("rates", Primary) },
            "emptyInput" => request with { NamedInputs = [new("rates", [])] },
            "tooManyInputs" => request with { NamedInputs = Enumerable.Range(0, 16).Select(index => new PreviewInputDocument($"s{index}", Rates)).ToArray() },
            "inputLimit" => request with { Limits = PreviewLimits.Default with { MaximumInputBytes = Primary.Length + Rates.Length - 1 } },
            "duplicateParameter" => request with { Parameters = [new("region", PreviewParameterKind.String, "a"), new("region", PreviewParameterKind.String, "b")] },
            "boolean" => request with { Parameters = [new("express", PreviewParameterKind.Boolean, "True")] },
            "longParameter" => request with { Parameters = [new("region", PreviewParameterKind.String, new string('x', PreviewPolicy.MaximumParameterTextBytes + 1))] },
            "clock" => request with { CurrentDateTime = "2026-10-20T01:02:03 Z" },
            "target" => request with { Target = PreviewTargetSelection.Named(new string('t', PreviewPolicy.MaximumNameBytes + 1)) },
            _ => request
        };
        using var canceled = new CancellationTokenSource();
        if (refusal == "canceled")
            await canceled.CancelAsync();
        var runner = new TranscriptRunner(header => Reply(header));
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(request, canceled.Token);

        Assert.Equal(status, result.Status);
        Assert.False(result.Provenance.Confirmed);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData(WorkerProcessFailure.Canceled, MappingPreviewStatus.Canceled)]
    [InlineData(WorkerProcessFailure.TimedOut, MappingPreviewStatus.TimedOut)]
    [InlineData(WorkerProcessFailure.OutputLimitExceeded, MappingPreviewStatus.ResourceLimit)]
    [InlineData(WorkerProcessFailure.CleanupFailed, MappingPreviewStatus.IsolationFailure)]
    [InlineData(WorkerProcessFailure.SandboxUnavailable, MappingPreviewStatus.BackendUnavailable)]
    [InlineData(WorkerProcessFailure.WorkerFailed, MappingPreviewStatus.BackendFailure)]
    public async Task ProcessFailuresMapToDistinctUnconfirmedStatuses(WorkerProcessFailure failure, MappingPreviewStatus status)
    {
        var runner = new TranscriptRunner(header => Reply(header)) { Failure = failure };
        var result = await new FerrulePreviewBackend(runner).PreviewAsync(Request());
        Assert.Equal(status, result.Status);
        Assert.False(result.Provenance.Confirmed);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public void PreviewRequiresAPreviewProfileDockerRunner()
    {
        Assert.Throws<ArgumentException>(() => new FerrulePreviewBackend(
            new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker", Image))));
        _ = new FerrulePreviewBackend(new DockerInspectionWorkerRunner(
            new DockerInspectionWorkerOptions("/usr/bin/docker", Image, profile: DockerWorkerProfile.Preview)));
    }

    [Fact]
    public async Task NamedTargetSelectionRequiresExactlyThatOneNamedOutput()
    {
        var request = Request() with { Target = PreviewTargetSelection.Named("summary"), NamedInputs = [] };
        var exact = new TranscriptRunner(header => Reply(header, response =>
        {
            var artifacts = response["result"]!["artifacts"]!.AsArray();
            artifacts.RemoveAt(0);
        }, [Summary]));
        var result = await new FerrulePreviewBackend(exact).PreviewAsync(request);
        Assert.Equal(MappingPreviewStatus.Succeeded, result.Status);
        Assert.Equal("{\"kind\":\"named\",\"name\":\"summary\"}", exact.LastHeader!["target"]!.ToJsonString());
        Assert.Equal("named-output:0", Assert.Single(result.Artifacts).BoundaryId);

        var withPrimary = new TranscriptRunner(header => Reply(header));
        Assert.Equal(MappingPreviewStatus.ProtocolFailure, (await new FerrulePreviewBackend(withPrimary).PreviewAsync(request)).Status);
    }

    private static MappingPreviewRequest Request() => new(
        NativeArtifact.Admit(Scope, "{\"source\":{}}"u8, InspectionPolicy.MaximumNativeBytes),
        PreviewTargetSelection.All,
        new PreviewInputDocument(null, Primary),
        [new PreviewInputDocument("rates", Rates)],
        [
            new PreviewParameter("region", PreviewParameterKind.String, "west"),
            new PreviewParameter("express", PreviewParameterKind.Boolean, "true"),
            new PreviewParameter("limit", PreviewParameterKind.Integer, "9007199254740993")
        ],
        "2026-10-20T01:02:03Z",
        PreviewLimits.Default);

    private static byte[] Reply(JsonObject header, Action<JsonObject>? change = null, byte[][]? outputs = null)
    {
        outputs ??= [Shipment, Summary];
        var response = new JsonObject
        {
            ["protocolVersion"] = "braid.ferrule-preview.v1",
            ["requestId"] = header["requestId"]!.DeepClone(),
            ["revision"] = header["revision"]!.DeepClone(),
            ["nativeDigest"] = header["nativeDigest"]!.DeepClone(),
            ["documentId"] = header["documentId"]!.DeepClone(),
            ["engineRevision"] = FerruleInspectionBackend.EngineRevision,
            ["profile"] = PreviewPolicy.Profile,
            ["result"] = new JsonObject
            {
                ["recordsWritten"] = 3,
                ["artifacts"] = new JsonArray(
                    Artifact("primary-output", "Shipment", Shipment),
                    Artifact("named-output:0", "summary", Summary))
            },
            ["failure"] = null
        };
        change?.Invoke(response);
        return Framed(Encoding.UTF8.GetBytes(response.ToJsonString()), outputs);
    }

    private static JsonObject Artifact(string boundary, string target, byte[] bytes) => new()
    {
        ["boundaryId"] = boundary,
        ["targetName"] = target,
        ["targetNameTruncated"] = false,
        ["mediaType"] = "application/json",
        ["recordsWritten"] = 1,
        ["byteLength"] = bytes.Length,
        ["sha256"] = Hex(bytes)
    };

    private static byte[] Forge(JsonObject header, string forgery)
    {
        JsonNode Artifacts(JsonObject response) => response["result"]!["artifacts"]!;
        switch (forgery)
        {
            case "oldEngine": return Reply(header, response => response["engineRevision"] = "0495822e102bba3f97d9bffd3c665adfb6138bec");
            case "previousEngine": return Reply(header, response => response["engineRevision"] = "577359a991a7dd44172a3c4af91fdcab7592457c");
            case "forgedEngine": return Reply(header, response => response["engineRevision"] = new string('f', 40));
            case "requestId": return Reply(header, response => response["requestId"] = Guid.NewGuid().ToString("D"));
            case "revision": return Reply(header, response => response["revision"]!["revisionId"] = Guid.NewGuid().ToString("D"));
            case "digest": return Reply(header, response => response["nativeDigest"] = new string('0', 64));
            case "documentId": return Reply(header, response => response["documentId"] = "other.json");
            case "profile": return Reply(header, response => response["profile"] = "braid.ferrule-json-preview.v2");
            case "protocol": return Reply(header, response => response["protocolVersion"] = "braid.ferrule-host.v1");
            case "unknownMember": return Reply(header, response => response["unexpected"] = true);
            case "artifactDigest": return Reply(header, response => Artifacts(response)[1]!["sha256"] = Hex(Shipment));
            case "frameLength": return Reply(header, response => Artifacts(response)[0]!["byteLength"] = Shipment.Length + 1);
            case "missingFrame": return Reply(header, outputs: [Shipment]);
            case "trailingByte": return [.. Reply(header), 0];
            case "boundaryOrder":
                return Reply(header, response =>
                {
                    Artifacts(response)[0]!["boundaryId"] = "named-output:0";
                    Artifacts(response)[1]!["boundaryId"] = "primary-output";
                });
            case "duplicateBoundary": return Reply(header, response => Artifacts(response)[1]!["boundaryId"] = "primary-output");
            case "mediaType": return Reply(header, response => Artifacts(response)[0]!["mediaType"] = "text/plain");
            case "outputLimit" or "artifactLimit": return Reply(header);
            case "bothResultAndFailure":
                return Reply(header, response => response["failure"] = new JsonObject
                {
                    ["code"] = "executionFailed",
                    ["message"] = "x",
                    ["messageTruncated"] = false,
                    ["subject"] = null,
                    ["subjectTruncated"] = false
                });
            case "unknownFailureCode":
                return Reply(header, response =>
                {
                    response["result"] = null;
                    response["failure"] = new JsonObject
                    {
                        ["code"] = "somethingNew",
                        ["message"] = "x",
                        ["messageTruncated"] = false,
                        ["subject"] = null,
                        ["subjectTruncated"] = false
                    };
                }, []);
            case "failureWithFrames":
                return Reply(header, response =>
                {
                    response["result"] = null;
                    response["failure"] = new JsonObject
                    {
                        ["code"] = "executionFailed",
                        ["message"] = "x",
                        ["messageTruncated"] = false,
                        ["subject"] = null,
                        ["subjectTruncated"] = false
                    };
                });
            case "duplicateProperty":
                var json = Encoding.UTF8.GetString(Frames(Reply(header))[0]);
                return Framed(Encoding.UTF8.GetBytes(json.Replace("\"profile\":", "\"profile\":\"x\",\"profile\":", StringComparison.Ordinal)), [Shipment, Summary]);
            case "emptyArtifacts": return Reply(header, response => Artifacts(response).AsArray().Clear(), []);
            default: throw new ArgumentOutOfRangeException(nameof(forgery));
        }
    }

    private static byte[] Framed(byte[] header, byte[][] outputs)
    {
        var frames = new List<byte>();
        foreach (var body in outputs.Prepend(header))
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)body.Length);
            frames.AddRange(prefix);
            frames.AddRange(body);
        }
        return [.. frames];
    }

    private static List<byte[]> Frames(byte[] bytes)
    {
        var frames = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            frames.Add(bytes.AsSpan(offset + 4, length).ToArray());
            offset += 4 + length;
        }
        return frames;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class TranscriptRunner(Func<JsonObject, byte[]> reply) : IInspectionWorkerRunner
    {
        public string BuildIdentity => Image;
        public int Calls { get; private set; }
        public byte[]? LastRequest { get; private set; }
        public JsonObject? LastHeader { get; private set; }
        public WorkerProcessFailure Failure { get; init; }

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = requestFrame.ToArray();
            LastHeader = JsonNode.Parse(Frames(LastRequest)[0])!.AsObject();
            return Task.FromResult(Failure == WorkerProcessFailure.None
                ? new WorkerProcessResult(reply(LastHeader), WorkerProcessFailure.None)
                : new WorkerProcessResult([], Failure));
        }
    }
}
