using Braid.Domain;

namespace Braid.Application;

public interface IMappingPreviewBackend
{
    /// <summary>Executes one bounded sample preview of an exact native revision.</summary>
    Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Separate admission policies; JSON callers never gain document codecs implicitly.</summary>
public enum PreviewExecutionProfile
{
    LegacyJson,
    Document,
    /// <summary>Separate primary-only CSV contract, including explicit empty input presence.</summary>
    CsvDocument,
    XmlDocument
}

public static class DocumentPreviewPolicy
{
    public const string Profile = "braid.ferrule-document-preview.v1";

    public static bool AdmitsMediaType(string mediaType) =>
        mediaType is "application/json" or "application/edi-x12" or "application/edifact";
}

public enum PreviewTargetKind
{
    All,
    Primary,
    Named
}

/// <summary>All targets, the primary target, or exactly one named target.</summary>
public sealed record PreviewTargetSelection
{
    private PreviewTargetSelection(PreviewTargetKind kind, string? name)
    {
        Kind = kind;
        Name = name;
    }

    public static PreviewTargetSelection All { get; } = new(PreviewTargetKind.All, null);
    public static PreviewTargetSelection Primary { get; } = new(PreviewTargetKind.Primary, null);

    public static PreviewTargetSelection Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new(PreviewTargetKind.Named, name);
    }

    public PreviewTargetKind Kind { get; }
    public string? Name { get; }
}

public enum PreviewParameterKind
{
    String,
    Integer,
    Float,
    Boolean
}

/// <summary>One typed runtime parameter. Numbers and booleans keep their exact lexical form.</summary>
public sealed record PreviewParameter(string Name, PreviewParameterKind Kind, string Value);

/// <summary>A bounded input document copied at construction so later caller edits cannot change it.</summary>
public sealed class PreviewInputDocument
{
    private readonly byte[] _bytes;

    public PreviewInputDocument(string? name, ReadOnlySpan<byte> bytes, CsvDocumentDialect? csvDialect = null,
        XmlDocumentMetadata? xmlMetadata = null)
    {
        if (csvDialect is not null && !CsvDocumentPolicy.Admits(csvDialect, CsvDialectJson.MediaType(csvDialect)))
            throw new ArgumentException("Invalid CSV input dialect.", nameof(csvDialect));
        Name = name;
        if (xmlMetadata is not null && (csvDialect is not null || !XmlDocumentPolicy.Admits(xmlMetadata, XmlDocumentRole.Input)))
            throw new ArgumentException("XML input requires distinct role-valid metadata.", nameof(xmlMetadata));
        XmlMetadata = xmlMetadata;
        CsvDialect = csvDialect;
        _bytes = bytes.ToArray();
        Digest = Sha256Digest.FromBytes(_bytes);
    }

    /// <summary>Null for the primary input; otherwise the declared named source.</summary>
    public string? Name { get; }
    public CsvDocumentDialect? CsvDialect { get; }
    public XmlDocumentMetadata? XmlMetadata { get; }
    public Sha256Digest Digest { get; }
    public int ByteCount => _bytes.Length;
    public ReadOnlySpan<byte> Bytes => _bytes;
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
}

public sealed record PreviewLimits(int MaximumInputBytes, int MaximumOutputBytes, int MaximumArtifacts, int MaximumTextBytes)
{
    public static PreviewLimits Default { get; } = new(PreviewPolicy.MaximumInputBytes, PreviewPolicy.MaximumOutputBytes,
        PreviewPolicy.MaximumArtifacts, PreviewPolicy.MaximumTextBytes);
}

public sealed record MappingPreviewRequest(
    NativeArtifact Artifact,
    PreviewTargetSelection Target,
    PreviewInputDocument Primary,
    IReadOnlyList<PreviewInputDocument> NamedInputs,
    IReadOnlyList<PreviewParameter> Parameters,
    string CurrentDateTime,
    PreviewLimits Limits)
{
    public PreviewExecutionProfile? Profile { get; init; }
}

public enum MappingPreviewStatus
{
    Succeeded,
    InvalidRequest,
    InvalidMapping,
    UnsupportedArtifact,
    UnsupportedCapability,
    MissingInput,
    InvalidInput,
    InvalidParameter,
    ExecutionFailed,
    ResourceLimit,
    ProtocolFailure,
    BackendUnavailable,
    BackendFailure,
    TimedOut,
    Canceled,
    IsolationFailure
}

