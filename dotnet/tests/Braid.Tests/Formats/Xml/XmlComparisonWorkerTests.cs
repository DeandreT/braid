using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Operations;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

/// <summary>In-process transport definitions only. These do not qualify the real Linux containment boundary.</summary>
public sealed class XmlComparisonWorkerTests
{
    [Fact]
    public async Task Both_sides_are_compared_in_one_request_and_only_safe_facts_return()
    {
        var expected = Set((null, "<root b='2' a='1'>private-sentinel</root>"));
        var actual = Set((null, "<root a='1' b='2'>private-sentinel</root>"));
        var runner = new MockRunner();
        var result = await new XmlCanonicalComparisonWorker(runner, Options()).CompareAsync(expected, actual, OperationStatusV1.Succeeded);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(XmlComparisonWorkerFailure.None, result.Failure);
        Assert.Equal(RegressionComparisonOutcome.Passed, result.Comparison!.Outcome);
        Assert.Equal(expected.ByteCount, result.Comparison.ExpectedOriginalBytes);
        Assert.Equal(actual.ByteCount, result.Comparison.ActualOriginalBytes);
        Assert.DoesNotContain("private-sentinel", Encoding.UTF8.GetString(runner.LastResponse!), StringComparison.Ordinal);
        Assert.True(result.Comparison.ExpectedCanonical!.Bytes > 0);
    }

    [Fact]
    public void Startup_dependency_probe_exercises_the_fixed_canonicalizer()
    {
        XmlComparisonProtocol.VerifyRuntimeDependencies(default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => XmlComparisonProtocol.VerifyRuntimeDependencies(canceled.Token));
    }

