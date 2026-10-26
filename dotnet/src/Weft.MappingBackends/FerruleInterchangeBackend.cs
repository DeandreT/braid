using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;

namespace Weft.MappingBackends;

/// <summary>Strict byte-only adapter for the separate package conversion profile.</summary>
public sealed class FerruleInterchangeBackend : IInterchangeBackend, IInterchangeBackendRegistry
{
    public const string Protocol = "weft.ferrule-mfd.v1";
    public const int MaximumHeaderBytes = 1024 * 1024;
    public const int MaximumRequestBytes = InterchangePackage.MaximumEncodedBytes + 16 * 1024 + 8;
    public const int MaximumResponseBytes = 18 * 1024 * 1024;
    private readonly IInspectionWorkerRunner runner;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public FerruleInterchangeBackend(IInspectionWorkerRunner runner)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        Profile = new("ferrule", FerruleInspectionBackend.EngineRevision, runner.BuildIdentity, FerruleInspectionBackend.AdapterBuild);
    }
    public IngestionBackendProfile Profile { get; }
    public IInterchangeBackend? GetCurrent(string backendId) => backendId == Profile.BackendId ? this : null;

    public async Task<InterchangeImportOutput> ImportAsync(InterchangePackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = await ConvertAsync("import", package.Encode(), package.DesignPath, cancellationToken);
        if (result.Payload.Length > IngestionAdmissionPolicy.MaximumNativeByteCount) throw Refused();
        return new(Profile, package.Digest, result.Payload, result.Report);
    }

    public async Task<InterchangeExportOutput> ExportAsync(NativeArtifact native, string designPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(native);
        var result = await ConvertAsync("export", native.CopyBytes(), designPath, cancellationToken);
        var package = InterchangePackage.Decode(result.Payload);
        if (package.DesignPath != designPath) throw Refused();
        return new(Profile, native.Digest, package, result.Report);
    }

    private async Task<(byte[] Payload, InterchangeReport Report)> ConvertAsync(string operation, byte[] payload,
        string designPath, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("D");
        var digest = Sha256Digest.FromBytes(payload).Value;
        var header = JsonSerializer.SerializeToUtf8Bytes(new Request(Protocol, id, operation, digest, designPath, Profile.EngineRevision), Json);
        var request = new byte[header.Length + payload.Length + 8];
        if (request.Length > MaximumRequestBytes) throw Refused();
        BinaryPrimitives.WriteInt32BigEndian(request, header.Length);
        header.CopyTo(request, 4);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(header.Length + 4), payload.Length);
        payload.CopyTo(request, header.Length + 8);
        var output = await runner.RunAsync(request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (output.Failure != WorkerProcessFailure.None || output.Output.Length > MaximumResponseBytes) throw Refused();
        try
        {
            ReadOnlyMemory<byte> remaining = output.Output;
            var responseBytes = TakeFrame(ref remaining, MaximumHeaderBytes);
            using var document = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 8 });
            RequireUniqueProperties(document.RootElement);
            var response = JsonSerializer.Deserialize<Response>(responseBytes.Span, Json) ?? throw Refused();
            if (response.ProtocolVersion != Protocol || response.RequestId != id || response.Operation != operation ||
                response.InputDigest != digest || response.DesignPath != designPath || response.EngineRevision != Profile.EngineRevision ||
                response.Status != "succeeded" || response.Warnings is null || response.Warnings.Length > InterchangeReport.MaximumWarnings)
                throw Refused();
            var converted = TakeFrame(ref remaining, operation == "import" ? (int)IngestionAdmissionPolicy.MaximumNativeByteCount : InterchangePackage.MaximumEncodedBytes).ToArray();
            if (!remaining.IsEmpty || response.PayloadBytes != converted.Length || response.PayloadDigest != Sha256Digest.FromBytes(converted).Value)
                throw Refused();
            var report = new InterchangeReport(response.Summary, response.Warnings.Select(w => new InterchangeWarning(w.Code, w.Message)));
            return (converted, report);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or OverflowException)
        { throw Refused(); }
    }

    private static ReadOnlyMemory<byte> TakeFrame(ref ReadOnlyMemory<byte> bytes, int maximum)
    {
        if (bytes.Length < 4) throw Refused();
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.Span);
        if (length < 1 || length > maximum || length > bytes.Length - 4) throw Refused();
        var frame = bytes.Slice(4, length);
        bytes = bytes[(length + 4)..];
        return frame;
    }
    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Refused();
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RequireUniqueProperties(item);
    }
    private static InvalidOperationException Refused() => new("The bounded interchange worker response was refused.");
    private sealed record Request(string ProtocolVersion, string RequestId, string Operation, string InputDigest, string DesignPath, string EngineRevision);
    private sealed record Warning(string Code, string Message);
    private sealed record Response(string ProtocolVersion, string RequestId, string Operation, string InputDigest, string DesignPath,
        string EngineRevision, string Status, string? PayloadDigest, int PayloadBytes, string Summary, Warning[] Warnings);
}
