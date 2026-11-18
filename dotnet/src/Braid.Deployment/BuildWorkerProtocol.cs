using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Closed framed compiler protocol shared by the host adapter and installed compiler worker.</summary>
public static class BuildWorkerProtocol
{
    public const string Protocol = "braid.ferrule-build.v1";
    public const string Profile = "braid.ferrule-csharp-json.v1";
    public const string DocumentProfile = "braid.ferrule-csharp-document.v1";
    public const string X12InterchangesProfile = "braid.ferrule-csharp-x12-interchanges.v1";
    public const string CsvProfile = "braid.ferrule-csharp-csv.v1";
    public static bool IsDocumentProfile(string profile) => profile is DocumentProfile or X12InterchangesProfile or CsvProfile;
    public static string PreviewProfileFor(string profile) => profile == CsvProfile ? CsvDocumentPolicy.Profile :
        IsDocumentProfile(profile) ? DocumentPreviewPolicy.Profile : PreviewPolicy.Profile;
    public const string SdkVersion = "10.0.112";
    public const string RuntimeVersion = "10.0.12";
    public const string Engine = "238369bf0071ee8ea097d8c54fa5061f433807fb";
    public const int MaximumNativeBytes = 4 * 1024 * 1024;
    public const int MaximumSourceBytes = 32 * 1024 * 1024;
    public const int MaximumHeaderBytes = 128 * 1024;
    public const int MaximumReplyBytes = MaximumHeaderBytes + MaximumSourceBytes + 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };
    public sealed record Request(string ProtocolVersion, string RequestId, RevisionScope Revision,
        string NativeDigest, string IntentDigest, string Operation, string Profile);
    public sealed record Boundary(string Kind, string? Name, string Format,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CsvDocumentDialect? CsvDialect = null);
    public sealed record File(string Path, int ByteCount, string Sha256);
    public sealed record Response(string ProtocolVersion, string? RequestId, RevisionScope? Revision,
        string? NativeDigest, string? IntentDigest, string EngineRevision, string Profile, string Status,
        Boundary[] Boundaries, File[] Files);
    public sealed record Reply(Response Header, IReadOnlyList<byte[]> Contents);

    public static byte[] EncodeRequest(Request request, ReadOnlySpan<byte> native)
    {
        ValidateRequest(request);
        if (native.Length is < 1 or > MaximumNativeBytes || Sha256Digest.FromBytes(native).Value != request.NativeDigest) throw Invalid();
        using var stream = new MemoryStream(); WriteFrame(stream, JsonSerializer.SerializeToUtf8Bytes(request, Json)); WriteFrame(stream, native);
        return stream.ToArray();
    }
    public static (Request Header, byte[] Native) DecodeRequest(byte[] bytes)
    {
        var offset = 0; var header = ReadFrame(bytes, ref offset, 16384);
        var request = ReadJson<Request>(header); ValidateRequest(request);
        var native = ReadFrame(bytes, ref offset, MaximumNativeBytes);
        if (offset != bytes.Length || Sha256Digest.FromBytes(native).Value != request.NativeDigest) throw Invalid();
        return (request, native);
    }
    public static Reply DecodeReply(byte[] bytes, Request expected)
    {
        if (bytes.Length > MaximumReplyBytes) throw Invalid();
        var offset = 0;
        var header = ReadJson<Response>(ReadFrame(bytes, ref offset, MaximumHeaderBytes));
        if (header.ProtocolVersion != Protocol || header.RequestId != expected.RequestId || header.Revision != expected.Revision ||
            header.NativeDigest != expected.NativeDigest || header.IntentDigest != expected.IntentDigest || header.EngineRevision != Engine || header.Profile != expected.Profile ||
            header.Status is not ("supported" or "unsupported" or "unsupportedCsvPlan" or "unsupportedXmlBuild" or "unsupportedFlexTextBuild" or
                "unsupportedXlsxBuild" or "unsupportedProtobufBuild" or "unsupportedXbrlBuild" or "invalidRequest" or "invalidMapping" or "sourceLimit" or "compileFailed") ||
            header.Files.Length > 128 || header.Boundaries.Length > 128) throw Invalid();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = new List<byte[]>(); var remaining = MaximumSourceBytes;
        foreach (var file in header.Files)
        {
            if (file is null || !SafePath(file.Path) || !paths.Add(file.Path) || file.ByteCount is < 1 || file.ByteCount > remaining) throw Invalid();
            var data = ReadFrame(bytes, ref offset, file.ByteCount);
            if (data.Length != file.ByteCount || Sha256Digest.FromBytes(data).Value != file.Sha256) throw Invalid();
            remaining -= data.Length; contents.Add(data);
        }
        if (offset != bytes.Length || header.Status != "supported" && (header.Files.Length != 0 || header.Boundaries.Length != 0)) throw Invalid();
        if (header.Status == "supported")
        {
            var identities = new HashSet<(string, string?)>();
            foreach (var boundary in header.Boundaries)
                if (boundary is null || boundary.Kind is not ("primaryInput" or "namedInput" or "primaryOutput" or "namedOutput") || !AdmitsBoundary(expected.Profile, boundary.Format, boundary.CsvDialect) ||
                    (boundary.Kind is "namedInput" or "namedOutput") != (boundary.Name is not null) ||
                    boundary.Name is { } name && (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 256 || name.Any(char.IsControl)) ||
                    !identities.Add((boundary.Kind, boundary.Name))) throw Invalid();
            if (!identities.Contains(("primaryInput", null)) || !identities.Contains(("primaryOutput", null))) throw Invalid();
            if (expected.Profile == CsvProfile && identities.Count != 2) throw Invalid();
        }
        return new(header, contents.AsReadOnly());
    }
    public static byte[] EncodeReply(Response response, IReadOnlyList<byte[]> contents)
    {
        if (response.Files.Length != contents.Count) throw Invalid();
        using var stream = new MemoryStream(); var header = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        if (header.Length > MaximumHeaderBytes) throw Invalid();
        WriteFrame(stream, header);
        foreach (var content in contents) WriteFrame(stream, content);
        if (stream.Length > MaximumReplyBytes) throw Invalid();
        return stream.ToArray();
    }
    public static bool SafePath(string value) => value.Length is > 0 and <= 256 && value.Split('/').All(part =>
        part.Length > 0 && part is not ("." or "..") && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    public static Request For(BuildIntent intent, string operation) => new(Protocol, Guid.NewGuid().ToString("D"), intent.Revision,
        intent.NativeDigest.Value, intent.Fingerprint.Value, operation, intent.Profile.Id);
    public static bool AdmitsFormat(string profile, string format) => profile switch
    {
        Profile => format == "json",
        DocumentProfile or X12InterchangesProfile => format is "json" or "x12" or "edifact",
        CsvProfile => format is "csv" or "tsv",
        _ => false
    };
    public static bool AdmitsBoundary(string profile, string format, CsvDocumentDialect? dialect) =>
        AdmitsFormat(profile, format) && (profile == CsvProfile ? CsvDocumentPolicy.Admits(dialect, MediaType(format)) : dialect is null);
    public static bool AdmitsCsvPlan(IReadOnlyList<BuildBoundary> boundaries) => boundaries.Count == 2 &&
        boundaries.Count(boundary => boundary.Kind == BuildBoundaryKind.PrimaryInput && boundary.Name is null) == 1 &&
        boundaries.Count(boundary => boundary.Kind == BuildBoundaryKind.PrimaryOutput && boundary.Name is null) == 1 &&
        boundaries.All(boundary => AdmitsBoundary(CsvProfile, boundary.Format, boundary.CsvDialect));
    public static string MediaType(string format) => format switch
    {
        "json" => "application/json",
        "x12" => "application/edi-x12",
        "edifact" => "application/edifact",
        "csv" => "text/csv",
        "tsv" => "text/tab-separated-values",
        _ => throw Invalid()
    };
    public static BuildBoundary ToBoundary(Boundary boundary) => new(boundary.Kind switch
    {
        "primaryInput" => BuildBoundaryKind.PrimaryInput,
        "namedInput" => BuildBoundaryKind.NamedInput,
        "primaryOutput" => BuildBoundaryKind.PrimaryOutput,
        "namedOutput" => BuildBoundaryKind.NamedOutput,
        _ => throw Invalid()
    }, boundary.Name, boundary.Format, boundary.CsvDialect);
    private static void ValidateRequest(Request request)
    {
        if (request.ProtocolVersion != Protocol || request.Profile is not (Profile or DocumentProfile or X12InterchangesProfile or CsvProfile) || request.Operation is not ("inspect" or "compile") ||
            !Guid.TryParseExact(request.RequestId, "D", out var id) || id == Guid.Empty || id.ToString("D") != request.RequestId ||
            Sha256Digest.Parse(request.NativeDigest).Value != request.NativeDigest || Sha256Digest.Parse(request.IntentDigest).Value != request.IntentDigest) throw Invalid();
    }
    private static T ReadJson<T>(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        ValidateKeys(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid();
    }
    private static void ValidateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject()) { if (!keys.Add(property.Name)) throw Invalid(); ValidateKeys(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) ValidateKeys(item);
    }
    private static byte[] ReadFrame(byte[] bytes, ref int offset, int maximum)
    {
        if (offset > bytes.Length - 4) throw Invalid();
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4;
        if (count < 1 || count > maximum || count > bytes.Length - offset) throw Invalid();
        var data = bytes.AsSpan(offset, count).ToArray(); offset += count; return data;
    }
    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, value.Length); stream.Write(prefix); stream.Write(value);
    }
    private static InvalidOperationException Invalid() => new("Build worker protocol refused.");
}