    [Fact]
    public async Task Metadata_is_bound_in_the_request_but_not_echoed_into_comparison_facts()
    {
        const string location = "https://private.invalid/schema.xsd";
        var metadata = new XmlDocumentMetadata(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile,
            new XmlSchemaHints(location, []));
        var expected = XmlComparisonOutputSet.Capture([XmlComparisonOutput.Capture(RegressionOutputKey.Primary, "<r/>"u8, metadata)]);
        var actual = Set((null, "<r/>"));
        var request = XmlComparisonProtocol.For(expected, actual, OperationStatusV1.Succeeded, Options());
        var restored = XmlComparisonProtocol.DecodeRequest(XmlComparisonProtocol.EncodeRequest(request, expected, actual));
        Assert.Equal(metadata, restored.Expected.Outputs[0].Metadata);
        var runner = new MockRunner();
        var result = await new XmlCanonicalComparisonWorker(runner, Options()).CompareAsync(expected, actual, OperationStatusV1.Succeeded);
        Assert.Equal(XmlCanonicalDifferenceKind.XmlMetadataDiffer, Assert.Single(result.Comparison!.Differences).Kind);
        Assert.DoesNotContain(location, Encoding.UTF8.GetString(runner.LastResponse!), StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_binary_round_trip_handles_64_documents_and_the_full_8MiB_per_side_without_base64()
    {
        var payload = new byte[XmlDocumentPolicy.MaximumBytes / XmlDocumentPolicy.MaximumOutputs];
        payload[0] = 0xff;
        var set = XmlComparisonOutputSet.Capture(Enumerable.Range(0, 64).Select(index =>
            XmlComparisonOutput.Capture(RegressionOutputKey.Named("out" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)),
                payload, XmlDocumentMetadata.WithoutHints)).ToArray());
        var request = XmlComparisonProtocol.For(set, set, OperationStatusV1.Succeeded, Options());
        var frame = XmlComparisonProtocol.EncodeRequest(request, set, set);
        Assert.True(frame.Length < 2 * set.ByteCount + XmlComparisonProtocol.MaximumHeaderBytes);
        Assert.True(frame.Length <= ProcessCompiledRuntimeOptions.Profile.MaximumRequestBytes);
        Assert.True(XmlComparisonProtocol.MaximumResponseBytes <= ProcessCompiledRuntimeOptions.Profile.MaximumOutputBytes);
        var restored = XmlComparisonProtocol.DecodeRequest(frame);
        Assert.Equal(64, restored.Expected.Outputs.Count);
        Assert.Equal(XmlDocumentPolicy.MaximumBytes, restored.Expected.ByteCount);
        Assert.Equal(XmlDocumentPolicy.MaximumBytes, restored.Actual.ByteCount);
        Assert.Equal(payload, restored.Actual.Outputs[^1].CopyBytes());
        Assert.Throws<JsonException>(() => CompiledRuntimeProtocol.DecodeRequest(frame));
    }

    public enum BadRequest
    {
        NegativeHeader, OversizedHeader, TruncatedPrefix, TruncatedPayload, TrailingBytes, TamperedPayload,
        WrongPayloadLength, WrongVersion, WrongComparer, WrongDigest, InvalidStatus, NullSet, TooManyDocuments,
        AggregateOverflow, DuplicateKey, OutOfOrder, UnknownProperty, DuplicateProperty, InvalidLimits
    }

    [Theory]
    [InlineData(BadRequest.NegativeHeader)]
    [InlineData(BadRequest.OversizedHeader)]
    [InlineData(BadRequest.TruncatedPrefix)]
    [InlineData(BadRequest.TruncatedPayload)]
    [InlineData(BadRequest.TrailingBytes)]
    [InlineData(BadRequest.TamperedPayload)]
    [InlineData(BadRequest.WrongPayloadLength)]
    [InlineData(BadRequest.WrongVersion)]
    [InlineData(BadRequest.WrongComparer)]
    [InlineData(BadRequest.WrongDigest)]
    [InlineData(BadRequest.InvalidStatus)]
    [InlineData(BadRequest.NullSet)]
    [InlineData(BadRequest.TooManyDocuments)]
    [InlineData(BadRequest.AggregateOverflow)]
    [InlineData(BadRequest.DuplicateKey)]
    [InlineData(BadRequest.OutOfOrder)]
    [InlineData(BadRequest.UnknownProperty)]
    [InlineData(BadRequest.DuplicateProperty)]
    [InlineData(BadRequest.InvalidLimits)]
    public void Malformed_request_frames_are_typed_refusals(BadRequest mutation)
    {
        var set = Set((null, "<r/>"), ("other", "<s/>"));
        var request = XmlComparisonProtocol.For(set, set, OperationStatusV1.Succeeded, Options());
        var frame = XmlComparisonProtocol.EncodeRequest(request, set, set);
        switch (mutation)
        {
            case BadRequest.NegativeHeader: BinaryPrimitives.WriteInt32BigEndian(frame, -1); break;
            case BadRequest.OversizedHeader: BinaryPrimitives.WriteInt32BigEndian(frame, XmlComparisonProtocol.MaximumHeaderBytes + 1); break;
            case BadRequest.TruncatedPrefix: frame = frame[..2]; break;
            case BadRequest.TruncatedPayload: frame = frame[..^1]; break;
            case BadRequest.TrailingBytes: frame = [.. frame, 0]; break;
            case BadRequest.TamperedPayload: frame[^1] ^= 1; break;
            case BadRequest.WrongPayloadLength:
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4 + BinaryPrimitives.ReadInt32BigEndian(frame), 4), int.MaxValue); break;
            case BadRequest.DuplicateProperty:
                frame = ReplaceHeader(frame, text => text[..^1] + ",\"requestId\":\"duplicate\"}"); break;
            default:
                frame = ChangeHeader(frame, header =>
                {
                    var body = header["body"]!.AsObject();
                    var documents = body["expected"]!.AsArray();
                    switch (mutation)
                    {
                        case BadRequest.WrongVersion: header["protocolVersion"] = CompiledRuntimeProtocol.Protocol; break;
                        case BadRequest.WrongComparer: header["comparerVersion"] = "arbitrary"; break;
                        case BadRequest.WrongDigest: header["requestDigest"] = new string('0', 64); return;
                        case BadRequest.InvalidStatus: body["executionStatus"] = 999; break;
                        case BadRequest.NullSet: body["expected"] = null; break;
                        case BadRequest.TooManyDocuments:
                            body["expected"] = new JsonArray(Enumerable.Range(0, 65).Select(_ => documents[0]!.DeepClone()).ToArray()); break;
                        case BadRequest.AggregateOverflow:
                            foreach (var document in documents) document!["byteCount"] = XmlDocumentPolicy.MaximumBytes; break;
                        case BadRequest.DuplicateKey: documents[1]!["name"] = null; break;
                        case BadRequest.OutOfOrder: documents[0]!["name"] = "z"; break;
                        case BadRequest.UnknownProperty: header["assemblyPath"] = "/untrusted.dll"; break;
                        case BadRequest.InvalidLimits: body["limits"]!["canonicalBytesTotal"] = long.MaxValue; break;
                        default: throw new InvalidOperationException();
                    }
                    Rebind(header);
                });
                break;
        }
        Assert.Throws<InvalidOperationException>(() => XmlComparisonProtocol.DecodeRequest(frame));
    }

    public enum BadResponse { Version, Comparer, RequestId, Digest, Outcome, DifferenceKind, ByteCount, CanonicalTotal, Offset, DuplicateDifference, Trailing, UnknownProperty }

    [Theory]
    [InlineData(BadResponse.Version)]
    [InlineData(BadResponse.Comparer)]
    [InlineData(BadResponse.RequestId)]
    [InlineData(BadResponse.Digest)]
    [InlineData(BadResponse.Outcome)]
    [InlineData(BadResponse.DifferenceKind)]
    [InlineData(BadResponse.ByteCount)]
    [InlineData(BadResponse.CanonicalTotal)]
    [InlineData(BadResponse.Offset)]
    [InlineData(BadResponse.DuplicateDifference)]
    [InlineData(BadResponse.Trailing)]
    [InlineData(BadResponse.UnknownProperty)]
    public async Task Malformed_or_unbound_response_cannot_be_a_comparison(BadResponse mutation)
    {
        var runner = new MockRunner
        {
            Mutate = frame => mutation == BadResponse.Trailing ? [.. frame, 0] : ChangeHeader(frame, header =>
            {
                var difference = header["differences"]!.AsArray()[0]!;
                switch (mutation)
                {
                    case BadResponse.Version: header["protocolVersion"] = "other"; break;
                    case BadResponse.Comparer: header["comparerVersion"] = "other"; break;
                    case BadResponse.RequestId: header["requestId"] = Guid.NewGuid().ToString("D"); break;
                    case BadResponse.Digest: header["requestDigest"] = new string('0', 64); break;
                    case BadResponse.Outcome: header["outcome"] = 999; break;
                    case BadResponse.DifferenceKind: difference["kind"] = 999; break;
                    case BadResponse.ByteCount: difference["expectedByteCount"] = 0; break;
                    case BadResponse.CanonicalTotal: header["expectedCanonical"]!["bytes"] = long.MaxValue; break;
                    case BadResponse.Offset: difference["canonicalByteOffset"] = long.MaxValue; break;
                    case BadResponse.DuplicateDifference: header["differences"]!.AsArray().Add(difference.DeepClone()); break;
                    case BadResponse.UnknownProperty: header["canonicalXml"] = "private"; break;
                    default: throw new InvalidOperationException();
                }
            })
        };
        var result = await new XmlCanonicalComparisonWorker(runner, Options()).CompareAsync(Set((null, "<r>a</r>")), Set((null, "<r>b</r>")), OperationStatusV1.Succeeded);
        Assert.Equal(XmlComparisonWorkerFailure.InvalidResponse, result.Failure);
        Assert.Null(result.Comparison);
    }

    [Fact]
    public void Response_is_bound_to_both_complete_sets_and_trusted_limit_selection()
    {
        var set = Set((null, "<r/>"));
        var options = Options();
        var request = XmlComparisonProtocol.For(set, set, OperationStatusV1.Succeeded, options);
        var response = XmlComparisonProtocol.Compare(XmlComparisonProtocol.DecodeRequest(XmlComparisonProtocol.EncodeRequest(request, set, set)), default);
        Assert.Throws<InvalidOperationException>(() => XmlComparisonProtocol.DecodeResponse(response, request, set, Set((null, "<changed/>"))));
        var differentLimits = XmlComparisonProtocol.For(set, set, OperationStatusV1.Succeeded,
            new(new(1024, 1), new(1024, 1024, 2048)));
        Assert.Throws<InvalidOperationException>(() => XmlComparisonProtocol.DecodeResponse(response, differentLimits, set, set));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(1024, long.MaxValue, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XmlCanonicalizationLimits(1024, 1024, 0));
    }

    [Fact]
    public async Task Original_budget_is_charged_for_both_sides_before_any_parsing()
    {
        var expected = Set((null, "<r/>"));
        var actual = Set((null, "<s/>"));
        var worker = new XmlCanonicalComparisonWorker(new MockRunner(), new(new(expected.ByteCount + actual.ByteCount - 1, 1), new(1024, 1024, 2048)));
        var result = await worker.CompareAsync(expected, actual, OperationStatusV1.Succeeded);
        Assert.Equal(XmlCanonicalFailure.ResourceLimit, result.Comparison!.NotEvaluatedReason);
        Assert.Equal(expected.ByteCount + actual.ByteCount, result.Comparison.Diagnostic!.Observed);
        Assert.Equal(XmlCanonicalPhase.Admission, result.Comparison.Diagnostic.Phase);
    }

    [Fact]
    public async Task Canonical_budget_counts_every_document_after_difference_truncation_and_includes_unmatched_tails()
    {
        var expected = Set((null, "<r>a</r>"), ("one", "<r>a</r>"));
        var actual = Set((null, "<r>b</r>"), ("two", "<r>b</r>"));
        var enough = new XmlCanonicalComparisonWorker(new MockRunner(), new(new(4096, 1), new(4096, 1024, 32)));
        var compared = (await enough.CompareAsync(expected, actual, OperationStatusV1.Succeeded)).Comparison!;
        Assert.Equal(RegressionComparisonOutcome.Failed, compared.Outcome);
        Assert.Equal(3, compared.TotalDifferenceCount);
        Assert.Single(compared.Differences);
        Assert.Equal(16, compared.ExpectedCanonical!.Bytes);
        Assert.Equal(16, compared.ActualCanonical!.Bytes);
        var limited = new XmlCanonicalComparisonWorker(new MockRunner(), new(new(4096, 1), new(4096, 1024, 31)));
        var refused = (await limited.CompareAsync(expected, actual, OperationStatusV1.Succeeded)).Comparison!;
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, refused.Outcome);
        Assert.Equal(XmlCanonicalFailure.ResourceLimit, refused.NotEvaluatedReason);
        Assert.Empty(refused.Differences);
        Assert.Null(refused.ExpectedCanonical);
    }

    [Fact]
    public async Task Identical_and_unmatched_invalid_documents_are_not_skipped_or_partially_reported()
    {
        var worker = new XmlCanonicalComparisonWorker(new MockRunner(), Options());
        var sameInvalid = Set((null, "<!DOCTYPE r><r/>"));
        var identical = await worker.CompareAsync(sameInvalid, sameInvalid, OperationStatusV1.Succeeded);
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, identical.Comparison!.Outcome);
        var result = await worker.CompareAsync(Set((null, "<r>a</r>"), ("tail", "<broken>")), Set((null, "<r>b</r>")), OperationStatusV1.Succeeded);
        Assert.Equal(RegressionComparisonOutcome.NotEvaluated, result.Comparison!.Outcome);
        Assert.Empty(result.Comparison.Differences);
        Assert.Null(result.Comparison.ExpectedCanonical);
    }

    [Fact]
    public async Task Cancellation_returns_no_facts_and_does_not_poison_a_later_successful_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MockRunner
        {
            Before = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        };
        var worker = new XmlCanonicalComparisonWorker(runner, Options());
        var set = Set((null, "<r/>"));
        using var cancellation = new CancellationTokenSource();
        var pending = worker.CompareAsync(set, set, OperationStatusV1.Succeeded, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        var canceled = await pending;
        Assert.Equal(XmlComparisonWorkerFailure.Canceled, canceled.Failure);
        Assert.Null(canceled.Comparison);
        runner.Before = null;
        Assert.Equal(RegressionComparisonOutcome.Passed, (await worker.CompareAsync(set, set, OperationStatusV1.Succeeded)).Comparison!.Outcome);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        Assert.Equal(XmlComparisonWorkerFailure.Canceled, (await worker.CompareAsync(set, set, OperationStatusV1.Succeeded, alreadyCanceled.Token)).Failure);
        Assert.Equal(2, runner.Calls);
    }

    [Theory]
    [InlineData(WorkerProcessFailure.TimedOut, (int)XmlComparisonWorkerFailure.TimedOut)]
    [InlineData(WorkerProcessFailure.CleanupFailed, (int)XmlComparisonWorkerFailure.Unavailable)]
    [InlineData(WorkerProcessFailure.SandboxUnavailable, (int)XmlComparisonWorkerFailure.Unavailable)]
    [InlineData(WorkerProcessFailure.OutputLimitExceeded, (int)XmlComparisonWorkerFailure.Unavailable)]
    public async Task Worker_failure_never_returns_partial_comparison_facts(WorkerProcessFailure failure, int expected)
    {
        var runner = new MockRunner { Failure = failure };
        var result = await new XmlCanonicalComparisonWorker(runner, Options()).CompareAsync(Set((null, "<r/>")), Set((null, "<r/>")), OperationStatusV1.Succeeded);
        Assert.Equal((XmlComparisonWorkerFailure)expected, result.Failure);
        Assert.Null(result.Comparison);
    }

    [BuildWorkerLinuxFact]
    public void Fixed_XML_operation_changes_only_dispatch_and_preserves_the_read_only_compiled_profile()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var assembly = Path.Combine(root, "braid-compiled-runtime.dll");
        var launcher = Path.Combine(root, "launcher");
        var legacy = new ProcessCompiledRuntimeOptions("sha256:" + new string('a', 64), assembly, launcher, root, root);
        var xml = new ProcessCompiledRuntimeOptions(legacy.NativeIdentity, assembly, launcher, root, root)
        { Operation = CompiledRuntimeOperation.XmlCanonicalComparison };
        var grants = new ProcessCompileOptions.ToolchainGrants([root], [Path.Combine(root, "interpreter")]);
        var legacyArguments = legacy.Arguments(grants);
        var xmlArguments = xml.Arguments(grants);
        Assert.Equal(CompiledRuntimeOperation.Mapping, legacy.Operation);
        Assert.Equal(legacyArguments, xmlArguments.Take(xmlArguments.Count - 1));
        Assert.Equal(XmlComparisonProtocol.ModeArgument, xmlArguments[^1]);
        Assert.Equal(legacy.Environment(root).OrderBy(item => item.Key, StringComparer.Ordinal),
            xml.Environment(root).OrderBy(item => item.Key, StringComparer.Ordinal));
        Assert.Equal("0", xml.Environment(root)["DOTNET_EnableWriteXorExecute"]);
        Assert.Equal("false", xml.Environment(root)["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]);
        Assert.Equal(512L << 20, ProcessCompiledRuntimeOptions.Profile.MemoryBytes);
        Assert.Equal(3L << 30, ProcessCompiledRuntimeOptions.AddressSpaceBytes);
        Assert.Equal(1L << 20, ProcessCompiledRuntimeOptions.FileBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), xml.WallTimeout);
        Assert.Equal(30, ProcessCompiledRuntimeOptions.Profile.CpuSeconds);
        Assert.Equal(TimeSpan.FromSeconds(2), ProcessWorkerOptions.KillTimeout);
        Assert.NotEqual(XmlComparisonProtocol.Version, CompiledRuntimeProtocol.Protocol);
        Assert.Equal(XmlCanonicalComparison.Policy, XmlComparisonProtocol.Comparer);
        Assert.Equal("BraidXmlComparisonFailed:decode\n", XmlComparisonProtocol.ProbeRefusal);
    }

    [BuildWorkerLinuxFact]
    public void Runtime_identity_separates_modes_and_covers_the_packaged_canonicalization_dependency()
    {
        var root = Directory.CreateTempSubdirectory("braid-xml-identity-").FullName;
        try
        {
            var runtime = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var dotnet = Directory.CreateDirectory(Path.Combine(root, "dotnet")).FullName;
            Directory.CreateDirectory(Path.Combine(dotnet, "host"));
            Directory.CreateDirectory(Path.Combine(dotnet, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion));
            var assembly = Path.Combine(runtime, "braid-compiled-runtime.dll");
            var dependency = Path.Combine(runtime, "System.Security.Cryptography.Xml.dll");
            var launcher = Path.Combine(root, "launcher");
            foreach (var file in new[] { assembly, dependency, launcher, Path.Combine(dotnet, "dotnet") }) File.WriteAllBytes(file, [1]);
            var native = "sha256:" + new string('a', 64);
            var mapping = new ProcessCompiledRuntimeOptions(native, assembly, launcher, root, dotnet);
            var xml = new ProcessCompiledRuntimeOptions(native, assembly, launcher, root, dotnet)
            { Operation = CompiledRuntimeOperation.XmlCanonicalComparison };
            var mappingIdentity = ProcessCompiledRuntimeRunner.ComputeIdentity(mapping, [], []);
            var xmlIdentity = ProcessCompiledRuntimeRunner.ComputeIdentity(xml, [], []);
            Assert.NotEqual(mappingIdentity, xmlIdentity);
            Assert.Equal(mappingIdentity, ProcessCompiledRuntimeRunner.ComputeIdentity(mapping, [], []));
            File.WriteAllBytes(dependency, [2]);
            Assert.NotEqual(xmlIdentity, ProcessCompiledRuntimeRunner.ComputeIdentity(xml, [], []));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static XmlComparisonWorkerOptions Options() => new(new(4096, 4), new(4096, 4096, 8192));
    private static XmlComparisonOutputSet Set(params (string? Name, string Xml)[] documents) => XmlComparisonOutputSet.Capture(documents.Select(value =>
        XmlComparisonOutput.Capture(value.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(value.Name),
            Encoding.UTF8.GetBytes(value.Xml), XmlDocumentMetadata.WithoutHints)).ToArray());
    private static byte[] ChangeHeader(byte[] frame, Action<JsonObject> change) => ReplaceHeader(frame, text =>
    {
        var header = JsonNode.Parse(text)!.AsObject();
        change(header);
        return header.ToJsonString();
    });
    private static byte[] ReplaceHeader(byte[] frame, Func<string, string> change)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(frame);
        var header = Encoding.UTF8.GetBytes(change(Encoding.UTF8.GetString(frame, 4, length)));
        var result = new byte[4 + header.Length + frame.Length - 4 - length];
        BinaryPrimitives.WriteInt32BigEndian(result, header.Length);
        header.CopyTo(result, 4);
        frame.AsSpan(4 + length).CopyTo(result.AsSpan(4 + header.Length));
        return result;
    }
    private static void Rebind(JsonObject header) => header["requestDigest"] = Sha256Digest.FromBytes(
        JsonSerializer.SerializeToUtf8Bytes(header["body"], new JsonSerializerOptions(JsonSerializerDefaults.Web))).Value;

    private sealed class MockRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('a', 64);
        public int Calls { get; private set; }
        public byte[]? LastResponse { get; private set; }
        public Func<CancellationToken, Task>? Before { get; set; }
        public Func<byte[], byte[]>? Mutate { get; init; }
        public WorkerProcessFailure Failure { get; init; }
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Calls++;
            if (Before is { } before) await before(cancellationToken);
            if (Failure != WorkerProcessFailure.None) return new([], Failure);
            var invocation = XmlComparisonProtocol.DecodeRequest(requestFrame.ToArray());
            var response = XmlComparisonProtocol.Compare(invocation, cancellationToken);
            LastResponse = response;
            return new(Mutate is { } mutate ? mutate(response) : response, WorkerProcessFailure.None);
        }
    }
}
