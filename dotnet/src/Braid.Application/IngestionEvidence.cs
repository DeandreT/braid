using System.Text;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

public enum IngestionBackendStatus
{
    Succeeded = 1,
    InvalidMapping = 2,
    UnsupportedArtifact = 3,
    ResourceLimit = 4,
    ProtocolFailure = 5,
    BackendUnavailable = 6,
    BackendFailure = 7,
    TimedOut = 8,
    Canceled = 9,
    IsolationFailure = 10
}

public sealed record IngestionProvenance
{
    public IngestionProvenance(RevisionScope scope, Sha256Digest nativeDigest,
        IngestionBackendProfile backendProfile, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(backendProfile);
        Scope = scope;
        NativeDigest = nativeDigest;
        BackendProfile = backendProfile;
        Confirmed = confirmed;
    }

    public RevisionScope Scope { get; }
    public Sha256Digest NativeDigest { get; }
    public IngestionBackendProfile BackendProfile { get; }
    public bool Confirmed { get; }
}

public sealed record IngestionValidationFacts
{
    public IngestionValidationFacts(IngestionBackendStatus status, IngestionProvenance provenance,
        bool? isValid, int totalIssueCount, int emittedIssueCount, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), "The backend status must be known.");
        if (totalIssueCount is < 0 or > 32768 || emittedIssueCount is < 0 or > 128 || emittedIssueCount > totalIssueCount)
            throw new ArgumentOutOfRangeException(nameof(totalIssueCount), "Validation counts exceed their bounds or disagree.");
        Status = status;
        Provenance = provenance;
        IsValid = isValid;
        TotalIssueCount = totalIssueCount;
        EmittedIssueCount = emittedIssueCount;
        Truncated = truncated;
    }

    public IngestionBackendStatus Status { get; }
    public IngestionProvenance Provenance { get; }
    public bool? IsValid { get; }
    public int TotalIssueCount { get; }
    public int EmittedIssueCount { get; }
    public bool Truncated { get; }
}

/// <summary>Owned derived validation metadata; never native artifact bytes or HTTP-supplied evidence.</summary>
public sealed class IngestionEvidenceDocument
{
    public const string SchemaVersion = "braid.ingestion-validation.v1";
    public const int MaximumUtf8Bytes = 8 * 1024 * 1024;
    public const int MaximumDepth = 64;
    public const int MaximumNodes = 32768;
    public const int MaximumStringBytes = 2048;
    public const int MaximumDiagnostics = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _utf8;

    private IngestionEvidenceDocument(IngestionValidationFacts facts, byte[] ownedUtf8)
    {
        Facts = facts;
        _utf8 = ownedUtf8;
    }

    public IngestionValidationFacts Facts { get; }
    public string Version => SchemaVersion;

