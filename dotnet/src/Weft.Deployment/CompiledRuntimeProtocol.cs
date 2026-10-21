using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Deployment;

/// <summary>Internal byte protocol; an HTTP caller cannot supply a DLL, assembly path or entrypoint.</summary>
public static class CompiledRuntimeProtocol
{
    public const string Protocol = "weft.ferrule-compiled-json.v1";
    // 32 parameter values of 4096 UTF-8 bytes can expand sixfold under JSON escaping.
    public const int MaximumHeaderBytes = 1024 * 1024;
    public const int MaximumIntentBytes = 1024 * 1024;
    public const int MaximumRequestBytes = MaximumHeaderBytes + MaximumIntentBytes + BuildWorkerProtocol.MaximumSourceBytes + PreviewPolicy.MaximumInputBytes + 128;
    public const int MaximumResponseBytes = MaximumHeaderBytes + PreviewPolicy.MaximumOutputBytes + 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public sealed record Document(string? Name, int ByteCount, string Sha256);
    public sealed record Parameter(string Name, string Kind, string Value);
    public sealed record Replay(string Target, string? TargetName, string CurrentDateTime, PreviewLimits Limits,
        Document[] Inputs, Parameter[] Parameters)
    {
        public override string ToString() => nameof(Replay);
    }
    public sealed record Request(string ProtocolVersion, string RequestId, Guid BuildId, string IntentDigest,
        string ArtifactDigest, int ArtifactBytes, string ReplayDigest, Replay Replay)
    {
        public override string ToString() => nameof(Request);
    }
    public sealed record Response(string ProtocolVersion, string RequestId, Guid BuildId, string IntentDigest,
        string ArtifactDigest, string ReplayDigest, string Status, Document[] Outputs);
    public sealed record Invocation(Request Header, BuildIntent Intent, BuildBytes Artifact, RegressionReplay Replay)
    {
        public override string ToString() => nameof(Invocation);
    }
    public sealed record Result(Response Header, RegressionOutputSet? Outputs)
    {
        public override string ToString() => nameof(Result);
    }

