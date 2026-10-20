using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Operations;
using Weft.Domain;

namespace Weft.Tests;

public sealed class RegressionCaseEnvelopeTests
{
    private static readonly Guid CaseId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly PrincipalId Owner = new(Guid.Parse("20000000-0000-4000-8000-000000000001"));
    private static readonly RevisionScope Scope = new(Guid.Parse("30000000-0000-4000-8000-000000000001"),
        Guid.Parse("40000000-0000-4000-8000-000000000001"), Guid.Parse("50000000-0000-4000-8000-000000000001"),
        Guid.Parse("60000000-0000-4000-8000-000000000001"));
    private static readonly Guid Other = Guid.Parse("70000000-0000-4000-8000-000000000001");
    private static readonly byte[] Native = [1, 2, 3];
    private const string Clock = "2026-10-20T12:34:56.123456789+01:00";
    private readonly RegressionCaseEnvelopeCodec codec = new();

    [Fact]
    public void Exact_private_bytes_parameters_and_provenance_round_trip()
    {
        var value = Case();
        var encoded = codec.Encode(value);
        var stored = encoded.CopyBytes();
        var restored = codec.Decode(encoded.Reference, stored);
        Assert.Equal(value.CaseId, restored.CaseId);
        Assert.Equal(value.Owner, restored.Owner);
        Assert.Equal(value.Name, restored.Name);
        Assert.Equal(value.Revision, restored.Revision);
        Assert.Equal(value.Version, restored.Version);
        Assert.Equal(value.NativeDigest, restored.NativeDigest);
        Assert.Equal(value.Runtime, restored.Runtime);
        Assert.Equal(Clock, restored.Replay.CurrentDateTime);
        Assert.Equal(value.Replay.Limits, restored.Replay.Limits);
        Assert.Equal(value.Replay.Primary.CopyBytes(), restored.Replay.Primary.CopyBytes());
        Assert.Equal(value.Replay.Parameters, restored.Replay.Parameters);
        Assert.Equal(value.Replay.NamedInputs.Select(input => input.Name), restored.Replay.NamedInputs.Select(input => input.Name));
        Assert.Equal(encoded.Reference.ContentDigest, Sha256Digest.FromBytes(stored));
        Assert.Equal(stored, codec.Encode(restored).CopyBytes());
        Assert.Equal(RegressionComparisonOutcome.Passed, new RegressionComparisonService().Compare(value.Expected,
            new(OperationStatusV1.Succeeded, restored.Expected), restored.ComparisonPolicy).Outcome);
    }

    [Fact]
    public void Reordered_caller_collections_have_the_same_stored_identity()
    {
        var original = Case();
        var replay = Replay(named: original.Replay.NamedInputs.Reverse().ToArray(), parameters: original.Replay.Parameters.Reverse().ToArray());
        var reversed = Case(replay: replay, expected: RegressionOutputSet.Capture(original.Expected.Outputs.Reverse().ToArray()));
        Assert.Equal(codec.Encode(original).Reference, codec.Encode(reversed).Reference);
        Assert.Equal(codec.Encode(original).CopyBytes(), codec.Encode(reversed).CopyBytes());
    }

    [Theory]
    [InlineData("{\"n\":1}", "{ \"n\" : 1.0 }")]
    [InlineData("{\"n\":1.0}", "{\"n\":1e0}")]
    [InlineData("\"é\"", "\"e\u0301\"")]
    [InlineData("line\n", "line\r\n")]
    public void Semantically_equivalent_or_invalid_input_is_never_rewritten(string first, string second)
    {
        var left = codec.Encode(Case(replay: Replay(primary: Input(null, first))));
        var right = codec.Encode(Case(replay: Replay(primary: Input(null, second))));
        Assert.NotEqual(left.Reference.ContentDigest, right.Reference.ContentDigest);
        Assert.Equal(Encoding.UTF8.GetBytes(second), codec.Decode(right.Reference, right.CopyBytes()).Replay.Primary.CopyBytes());
    }