    public static IngestionEvidenceDocument Create(IngestionValidationFacts facts, JsonElement diagnostics)
    {
        ArgumentNullException.ThrowIfNull(facts);
        try
        {
            return CreateDocument(facts, diagnostics);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or
            InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
        {
            throw new ArgumentException("Validation evidence must contain a bounded valid metadata document.", nameof(diagnostics));
        }
    }

    private static IngestionEvidenceDocument CreateDocument(IngestionValidationFacts facts, JsonElement diagnostics)
    {
        ValidateDiagnostics(diagnostics, facts);
        using var buffer = new EvidenceBuffer();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", SchemaVersion);
            writer.WriteStartObject("scope");
            writer.WriteString("organizationId", facts.Provenance.Scope.OrganizationId);
            writer.WriteString("projectId", facts.Provenance.Scope.ProjectId);
            writer.WriteString("mappingId", facts.Provenance.Scope.MappingId);
            writer.WriteString("revisionId", facts.Provenance.Scope.RevisionId);
            writer.WriteEndObject();
            writer.WriteString("nativeDigest", facts.Provenance.NativeDigest.Value);
            writer.WriteStartObject("backendProfile");
            writer.WriteString("backendId", facts.Provenance.BackendProfile.BackendId);
            writer.WriteString("engineRevision", facts.Provenance.BackendProfile.EngineRevision);
            writer.WriteString("backendBuild", facts.Provenance.BackendProfile.BackendBuild);
            writer.WriteString("adapterBuild", facts.Provenance.BackendProfile.AdapterBuild);
            writer.WriteEndObject();
            writer.WriteBoolean("confirmed", facts.Provenance.Confirmed);
            writer.WriteString("status", IngestionPersistenceNames.BackendStatus(facts.Status));
            if (facts.IsValid is { } isValid)
                writer.WriteBoolean("isValid", isValid);
            else
                writer.WriteNull("isValid");
            writer.WriteNumber("totalIssueCount", facts.TotalIssueCount);
            writer.WriteNumber("emittedIssueCount", facts.EmittedIssueCount);
            writer.WriteBoolean("truncated", facts.Truncated);
            writer.WritePropertyName("diagnostics");
            diagnostics.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Restore(facts, buffer.ToArray());
    }

    public static IngestionEvidenceDocument Restore(IngestionValidationFacts facts, ReadOnlySpan<byte> utf8Document)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (utf8Document.Length is < 1 or > MaximumUtf8Bytes)
            throw new ArgumentException("Validation evidence exceeds its UTF8 document limit.", nameof(utf8Document));
        try
        {
            _ = StrictUtf8.GetCharCount(utf8Document);
            var owned = utf8Document.ToArray();
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            ValidateTree(document.RootElement, 0, new NodeBudget());
            if (ReadFacts(document.RootElement) != facts)
                throw new ArgumentException("Validation evidence metadata must agree with its typed facts.", nameof(utf8Document));
            ValidateDiagnostics(document.RootElement.GetProperty("diagnostics"), facts);
            return new(facts, owned);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or
            InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
        {
            throw new ArgumentException("Validation evidence must contain a bounded valid metadata document.", nameof(utf8Document));
        }
    }

    public byte[] CopyUtf8() => (byte[])_utf8.Clone();

    public JsonElement CopyDocument()
    {
        using var document = JsonDocument.Parse(_utf8, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        return document.RootElement.Clone();
    }

    internal bool HasDiagnosticCode(string code)
    {
        using var document = JsonDocument.Parse(_utf8, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        return document.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Any(diagnostic => diagnostic.GetProperty("code").GetString() == code);
    }

    private static IngestionValidationFacts ReadFacts(JsonElement root)
    {
        RequireObjectFields(root, "version", "scope", "nativeDigest", "backendProfile", "confirmed", "status",
            "isValid", "totalIssueCount", "emittedIssueCount", "truncated", "diagnostics");
        if (root.GetProperty("version").GetString() != SchemaVersion)
            throw new ArgumentException("The validation evidence version is unsupported.");
        var scope = root.GetProperty("scope");
        RequireObjectFields(scope, "organizationId", "projectId", "mappingId", "revisionId");
        var profile = root.GetProperty("backendProfile");
        RequireObjectFields(profile, "backendId", "engineRevision", "backendBuild", "adapterBuild");
        var provenance = new IngestionProvenance(
            new RevisionScope(ReadId(scope, "organizationId"), ReadId(scope, "projectId"),
                ReadId(scope, "mappingId"), ReadId(scope, "revisionId")),
            Sha256Digest.Parse(RequireString(root, "nativeDigest")),
            new IngestionBackendProfile(RequireString(profile, "backendId"), RequireString(profile, "engineRevision"),
                RequireString(profile, "backendBuild"), RequireString(profile, "adapterBuild")),
            root.GetProperty("confirmed").GetBoolean());
        var validity = root.GetProperty("isValid");
        return new(IngestionPersistenceNames.ParseBackendStatus(RequireString(root, "status")), provenance,
            validity.ValueKind == JsonValueKind.Null ? null : validity.GetBoolean(),
            root.GetProperty("totalIssueCount").GetInt32(), root.GetProperty("emittedIssueCount").GetInt32(),
            root.GetProperty("truncated").GetBoolean());
    }

    private static Guid ReadId(JsonElement root, string propertyName)
    {
        if (!Guid.TryParseExact(RequireString(root, propertyName), "D", out var id))
            throw new ArgumentException("Validation evidence requires canonical UUID fields.");
        return id;
    }

    private static string RequireString(JsonElement root, string name) => root.GetProperty(name).GetString()
        ?? throw new ArgumentException("Validation evidence requires nonnull metadata strings.");

    private static void RequireObjectFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Validation evidence requires a metadata object.");
        var allowed = fields.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Remove(property.Name))
                throw new ArgumentException("Validation evidence has duplicated or unknown metadata fields.");
        }
        if (allowed.Count != 0)
            throw new ArgumentException("Validation evidence is missing required metadata fields.");
    }