/// <summary>One verified output document in primary-then-named target order.</summary>
public sealed class PreviewOutputArtifact
{
    private readonly byte[] _bytes;

    public PreviewOutputArtifact(string boundaryId, string targetName, bool targetNameTruncated, string mediaType, long recordsWritten, ReadOnlySpan<byte> bytes,
        CsvDocumentDialect? csvDialect = null, XmlDocumentMetadata? xmlMetadata = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(boundaryId);
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegative(recordsWritten);
        if (mediaType == XmlDocumentPolicy.MediaType ? csvDialect is not null || !XmlDocumentPolicy.Admits(xmlMetadata, XmlDocumentRole.Output, mediaType) : xmlMetadata is not null)
            throw new ArgumentException("XML output requires distinct role-valid metadata.", nameof(xmlMetadata));
        if (CsvDocumentPolicy.AdmitsMediaType(mediaType) ? !CsvDocumentPolicy.Admits(csvDialect, mediaType) : csvDialect is not null)
            throw new ArgumentException("CSV output requires a matching versioned dialect.", nameof(csvDialect));
        BoundaryId = boundaryId;
        TargetName = targetName;
        TargetNameTruncated = targetNameTruncated;
        MediaType = mediaType;
        RecordsWritten = recordsWritten;
        CsvDialect = csvDialect;
        XmlMetadata = xmlMetadata;
        _bytes = bytes.ToArray();
        Digest = Sha256Digest.FromBytes(_bytes);
    }

    public string BoundaryId { get; }
    public string TargetName { get; }
    public bool TargetNameTruncated { get; }
    public string MediaType { get; }
    public long RecordsWritten { get; }
    public CsvDocumentDialect? CsvDialect { get; }
    public XmlDocumentMetadata? XmlMetadata { get; }
    public Sha256Digest Digest { get; }
    public int ByteCount => _bytes.Length;
    public ReadOnlySpan<byte> Bytes => _bytes;
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
}

/// <summary>A bounded failure. Subject names the input, parameter, target or boundary when known.</summary>
public sealed record MappingPreviewFailure(string Code, string Message, bool MessageTruncated, string? Subject, bool SubjectTruncated);

/// <summary>Exact revision and backend identities; Confirmed means the adapter accepted matching worker output.</summary>
public sealed record PreviewProvenance(
    RevisionScope Scope,
    Sha256Digest NativeDigest,
    string BackendId,
    string EngineRevision,
    string BackendBuild,
    string AdapterBuild,
    bool Confirmed);

public sealed record MappingPreviewResult(
    MappingPreviewStatus Status,
    PreviewProvenance Provenance,
    string Profile,
    long RecordsWritten,
    IReadOnlyList<PreviewOutputArtifact> Artifacts,
    MappingPreviewFailure? Failure);

/// <summary>Compiled preview ceilings shared with the worker; requests may only select smaller limits.</summary>
public static class PreviewPolicy
{
    public const string Profile = "braid.ferrule-json-preview.v1";
    public const string MediaType = "application/json";
    public const int MaximumHeaderBytes = 16 * 1024;
    public const int MaximumInputDocuments = 16;
    public const int MaximumInputBytes = 8 * 1024 * 1024;
    public const int MaximumOutputBytes = 8 * 1024 * 1024;
    public const int MaximumArtifacts = 64;
    public const int MaximumParameters = 32;
    public const int MaximumNameBytes = 256;
    public const int MaximumParameterTextBytes = 4 * 1024;
    public const int MaximumClockBytes = 35;
    public const int MinimumTextBytes = 64;
    public const int MaximumTextBytes = 2048;
    public const int MaximumNativeBytes = 4 * 1024 * 1024;
    public const int MaximumResponseHeaderBytes = 1024 * 1024;

    /// <summary>Header, native and every input frame, each with its four-byte length prefix.</summary>
    public const int MaximumRequestBytes = 4 + MaximumHeaderBytes + 4 + MaximumNativeBytes
        + (4 * MaximumInputDocuments) + MaximumInputBytes;

    /// <summary>Response header and every output frame, each with its four-byte length prefix.</summary>
    public const int MaximumResponseBytes = 4 + MaximumResponseHeaderBytes + (4 * MaximumArtifacts) + MaximumOutputBytes;
}