    public static Request For(BuildArtifactReference artifact, RegressionReplay replay)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(replay);
        var body = Describe(replay);
        return new(Protocol, Guid.NewGuid().ToString("D"), artifact.Intent.BuildId, artifact.Intent.Fingerprint.Value,
            artifact.ContentDigest.Value, checked((int)artifact.ByteCount), Digest(body), body);
    }

    public static byte[] EncodeRequest(Request request, BuildIntent intent, BuildBytes artifact, RegressionReplay replay)
    {
        Validate(request, intent);
        if (artifact.Length != request.ArtifactBytes || artifact.Digest.Value != request.ArtifactDigest || Digest(Describe(replay)) != request.ReplayDigest)
            throw Invalid();
        using var output = new MemoryStream();
        Write(output, JsonSerializer.SerializeToUtf8Bytes(request, Json), MaximumHeaderBytes);
        Write(output, intent.CopyCanonicalBytes(), MaximumIntentBytes);
        Write(output, artifact.Copy(), checked((int)intent.Profile.MaximumArtifactBytes));
        Write(output, replay.Primary.CopyBytes(), replay.Limits.MaximumInputBytes);
        foreach (var input in replay.NamedInputs) Write(output, input.CopyBytes(), replay.Limits.MaximumInputBytes);
        if (output.Length > MaximumRequestBytes) throw Invalid();
        return output.ToArray();
    }

    public static Invocation DecodeRequest(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumRequestBytes) throw Invalid();
        var offset = 0;
        var header = ReadJson<Request>(Read(bytes, ref offset, MaximumHeaderBytes));
        var intent = BuildIntentCodec.Decode(Read(bytes, ref offset, MaximumIntentBytes));
        Validate(header, intent);
        var artifactBytes = Read(bytes, ref offset, header.ArtifactBytes);
        if (artifactBytes.Length != header.ArtifactBytes || Sha256Digest.FromBytes(artifactBytes).Value != header.ArtifactDigest) throw Invalid();
        var inputs = new List<PreviewInputDocument>();
        var remaining = header.Replay.Limits.MaximumInputBytes;
        foreach (var input in header.Replay.Inputs)
        {
            if (input.ByteCount > remaining) throw Invalid();
            var value = Read(bytes, ref offset, input.ByteCount);
            if (value.Length != input.ByteCount || Sha256Digest.FromBytes(value).Value != input.Sha256) throw Invalid();
            inputs.Add(new(input.Name, value));
            remaining -= value.Length;
        }
        if (offset != bytes.Length) throw Invalid();
        var replay = new RegressionReplay(Target(header.Replay.Target, header.Replay.TargetName), header.Replay.CurrentDateTime,
            header.Replay.Limits, inputs[0], inputs.Skip(1).ToArray(), header.Replay.Parameters.Select(value =>
                new PreviewParameter(value.Name, Kind(value.Kind), value.Value)).ToArray());
        if (Digest(Describe(replay)) != header.ReplayDigest) throw Invalid();
        return new(header, intent, new(artifactBytes, intent.Profile.MaximumArtifactBytes), replay);
    }

    public static byte[] EncodeResponse(Invocation invocation, string status, RegressionOutputSet? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var documents = outputs?.Outputs.Select(value => new Document(value.Key.Name, value.ByteCount, value.Digest.Value)).ToArray() ?? [];
        var request = invocation.Header;
        var header = new Response(Protocol, request.RequestId, request.BuildId, request.IntentDigest, request.ArtifactDigest,
            request.ReplayDigest, status, documents);
        ValidateResponse(header, request, invocation.Intent);
        using var buffer = new MemoryStream();
        Write(buffer, JsonSerializer.SerializeToUtf8Bytes(header, Json), MaximumHeaderBytes);
        if (outputs is not null) foreach (var output in outputs.Outputs) Write(buffer, output.CopyBytes(), invocation.Replay.Limits.MaximumOutputBytes);
        if (buffer.Length > MaximumResponseBytes) throw Invalid();
        return buffer.ToArray();
    }

    public static Result DecodeResponse(byte[] bytes, Request request, BuildIntent intent)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Validate(request, intent);
        if (bytes.Length > MaximumResponseBytes) throw Invalid();
        var offset = 0;
        var header = ReadJson<Response>(Read(bytes, ref offset, MaximumHeaderBytes));
        ValidateResponse(header, request, intent);
        var values = new List<RegressionOutput>();
        var remaining = request.Replay.Limits.MaximumOutputBytes;
        foreach (var document in header.Outputs)
        {
            if (document.ByteCount > remaining) throw Invalid();
            var value = Read(bytes, ref offset, document.ByteCount);
            if (value.Length != document.ByteCount || Sha256Digest.FromBytes(value).Value != document.Sha256) throw Invalid();
            remaining -= value.Length;
            values.Add(RegressionOutput.Capture(document.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(document.Name), value));
        }
        if (offset != bytes.Length) throw Invalid();
        return new(header, header.Status == "succeeded" ? RegressionOutputSet.Capture(values) : null);
    }

    private static void Validate(Request request, BuildIntent intent)
    {
        if (request is null || intent is null || request.ProtocolVersion != Protocol ||
            !Guid.TryParseExact(request.RequestId, "D", out var id) || id == Guid.Empty || id.ToString("D") != request.RequestId ||
            request.BuildId != intent.BuildId || request.IntentDigest != intent.Fingerprint.Value ||
            request.ArtifactBytes is < 1 or > BuildWorkerProtocol.MaximumSourceBytes || request.ArtifactBytes > intent.Profile.MaximumArtifactBytes ||
            !Hash(request.ArtifactDigest) || !Hash(request.ReplayDigest) || request.Replay is null || request.Replay.Limits is null ||
            request.Replay.Inputs is null || request.Replay.Inputs.Length is < 1 or > PreviewPolicy.MaximumInputDocuments ||
            request.Replay.Parameters is null || request.Replay.Parameters.Length > PreviewPolicy.MaximumParameters ||
            Digest(request.Replay) != request.ReplayDigest) throw Invalid();
        var limits = request.Replay.Limits;
        if (limits.MaximumInputBytes is < 1 or > PreviewPolicy.MaximumInputBytes || limits.MaximumOutputBytes is < 1 or > PreviewPolicy.MaximumOutputBytes ||
            limits.MaximumArtifacts is < 1 or > PreviewPolicy.MaximumArtifacts || limits.MaximumTextBytes is < PreviewPolicy.MinimumTextBytes or > PreviewPolicy.MaximumTextBytes)
            throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var remaining = limits.MaximumInputBytes;
        for (var i = 0; i < request.Replay.Inputs.Length; i++)
        {
            var input = request.Replay.Inputs[i];
            if (input is null || input.ByteCount is < 1 || input.ByteCount > remaining || !Hash(input.Sha256) ||
                (i == 0 ? input.Name is not null : !Name(input.Name) || !names.Add(input.Name!))) throw Invalid();
            remaining -= input.ByteCount;
        }
        _ = Target(request.Replay.Target, request.Replay.TargetName);
        names.Clear();
        foreach (var parameter in request.Replay.Parameters)
        {
            if (parameter is null || !Name(parameter.Name) || !names.Add(parameter.Name) || parameter.Value is null ||
                System.Text.Encoding.UTF8.GetByteCount(parameter.Value) > PreviewPolicy.MaximumParameterTextBytes) throw Invalid();
            _ = Kind(parameter.Kind);
        }
    }

    private static void ValidateResponse(Response response, Request request, BuildIntent intent)
    {
        if (response is null || response.ProtocolVersion != Protocol || response.RequestId != request.RequestId || response.BuildId != request.BuildId ||
            response.IntentDigest != request.IntentDigest || response.ArtifactDigest != request.ArtifactDigest || response.ReplayDigest != request.ReplayDigest ||
            response.Status is not ("succeeded" or "invalidInput" or "invalidParameter" or "unsupported" or "executionFailed" or "resourceLimit" or "invalidArtifact") ||
            response.Outputs is null || response.Outputs.Length > request.Replay.Limits.MaximumArtifacts) throw Invalid();
        if (response.Status != "succeeded") { if (response.Outputs.Length != 0) throw Invalid(); return; }
        var expected = intent.Boundaries.Where(value => value.Kind is BuildBoundaryKind.PrimaryOutput or BuildBoundaryKind.NamedOutput)
            .Where(value => request.Replay.Target == "all" || (request.Replay.Target == "primary" ? value.Kind == BuildBoundaryKind.PrimaryOutput : value.Name == request.Replay.TargetName))
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0 || response.Outputs.Length != expected.Count) throw Invalid();
        var remaining = request.Replay.Limits.MaximumOutputBytes;
        foreach (var document in response.Outputs)
        {
            if (document is null || document.ByteCount < 1 || document.ByteCount > remaining ||
                !Hash(document.Sha256) || !expected.Remove(document.Name)) throw Invalid();
            remaining -= document.ByteCount;
        }
        if (expected.Count != 0) throw Invalid();
    }

    private static Replay Describe(RegressionReplay replay) => new(replay.Target.Kind switch
    {
        PreviewTargetKind.All => "all",
        PreviewTargetKind.Primary => "primary",
        PreviewTargetKind.Named => "named",
        _ => throw Invalid()
    }, replay.Target.Name, replay.CurrentDateTime, replay.Limits,
        new[] { replay.Primary }.Concat(replay.NamedInputs).Select(value => new Document(value.Name, value.ByteCount, value.Digest.Value)).ToArray(),
        replay.Parameters.Select(value => new Parameter(value.Name, value.Kind switch
        {
            PreviewParameterKind.String => "string",
            PreviewParameterKind.Integer => "integer",
            PreviewParameterKind.Float => "float",
            PreviewParameterKind.Boolean => "boolean",
            _ => throw Invalid()
        }, value.Value)).ToArray());
    private static PreviewTargetSelection Target(string kind, string? name) => kind switch
    {
        "all" when name is null => PreviewTargetSelection.All,
        "primary" when name is null => PreviewTargetSelection.Primary,
        "named" when Name(name) => PreviewTargetSelection.Named(name!),
        _ => throw Invalid(),
    };
    private static PreviewParameterKind Kind(string kind) => kind switch
    {
        "string" => PreviewParameterKind.String,
        "integer" => PreviewParameterKind.Integer,
        "float" => PreviewParameterKind.Float,
        "boolean" => PreviewParameterKind.Boolean,
        _ => throw Invalid(),
    };
    private static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl) && System.Text.Encoding.UTF8.GetByteCount(value) <= 256;
    private static bool Hash(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(Replay replay) => Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(replay, Json)).Value;
    private static T ReadJson<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        Keys(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid();
    }
    private static void Keys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) throw Invalid(); Keys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Keys(item);
    }
    private static byte[] Read(byte[] bytes, ref int offset, int maximum)
    {
        if (offset > bytes.Length - 4) throw Invalid();
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4;
        if (length < 1 || length > maximum || length > bytes.Length - offset) throw Invalid();
        var result = bytes.AsSpan(offset, length).ToArray(); offset += length; return result;
    }
    private static void Write(Stream stream, ReadOnlySpan<byte> value, int maximum)
    {
        if (value.Length < 1 || value.Length > maximum) throw Invalid();
        Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, value.Length); stream.Write(prefix); stream.Write(value);
    }
    private static InvalidOperationException Invalid() => new("Compiled runtime protocol refused.");
}
