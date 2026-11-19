using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Internal byte protocol; an HTTP caller cannot supply a DLL, assembly path or entrypoint.</summary>
public static class CompiledRuntimeProtocol
{
    public const string Protocol = "braid.ferrule-compiled-json.v1";
    public const string DocumentProtocol = "braid.ferrule-compiled-document.v1";
    public const string CsvProtocol = "braid.ferrule-compiled-csv.v1";
    public const string XmlProtocol = "braid.ferrule-compiled-xml.v1";
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

    public sealed record Document(string? Name, int ByteCount, string Sha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MediaType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CsvDocumentDialect? CsvDialect = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] XmlDocumentMetadata? XmlMetadata = null);
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
        var body = Describe(replay, artifact.Intent);
        return new(ProtocolFor(artifact.Intent), Guid.NewGuid().ToString("D"), artifact.Intent.BuildId, artifact.Intent.Fingerprint.Value,
            artifact.ContentDigest.Value, checked((int)artifact.ByteCount), Digest(body), body);
    }

    public static byte[] EncodeRequest(Request request, BuildIntent intent, BuildBytes artifact, RegressionReplay replay)
    {
        Validate(request, intent);
        if (artifact.Length != request.ArtifactBytes || artifact.Digest.Value != request.ArtifactDigest || Digest(Describe(replay, intent)) != request.ReplayDigest)
            throw Invalid();
        using var output = new MemoryStream();
        Write(output, JsonSerializer.SerializeToUtf8Bytes(request, Json), MaximumHeaderBytes);
        Write(output, intent.CopyCanonicalBytes(), MaximumIntentBytes);
        Write(output, artifact.Copy(), checked((int)intent.Profile.MaximumArtifactBytes));
        Write(output, replay.Primary.CopyBytes(), replay.Limits.MaximumInputBytes, request.ProtocolVersion == CsvProtocol);
        foreach (var input in replay.NamedInputs) Write(output, input.CopyBytes(), replay.Limits.MaximumInputBytes);
        if (output.Length > MaximumRequestBytes) throw Invalid();
        return output.ToArray();
    }

    public static Invocation DecodeRequest(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumRequestBytes) throw Invalid();
        var offset = 0;
        var rawHeader = Read(bytes, ref offset, MaximumHeaderBytes);
        var header = ReadJson<Request>(rawHeader);
        if (header.ProtocolVersion != CsvProtocol)
        {
            using var previous = JsonDocument.Parse(rawHeader);
            if (previous.RootElement.GetProperty("replay").GetProperty("inputs").EnumerateArray().Any(value => value.TryGetProperty("csvDialect", out _))) throw Invalid();
        }
        if (header.ProtocolVersion == Protocol)
        {
            using var legacy = JsonDocument.Parse(rawHeader);
            if (legacy.RootElement.GetProperty("replay").GetProperty("inputs").EnumerateArray().Any(value => value.TryGetProperty("mediaType", out _))) throw Invalid();
        }
        var intent = BuildIntentCodec.Decode(Read(bytes, ref offset, MaximumIntentBytes));
        Validate(header, intent);
        var artifactBytes = Read(bytes, ref offset, header.ArtifactBytes);
        if (artifactBytes.Length != header.ArtifactBytes || Sha256Digest.FromBytes(artifactBytes).Value != header.ArtifactDigest) throw Invalid();
        var inputs = new List<PreviewInputDocument>();
        var remaining = header.Replay.Limits.MaximumInputBytes;
        foreach (var input in header.Replay.Inputs)
        {
            if (input.ByteCount > remaining) throw Invalid();
            var value = Read(bytes, ref offset, input.ByteCount, header.ProtocolVersion == CsvProtocol);
            if (value.Length != input.ByteCount || Sha256Digest.FromBytes(value).Value != input.Sha256) throw Invalid();
            inputs.Add(new(input.Name, value, input.CsvDialect, input.XmlMetadata));
            remaining -= value.Length;
        }
        if (offset != bytes.Length) throw Invalid();
        var replay = new RegressionReplay(Target(header.Replay.Target, header.Replay.TargetName), header.Replay.CurrentDateTime,
            header.Replay.Limits, inputs[0], inputs.Skip(1).ToArray(), header.Replay.Parameters.Select(value =>
                new PreviewParameter(value.Name, Kind(value.Kind), value.Value)).ToArray());
        if (Digest(Describe(replay, intent)) != header.ReplayDigest) throw Invalid();
        return new(header, intent, new(artifactBytes, intent.Profile.MaximumArtifactBytes), replay);
    }

    public static byte[] EncodeResponse(Invocation invocation, string status, RegressionOutputSet? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var documents = outputs?.Outputs.Select(value => new Document(value.Key.Name, value.ByteCount, value.Digest.Value,
            invocation.Header.ProtocolVersion is DocumentProtocol or CsvProtocol or XmlProtocol ? value.MediaType : null,
            value.CsvDialect, value.XmlMetadata)).ToArray() ?? [];
        if (invocation.Header.ProtocolVersion == Protocol && outputs?.Outputs.Any(value => value.MediaType != PreviewPolicy.MediaType) == true) throw Invalid();
        var request = invocation.Header;
        var header = new Response(request.ProtocolVersion, request.RequestId, request.BuildId, request.IntentDigest, request.ArtifactDigest,
            request.ReplayDigest, status, documents);
        ValidateResponse(header, request, invocation.Intent);
        using var buffer = new MemoryStream();
        Write(buffer, JsonSerializer.SerializeToUtf8Bytes(header, Json), MaximumHeaderBytes);
        if (outputs is not null) foreach (var output in outputs.Outputs) Write(buffer, output.CopyBytes(), invocation.Replay.Limits.MaximumOutputBytes,
            invocation.Header.ProtocolVersion == CsvProtocol);
        if (buffer.Length > MaximumResponseBytes) throw Invalid();
        return buffer.ToArray();
    }

    public static Result DecodeResponse(byte[] bytes, Request request, BuildIntent intent)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Validate(request, intent);
        if (bytes.Length > MaximumResponseBytes) throw Invalid();
        var offset = 0;
        var rawHeader = Read(bytes, ref offset, MaximumHeaderBytes);
        var header = ReadJson<Response>(rawHeader);
        if (request.ProtocolVersion != CsvProtocol)
        {
            using var previous = JsonDocument.Parse(rawHeader);
            if (previous.RootElement.GetProperty("outputs").EnumerateArray().Any(value => value.TryGetProperty("csvDialect", out _))) throw Invalid();
        }
        if (request.ProtocolVersion == Protocol)
        {
            using var legacy = JsonDocument.Parse(rawHeader);
            if (legacy.RootElement.GetProperty("outputs").EnumerateArray().Any(value => value.TryGetProperty("mediaType", out _))) throw Invalid();
        }
        ValidateResponse(header, request, intent);
        var values = new List<RegressionOutput>();
        var remaining = request.Replay.Limits.MaximumOutputBytes;
        foreach (var document in header.Outputs)
        {
            if (document.ByteCount > remaining) throw Invalid();
            var value = Read(bytes, ref offset, document.ByteCount, request.ProtocolVersion == CsvProtocol);
            if (value.Length != document.ByteCount || Sha256Digest.FromBytes(value).Value != document.Sha256) throw Invalid();
            remaining -= value.Length;
            values.Add(RegressionOutput.Capture(document.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(document.Name), value,
                mediaType: document.MediaType ?? PreviewPolicy.MediaType, csvDialect: document.CsvDialect, xmlMetadata: document.XmlMetadata));
        }
        if (offset != bytes.Length) throw Invalid();
        return new(header, header.Status == "succeeded" ? RegressionOutputSet.Capture(values) : null);
    }

    private static void Validate(Request request, BuildIntent intent)
    {
        if (request is null || intent is null || request.ProtocolVersion != ProtocolFor(intent) ||
            !Guid.TryParseExact(request.RequestId, "D", out var id) || id == Guid.Empty || id.ToString("D") != request.RequestId ||
            request.BuildId != intent.BuildId || request.IntentDigest != intent.Fingerprint.Value ||
            request.ArtifactBytes is < 1 or > BuildWorkerProtocol.MaximumSourceBytes || request.ArtifactBytes > intent.Profile.MaximumArtifactBytes ||
            !Hash(request.ArtifactDigest) || !Hash(request.ReplayDigest) || request.Replay is null || request.Replay.Limits is null ||
            request.Replay.Inputs is null || request.Replay.Inputs.Length is < 1 or > PreviewPolicy.MaximumInputDocuments ||
            request.Replay.Parameters is null || request.Replay.Parameters.Length > PreviewPolicy.MaximumParameters ||
            Digest(request.Replay) != request.ReplayDigest) throw Invalid();
        var limits = request.Replay.Limits;
        if (request.ProtocolVersion == CsvProtocol && (!BuildWorkerProtocol.AdmitsCsvPlan(intent.Boundaries) ||
            request.Replay.Inputs.Length != 1 || request.Replay.Target is not ("all" or "primary") || request.Replay.TargetName is not null)) throw Invalid();
        if (limits.MaximumInputBytes is < 1 or > PreviewPolicy.MaximumInputBytes || limits.MaximumOutputBytes is < 1 or > PreviewPolicy.MaximumOutputBytes ||
            limits.MaximumArtifacts is < 1 or > PreviewPolicy.MaximumArtifacts || limits.MaximumTextBytes is < PreviewPolicy.MinimumTextBytes or > PreviewPolicy.MaximumTextBytes)
            throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var remaining = limits.MaximumInputBytes;
        for (var i = 0; i < request.Replay.Inputs.Length; i++)
        {
            var input = request.Replay.Inputs[i];
            if (input is null || input.ByteCount < 0 || input.ByteCount == 0 && request.ProtocolVersion != CsvProtocol || input.ByteCount > remaining || !Hash(input.Sha256) ||
                (i == 0 ? input.Name is not null : !Name(input.Name) || !names.Add(input.Name!))) throw Invalid();
            remaining -= input.ByteCount;
            if (input.MediaType != DeclaredMedia(intent, input.Name, input: true) || input.CsvDialect != DeclaredDialect(intent, input.Name, input: true)) throw Invalid();
            if (input.XmlMetadata != DeclaredXml(intent, input.Name, true)) throw Invalid();
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
        if (response is null || response.ProtocolVersion != request.ProtocolVersion || response.RequestId != request.RequestId || response.BuildId != request.BuildId ||
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
            if (document is null || document.ByteCount < 0 || document.ByteCount == 0 && request.ProtocolVersion != CsvProtocol || document.ByteCount > remaining ||
                !Hash(document.Sha256) || !expected.Remove(document.Name) || document.MediaType != DeclaredMedia(intent, document.Name, input: false) ||
                document.CsvDialect != DeclaredDialect(intent, document.Name, input: false) || document.XmlMetadata != DeclaredXml(intent, document.Name, false)) throw Invalid();
            remaining -= document.ByteCount;
        }
        if (expected.Count != 0) throw Invalid();
    }

    public static string ProtocolFor(BuildIntent intent) => intent.Profile.Id switch
    {
        BuildWorkerProtocol.CsvProfile => CsvProtocol,
        BuildWorkerProtocol.XmlProfile => XmlProtocol,
        BuildWorkerProtocol.DocumentProfile or BuildWorkerProtocol.X12InterchangesProfile => DocumentProtocol,
        // Legacy qualification also uses this grammar to bind replay digests for
        // other backends. Execution admission independently checks runtime support.
        _ => Protocol
    };
    private static CsvDocumentDialect? DeclaredDialect(BuildIntent intent, string? name, bool input) =>
        intent.Profile.Id == BuildWorkerProtocol.CsvProfile ? DeclaredBoundary(intent, name, input).CsvDialect : null;
    private static XmlDocumentMetadata? DeclaredXml(BuildIntent intent, string? name, bool input) =>
        intent.Profile.Id == BuildWorkerProtocol.XmlProfile ? DeclaredBoundary(intent, name, input).XmlMetadata : null;
    private static BuildBoundary DeclaredBoundary(BuildIntent intent, string? name, bool input)
    {
        var kind = (input, name is null) switch
        {
            (true, true) => BuildBoundaryKind.PrimaryInput,
            (true, false) => BuildBoundaryKind.NamedInput,
            (false, true) => BuildBoundaryKind.PrimaryOutput,
            _ => BuildBoundaryKind.NamedOutput
        };
        return intent.Boundaries.SingleOrDefault(value => value.Kind == kind && value.Name == name) ?? throw Invalid();
    }
    private static string? DeclaredMedia(BuildIntent intent, string? name, bool input)
    {
        if (ProtocolFor(intent) == Protocol) return null;
        var boundary = DeclaredBoundary(intent, name, input);
        return BuildWorkerProtocol.MediaType(boundary.Format);
    }
    private static Replay Describe(RegressionReplay replay, BuildIntent intent)
    {
        foreach (var input in replay.NamedInputs.Prepend(replay.Primary))
            if (input.CsvDialect != DeclaredDialect(intent, input.Name, input: true) || input.XmlMetadata != DeclaredXml(intent, input.Name, true)) throw Invalid();
        return new(replay.Target.Kind switch
        {
            PreviewTargetKind.All => "all",
            PreviewTargetKind.Primary => "primary",
            PreviewTargetKind.Named => "named",
            _ => throw Invalid()
        }, replay.Target.Name, replay.CurrentDateTime, replay.Limits,
            new[] { replay.Primary }.Concat(replay.NamedInputs).Select(value => new Document(value.Name, value.ByteCount, value.Digest.Value,
                DeclaredMedia(intent, value.Name, input: true), value.CsvDialect, value.XmlMetadata)).ToArray(),
            replay.Parameters.Select(value => new Parameter(value.Name, value.Kind switch
            {
                PreviewParameterKind.String => "string",
                PreviewParameterKind.Integer => "integer",
                PreviewParameterKind.Float => "float",
                PreviewParameterKind.Boolean => "boolean",
                _ => throw Invalid()
            }, value.Value)).ToArray());
    }
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
    private static byte[] Read(byte[] bytes, ref int offset, int maximum, bool allowEmpty = false)
    {
        if (offset > bytes.Length - 4) throw Invalid();
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4;
        if (length < 0 || length == 0 && !allowEmpty || length > maximum || length > bytes.Length - offset) throw Invalid();
        var result = bytes.AsSpan(offset, length).ToArray(); offset += length; return result;
    }
    private static void Write(Stream stream, ReadOnlySpan<byte> value, int maximum, bool allowEmpty = false)
    {
        if (value.Length == 0 && !allowEmpty || value.Length > maximum) throw Invalid();
        Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, value.Length); stream.Write(prefix); stream.Write(value);
    }
    private static InvalidOperationException Invalid() => new("Compiled runtime protocol refused.");
}