    [Fact]
    public void Non_utf8_payload_bytes_and_empty_expected_artifacts_remain_exact()
    {
        var raw = new byte[] { 255, 0, 128 };
        var value = Case(replay: Replay(primary: new(null, raw)), expected: RegressionOutputSet.Capture([
            RegressionOutput.Capture(RegressionOutputKey.Primary, [])]));
        var encoded = codec.Encode(value);
        var restored = codec.Decode(encoded.Reference, encoded.CopyBytes());
        Assert.Equal(raw, restored.Replay.Primary.CopyBytes());
        Assert.Empty(Assert.Single(restored.Expected.Outputs).CopyBytes());
    }

    [Fact]
    public void Missing_baseline_remains_not_evaluated_and_cannot_be_promoted_by_storage()
    {
        var encoded = codec.Encode(Case(expected: RegressionOutputSet.Capture([])));
        var restored = codec.Decode(encoded.Reference, encoded.CopyBytes());
        Assert.Equal(RegressionNotEvaluatedReason.MissingBaseline, new RegressionComparisonService().Compare(restored.Expected,
            new(OperationStatusV1.Succeeded, RegressionOutputSet.Capture([])), restored.ComparisonPolicy).NotEvaluatedReason);
    }

    [Fact]
    public void Constructors_and_decode_defensively_own_mutable_bytes_and_collections()
    {
        var raw = Encoding.UTF8.GetBytes("private-input");
        var primary = new PreviewInputDocument(null, raw);
        var names = new List<PreviewInputDocument> { Input("private-name", "private-data") };
        var parameters = new List<PreviewParameter> { new("private-param", PreviewParameterKind.String, "private-value") };
        var value = Case(replay: Replay(primary: primary, named: names, parameters: parameters));
        var encoded = codec.Encode(value);
        raw[0] = 0;
        names.Clear();
        parameters.Clear();
        var bytes = encoded.CopyBytes();
        var restored = codec.Decode(encoded.Reference, bytes);
        Array.Fill(bytes, (byte)0);
        Array.Fill(encoded.CopyBytes(), (byte)0);
        primary.CopyBytes()[0] = 0;
        Assert.Equal("private-input", Encoding.UTF8.GetString(restored.Replay.Primary.CopyBytes()));
        Assert.Single(restored.Replay.NamedInputs);
        Assert.Single(restored.Replay.Parameters);
        Assert.Throws<NotSupportedException>(() => ((IList<PreviewInputDocument>)value.Replay.NamedInputs).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PreviewParameter>)value.Replay.Parameters).Clear());
        foreach (var text in new[] { restored.ToString(), restored.Replay.ToString(), restored.Runtime.ToString(),
            encoded.ToString(), encoded.Reference.ToString(), codec.ToString() })
            Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_stored_reference_identity_is_checked_even_for_valid_bytes()
    {
        var encoded = codec.Encode(Case());
        var r = encoded.Reference;
        foreach (var reference in new[]
        {
            Ref(r, caseId: Other), Ref(r, version: 2), Ref(r, owner: new(Other)),
            Ref(r, scope: new(Other, Scope.ProjectId, Scope.MappingId, Scope.RevisionId)),
            Ref(r, scope: new(Scope.OrganizationId, Other, Scope.MappingId, Scope.RevisionId)),
            Ref(r, scope: new(Scope.OrganizationId, Scope.ProjectId, Other, Scope.RevisionId)),
            Ref(r, scope: new(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Other)),
            Ref(r, nativeDigest: Sha256Digest.FromBytes([4])), Ref(r, contentDigest: Sha256Digest.FromBytes([5])),
            Ref(r, byteCount: r.ByteCount + 1)
        })
            Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(reference, encoded.CopyBytes()));
    }

    [Fact]
    public void Replay_requests_require_exact_native_revision_and_runtime_identity()
    {
        var value = Case();
        var artifact = NativeArtifact.Admit(Scope, Native, 10);
        var request = value.CreatePreviewRequest(artifact, Runtime());
        Assert.Same(artifact, request.Artifact);
        Assert.Same(value.Replay.Primary, request.Primary);
        Assert.Equal(Clock, request.CurrentDateTime);
        foreach (var scope in new[] { new RevisionScope(Other, Scope.ProjectId, Scope.MappingId, Scope.RevisionId),
            new(Scope.OrganizationId, Other, Scope.MappingId, Scope.RevisionId),
            new(Scope.OrganizationId, Scope.ProjectId, Other, Scope.RevisionId),
            new(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, Other) })
            Failure(RegressionCaseFailure.InvalidInput, () => value.CreatePreviewRequest(NativeArtifact.Admit(scope, Native, 10), Runtime()));
        Failure(RegressionCaseFailure.InvalidInput, () => value.CreatePreviewRequest(NativeArtifact.Admit(Scope, [9], 10), Runtime()));
        foreach (var runtime in new[] { Runtime(backend: "other"), Runtime(engine: "other"), Runtime(build: "other"), Runtime(adapter: "other"), Runtime(profile: "other") })
            Failure(RegressionCaseFailure.InvalidInput, () => value.CreatePreviewRequest(artifact, runtime));
    }

    [Fact]
    public void All_execution_fields_change_the_envelope_digest()
    {
        var value = Case();
        var original = codec.Encode(value).Reference.ContentDigest;
        var changed = new[]
        {
            Case(version: 2), Case(name: "other"), Case(runtime: Runtime(backend: "other")),
            Case(runtime: Runtime(engine: "other")), Case(runtime: Runtime(build: "other")), Case(runtime: Runtime(adapter: "other")),
            Case(replay: Replay(clock: "2026-10-20T11:34:56.123456789Z")),
            Case(replay: Replay(limits: PreviewLimits.Default with { MaximumInputBytes = PreviewPolicy.MaximumInputBytes - 1 })),
            Case(replay: Replay(limits: PreviewLimits.Default with { MaximumOutputBytes = PreviewPolicy.MaximumOutputBytes - 1 })),
            Case(replay: Replay(limits: PreviewLimits.Default with { MaximumArtifacts = PreviewPolicy.MaximumArtifacts - 1 })),
            Case(replay: Replay(limits: PreviewLimits.Default with { MaximumTextBytes = PreviewPolicy.MaximumTextBytes - 1 })),
            Case(replay: Replay(primary: Input(null, "different"))), Case(replay: Replay(named: [Input("changed", "{}") ])),
            Case(replay: Replay(parameters: [new("n", PreviewParameterKind.Integer, "1")])),
            Case(replay: Replay(target: PreviewTargetSelection.Primary), expected: RegressionOutputSet.Capture([Output(null, "ok")])),
            Case(expected: RegressionOutputSet.Capture([Output(null, "changed")]))
        };
        foreach (var item in changed) Assert.NotEqual(original, codec.Encode(item).Reference.ContentDigest);
    }

    [Theory]
    [InlineData("2026-10-20T12:34:56Z")]
    [InlineData("2026-10-20T12:34:56+00:00")]
    [InlineData("2026-10-20T12:34:56.123456789-07:00")]
    [InlineData("2024-02-29T12:34:56.0+14:00")]
    public void Fixed_clock_preserves_explicit_offset_and_fractional_precision(string clock)
    {
        var encoded = codec.Encode(Case(replay: Replay(clock: clock)));
        Assert.Equal(clock, codec.Decode(encoded.Reference, encoded.CopyBytes()).Replay.CurrentDateTime);
    }

    [Theory]
    [InlineData("2026-10-20T12:34:56")]
    [InlineData("2026-10-20 12:34:56Z")]
    [InlineData("2026-10-20T12:34:60Z")]
    [InlineData("2026-02-30T12:34:56Z")]
    [InlineData("2026-10-20T12:34:56+14:01")]
    [InlineData("2026-10-20T12:34:56.1234567890Z")]
    [InlineData("2026-10-20T12:34:56Z ")]
    public void Missing_or_invalid_evaluation_clock_is_rejected(string clock) =>
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(clock: clock));

    [Fact]
    public void Typed_parameter_values_are_not_coerced_or_silently_repaired()
    {
        var values = new[] { new PreviewParameter("integer", PreviewParameterKind.Integer, "9007199254740993"),
            new("float", PreviewParameterKind.Float, "1.00e+2"), new("boolean", PreviewParameterKind.Boolean, "false"),
            new("string", PreviewParameterKind.String, "\0\n"), new("invalid", PreviewParameterKind.Integer, "not-an-integer") };
        var encoded = codec.Encode(Case(replay: Replay(parameters: values)));
        var restored = codec.Decode(encoded.Reference, encoded.CopyBytes());
        Assert.Equal(values.OrderBy(item => item.Name, StringComparer.Ordinal), restored.Replay.Parameters);
    }

    [Theory]
    [InlineData("same", "Same")]
    [InlineData("é", "e\u0301")]
    [InlineData("a/b", "a\\b")]
    public void Named_input_output_and_parameter_identities_remain_ordinal(string first, string second)
    {
        var left = Case(replay: Replay(named: [Input(first, "{}")], parameters: [new(first, PreviewParameterKind.String, "v")]),
            expected: RegressionOutputSet.Capture([Output(first, "x")]));
        var right = Case(replay: Replay(named: [Input(second, "{}")], parameters: [new(second, PreviewParameterKind.String, "v")]),
            expected: RegressionOutputSet.Capture([Output(second, "x")]));
        Assert.NotEqual(codec.Encode(left).Reference.ContentDigest, codec.Encode(right).Reference.ContentDigest);
    }

    [Fact]
    public void Target_identity_does_not_confuse_primary_with_named_primary()
    {
        var named = Case(replay: Replay(target: PreviewTargetSelection.Named("primary")), expected: RegressionOutputSet.Capture([Output("primary", "x")]));
        var primary = Case(replay: Replay(target: PreviewTargetSelection.Primary), expected: RegressionOutputSet.Capture([Output(null, "x")]));
        Assert.NotEqual(codec.Encode(named).Reference.ContentDigest, codec.Encode(primary).Reference.ContentDigest);
        Failure(RegressionCaseFailure.InvalidInput, () => Case(replay: primary.Replay, expected: named.Expected));
        Failure(RegressionCaseFailure.InvalidInput, () => Case(replay: named.Replay, expected: primary.Expected));
    }

    [Fact]
    public void Input_parameter_and_expected_output_bounds_are_checked_before_encoding()
    {
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(primary: Input("not-primary", "{}")));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(primary: new(null, [])));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(named: [Input("a", "{}"), Input("a", "{}")]));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(named: [Input(null, "{}")]));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(parameters: [new("a", PreviewParameterKind.String, "x"), new("a", PreviewParameterKind.String, "y")]));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(parameters: [new("a", (PreviewParameterKind)100, "x")]));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(parameters: [new("a", PreviewParameterKind.String, "\ud800")]));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(parameters: [new("a", PreviewParameterKind.String, new('x', PreviewPolicy.MaximumParameterTextBytes + 1))]));
        Failure(RegressionCaseFailure.InvalidInput, () => Replay(named: [Input(new('é', 129), "{}")]));
        Failure(RegressionCaseFailure.ResourceLimit, () => Replay(named: Enumerable.Range(0, PreviewPolicy.MaximumInputDocuments)
            .Select(index => Input(index.ToString(), "{}")).ToArray()));
        Failure(RegressionCaseFailure.ResourceLimit, () => Replay(parameters: Enumerable.Range(0, PreviewPolicy.MaximumParameters + 1)
            .Select(index => new PreviewParameter(index.ToString(), PreviewParameterKind.String, "x")).ToArray()));
        Failure(RegressionCaseFailure.ResourceLimit, () => Replay(primary: Input(null, "abc"), named: [],
            limits: PreviewLimits.Default with { MaximumInputBytes = 2 }));
        Failure(RegressionCaseFailure.ResourceLimit, () => Case(expected: RegressionOutputSet.Capture([Output(null, "abc")]),
            replay: Replay(limits: PreviewLimits.Default with { MaximumOutputBytes = 2 })));
        Failure(RegressionCaseFailure.ResourceLimit, () => Case(replay: Replay(limits: PreviewLimits.Default with { MaximumArtifacts = 1 })));
        Failure(RegressionCaseFailure.InvalidInput, () => Case(runtime: Runtime(profile: "unknown")));
        Failure(RegressionCaseFailure.InvalidInput, () => Case(policy: "unknown"));
    }

    [Fact]
    public void Exact_maximum_frame_budgets_round_trip_without_base64_expansion()
    {
        var inputs = new byte[PreviewPolicy.MaximumInputBytes];
        var outputs = new byte[PreviewPolicy.MaximumOutputBytes];
        inputs[^1] = 123;
        outputs[^1] = 255;
        var value = Case(replay: Replay(primary: new(null, inputs), named: []),
            expected: RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, outputs)]));
        var encoded = codec.Encode(value);
        Assert.InRange(encoded.Reference.ByteCount, inputs.Length + outputs.Length, inputs.Length + outputs.Length + RegressionCaseEnvelopeCodec.MaximumHeaderBytes);
        var restored = codec.Decode(encoded.Reference, encoded.CopyBytes());
        Assert.Equal(inputs, restored.Replay.Primary.CopyBytes());
        Assert.Equal(outputs, restored.Expected.Outputs[0].CopyBytes());
        Failure(RegressionCaseFailure.ResourceLimit, () => Replay(primary: new(null, new byte[PreviewPolicy.MaximumInputBytes + 1]), named: []));
    }

    [Fact]
    public void Maximum_collection_counts_are_admitted_and_escaped_header_growth_is_bounded()
    {
        var named = Enumerable.Range(0, PreviewPolicy.MaximumInputDocuments - 1).Select(index => Input($"input-{index}", "{}")).ToArray();
        var parameters = Enumerable.Range(0, PreviewPolicy.MaximumParameters).Select(index =>
            new PreviewParameter($"parameter-{index}", PreviewParameterKind.String, "x")).ToArray();
        var outputs = RegressionOutputSet.Capture(Enumerable.Range(0, PreviewPolicy.MaximumArtifacts)
            .Select(index => Output($"output-{index}", "")).ToArray());
        var encoded = codec.Encode(Case(replay: Replay(named: named, parameters: parameters), expected: outputs));
        var restored = codec.Decode(encoded.Reference, encoded.CopyBytes());
        Assert.Equal(PreviewPolicy.MaximumInputDocuments - 1, restored.Replay.NamedInputs.Count);
        Assert.Equal(PreviewPolicy.MaximumParameters, restored.Replay.Parameters.Count);
        Assert.Equal(PreviewPolicy.MaximumArtifacts, restored.Expected.Outputs.Count);
        var escaped = parameters.Select(item => item with { Value = new string('\0', PreviewPolicy.MaximumParameterTextBytes) }).ToArray();
        Failure(RegressionCaseFailure.ResourceLimit, () => codec.Encode(Case(replay: Replay(parameters: escaped))));
    }

    [Fact]
    public void Invalid_utf8_header_and_excessive_header_length_are_integrity_failures()
    {
        var encoded = codec.Encode(Case());
        var invalid = encoded.CopyBytes();
        var marker = Encoding.UTF8.GetBytes("private-fixture-name");
        var index = invalid.AsSpan().IndexOf(marker);
        Assert.True(index > 4);
        invalid[index] = 255;
        Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(Rehash(encoded.Reference, invalid), invalid));
        var oversized = encoded.CopyBytes();
        BinaryPrimitives.WriteInt32BigEndian(oversized, RegressionCaseEnvelopeCodec.MaximumHeaderBytes + 1);
        Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(Rehash(encoded.Reference, oversized), oversized));
    }

    [Fact]
    public void Corrupt_truncated_extra_and_invalid_length_frames_are_rejected()
    {
        var encoded = codec.Encode(Case());
        var source = encoded.CopyBytes();
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(source);
        var frameStart = headerLength + 4;
        foreach (var position in new[] { 0, frameStart, frameStart + 4, source.Length - 1 })
        {
            var changed = (byte[])source.Clone();
            changed[position] ^= 1;
            Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(encoded.Reference, changed));
            Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(Rehash(encoded.Reference, changed), changed));
        }
        foreach (var changed in new[] { source[..^1], source.Concat(new byte[] { 0 }).ToArray(), new byte[] { 0, 0, 0 },
            new byte[] { 255, 255, 255, 255 } })
            Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(Rehash(encoded.Reference, changed), changed));
        var oversized = new byte[RegressionCaseEnvelopeCodec.MaximumEnvelopeBytes + 1];
        Failure(RegressionCaseFailure.ResourceLimit, () => codec.Decode(encoded.Reference, oversized));
    }

    public static IEnumerable<object[]> InvalidHeaders()
    {
        yield return ["unknown top-level field", new Action<JsonObject>(root => root["unknown"] = 1)];
        yield return ["missing field", new Action<JsonObject>(root => root.Remove("owner"))];
        yield return ["unknown schema", new Action<JsonObject>(root => root["schema"] = "weft.regression-case.v2")];
        yield return ["unknown policy", new Action<JsonObject>(root => root["comparisonPolicy"] = "unknown")];
        yield return ["unknown profile", new Action<JsonObject>(root => root["runtime"]!["profile"] = "unknown")];
        yield return ["unknown parameter kind", new Action<JsonObject>(root => root["parameters"]![0]!["kind"] = "number")];
        yield return ["unknown target", new Action<JsonObject>(root => root["target"]!["kind"] = "unknown")];
        yield return ["target mismatch", new Action<JsonObject>(root => root["target"]!["name"] = "unexpected")];
        yield return ["unknown nested field", new Action<JsonObject>(root => root["revision"]!["extra"] = true)];
        yield return ["negative length", new Action<JsonObject>(root => root["inputs"]![0]!["byteCount"] = -1)];
        yield return ["digest mismatch", new Action<JsonObject>(root => root["inputs"]![0]!["sha256"] = new string('0', 64))];
        yield return ["uppercase digest", new Action<JsonObject>(root => root["nativeDigest"] = root["nativeDigest"]!.GetValue<string>().ToUpperInvariant())];
        yield return ["uppercase GUID", new Action<JsonObject>(root => root["caseId"] = "ABCDEF00-0000-4000-8000-000000000001")];
        yield return ["string version", new Action<JsonObject>(root => root["version"] = "1")];
        yield return ["null name", new Action<JsonObject>(root => root["name"] = null)];
        yield return ["empty inputs", new Action<JsonObject>(root => root["inputs"] = new JsonArray())];
        yield return ["null parameter value", new Action<JsonObject>(root => root["parameters"]![0]!["value"] = null)];
        yield return ["duplicate input name", new Action<JsonObject>(root => root["inputs"]![2]!["name"] = root["inputs"]![1]!["name"]!.GetValue<string>())];
        yield return ["duplicate parameter name", new Action<JsonObject>(root => root["parameters"]![1]!["name"] = root["parameters"]![0]!["name"]!.GetValue<string>())];
        yield return ["duplicate output name", new Action<JsonObject>(root => root["expected"]![1]!["name"] = null)];
    }

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public void Strict_stored_schema_rejects_invalid_shapes_even_with_a_matching_envelope_hash(string _, Action<JsonObject> mutate)
    {
        var encoded = codec.Encode(Case());
        var changed = ChangeHeader(encoded.CopyBytes(), root => { mutate(root); return root; });
        Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(Rehash(encoded.Reference, changed), changed));
    }

    [Fact]
    public void Duplicate_properties_and_noncanonical_integer_tokens_are_rejected()
    {
        var encoded = codec.Encode(Case());
        foreach (var edit in new Func<string, string>[] {
            text => text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            text => text.Replace("\"version\":1", "\"version\":1.0", StringComparison.Ordinal),
            text => text.Replace("\"version\":1", "\"version\":1e0", StringComparison.Ordinal),
            text => text.Replace("\"kind\":\"all\"", "\"kind\":\"all\",\"kind\":\"all\"", StringComparison.Ordinal),
            text => text.Replace("\"name\":null", "\"name\":null,\"name\":null", StringComparison.Ordinal) })
        {
            var changed = ChangeHeaderText(encoded.CopyBytes(), edit);
            Failure(RegressionCaseFailure.StorageIntegrity, () => codec.Decode(Rehash(encoded.Reference, changed), changed));
        }
    }

    [Fact]
    public void Cancellation_and_privacy_are_preserved_for_all_public_operations()
    {
        var value = Case();
        var encoded = codec.Encode(value);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => codec.Encode(value, source.Token));
        Assert.Throws<OperationCanceledException>(() => codec.Decode(encoded.Reference, encoded.CopyBytes(), source.Token));
        Assert.Throws<OperationCanceledException>(() => value.CreatePreviewRequest(NativeArtifact.Admit(Scope, Native, 10), Runtime(), source.Token));
        Assert.Throws<OperationCanceledException>(() => Replay(token: source.Token));
        var changed = ChangeHeader(encoded.CopyBytes(), root => { root["owner"] = "private-secret-value"; return root; });
        var error = Assert.Throws<RegressionCaseException>(() => codec.Decode(Rehash(encoded.Reference, changed), changed));
        Assert.DoesNotContain("private-secret-value", error.ToString());
        Assert.Null(error.InnerException);
    }

    private static RegressionCaseVersion Case(RegressionReplay? replay = null, RegressionOutputSet? expected = null,
        long version = 1, string name = "private-fixture-name", RegressionRuntimeIdentity? runtime = null, string? policy = null) =>
        new(CaseId, version, Owner, name, Scope, Sha256Digest.FromBytes(Native), runtime ?? Runtime(), replay ?? Replay(),
            policy ?? RegressionComparisonService.ExactBytesPolicy, expected ?? RegressionOutputSet.Capture([Output(null, "ok"), Output("audit", "{}")]));

    private static RegressionRuntimeIdentity Runtime(string backend = "fixture", string engine = "engine-revision", string build = "backend-build",
        string adapter = "adapter-build", string profile = PreviewPolicy.Profile) => new(backend, engine, build, adapter, profile);

    private static RegressionReplay Replay(PreviewTargetSelection? target = null, string clock = Clock, PreviewLimits? limits = null,
        PreviewInputDocument? primary = null, IReadOnlyList<PreviewInputDocument>? named = null,
        IReadOnlyList<PreviewParameter>? parameters = null, CancellationToken token = default) =>
        new(target ?? PreviewTargetSelection.All, clock, limits ?? PreviewLimits.Default, primary ?? Input(null, "{ \"number\": 1.00 }"),
            named ?? [Input("z-last", "[]"), Input("a-first", "{}")], parameters ?? [new("z", PreviewParameterKind.Float, "1e0"),
                new("a", PreviewParameterKind.Integer, "9007199254740993")], token);

    private static PreviewInputDocument Input(string? name, string content) => new(name, Encoding.UTF8.GetBytes(content));
    private static RegressionOutput Output(string? name, string content) => RegressionOutput.Capture(name is null ? RegressionOutputKey.Primary :
        RegressionOutputKey.Named(name), Encoding.UTF8.GetBytes(content));

    private static RegressionCaseReference Ref(RegressionCaseReference reference, Guid? caseId = null, long? version = null,
        PrincipalId? owner = null, RevisionScope? scope = null, Sha256Digest? nativeDigest = null, Sha256Digest? contentDigest = null, int? byteCount = null) =>
        new(caseId ?? reference.CaseId, version ?? reference.Version, owner ?? reference.Owner, scope ?? reference.Revision,
            nativeDigest ?? reference.NativeDigest, contentDigest ?? reference.ContentDigest, byteCount ?? reference.ByteCount);

    private static RegressionCaseReference Rehash(RegressionCaseReference reference, byte[] bytes) =>
        Ref(reference, contentDigest: Sha256Digest.FromBytes(bytes), byteCount: bytes.Length);

    private static byte[] ChangeHeader(byte[] bytes, Func<JsonObject, JsonObject> edit) =>
        ChangeHeaderText(bytes, text => edit(JsonNode.Parse(text)!.AsObject()).ToJsonString());

    private static byte[] ChangeHeaderText(byte[] bytes, Func<string, string> edit)
    {
        var oldLength = BinaryPrimitives.ReadInt32BigEndian(bytes);
        var header = Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(bytes, 4, oldLength)));
        var changed = new byte[4 + header.Length + bytes.Length - 4 - oldLength];
        BinaryPrimitives.WriteInt32BigEndian(changed, header.Length);
        header.CopyTo(changed, 4);
        bytes.AsSpan(oldLength + 4).CopyTo(changed.AsSpan(header.Length + 4));
        return changed;
    }

    private static void Failure(RegressionCaseFailure expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<RegressionCaseException>(action).Failure);
}