    private static void ValidateDiagnostics(JsonElement diagnostics, IngestionValidationFacts facts)
    {
        if (diagnostics.ValueKind != JsonValueKind.Array || diagnostics.GetArrayLength() > MaximumDiagnostics ||
            (facts.IsValid is not null && diagnostics.GetArrayLength() != facts.EmittedIssueCount))
            throw new ArgumentException("Validation diagnostics must be bounded and agree with validator issue counts.");
        ValidateTree(diagnostics, 0, new NodeBudget());
        foreach (var diagnostic in diagnostics.EnumerateArray())
        {
            if (diagnostic.ValueKind != JsonValueKind.Object || !diagnostic.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString()))
                throw new ArgumentException("A validation diagnostic requires a bounded nonempty code.");
        }
    }

    private static void ValidateTree(JsonElement value, int depth, NodeBudget budget)
    {
        if (depth > MaximumDepth || ++budget.Count > MaximumNodes)
            throw new ArgumentException("Validation evidence exceeds its depth or item limit.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    ValidateString(property.Name);
                    if (!names.Add(property.Name))
                        throw new ArgumentException("Validation evidence contains duplicate fields.");
                    ValidateTree(property.Value, depth + 1, budget);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateTree(item, depth + 1, budget);
                break;
            case JsonValueKind.String: ValidateString(value.GetString()!); break;
            case JsonValueKind.Undefined: throw new ArgumentException("Validation evidence contains an undefined value.");
        }
    }

    private static void ValidateString(string value)
    {
        if (value.Contains('\0') || StrictUtf8.GetByteCount(value) > MaximumStringBytes)
            throw new ArgumentException("Validation evidence text must fit its persisted UTF8 representation.");
    }

    private sealed class NodeBudget { public int Count { get; set; } }

    private sealed class EvidenceBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireSpace(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireSpace(buffer.Length);
            base.Write(buffer);
        }

        private void RequireSpace(int count)
        {
            if (Length + count > MaximumUtf8Bytes)
                throw new ArgumentException("Validation evidence exceeds its UTF8 document limit.");
        }
    }
}

/// <summary>Stable persisted spellings, independent of CLR enum names or numeric values.</summary>
public static class IngestionPersistenceNames
{
    public static string BackendStatus(IngestionBackendStatus status) => status switch
    {
        IngestionBackendStatus.Succeeded => "succeeded",
        IngestionBackendStatus.InvalidMapping => "invalid_mapping",
        IngestionBackendStatus.UnsupportedArtifact => "unsupported_artifact",
        IngestionBackendStatus.ResourceLimit => "resource_limit",
        IngestionBackendStatus.ProtocolFailure => "protocol_failure",
        IngestionBackendStatus.BackendUnavailable => "backend_unavailable",
        IngestionBackendStatus.BackendFailure => "backend_failure",
        IngestionBackendStatus.TimedOut => "timed_out",
        IngestionBackendStatus.Canceled => "canceled",
        IngestionBackendStatus.IsolationFailure => "isolation_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "The backend status must be known.")
    };

    public static IngestionBackendStatus ParseBackendStatus(string value) => value switch
    {
        "succeeded" => IngestionBackendStatus.Succeeded,
        "invalid_mapping" => IngestionBackendStatus.InvalidMapping,
        "unsupported_artifact" => IngestionBackendStatus.UnsupportedArtifact,
        "resource_limit" => IngestionBackendStatus.ResourceLimit,
        "protocol_failure" => IngestionBackendStatus.ProtocolFailure,
        "backend_unavailable" => IngestionBackendStatus.BackendUnavailable,
        "backend_failure" => IngestionBackendStatus.BackendFailure,
        "timed_out" => IngestionBackendStatus.TimedOut,
        "canceled" => IngestionBackendStatus.Canceled,
        "isolation_failure" => IngestionBackendStatus.IsolationFailure,
        _ => throw new ArgumentException("The persisted backend status must be known.", nameof(value))
    };
}
