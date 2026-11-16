using System.Text.Json.Serialization;

namespace Braid.Contracts.Catalog;

/// <summary>
/// A bounded sample preview of one exact revision. Input documents are base64 bytes so the
/// server stores exactly what the caller supplied; the first input is the unnamed primary.
/// Profile is optional: null preserves legacy JSON. CSV/TSV uses its own explicit profile and saved native dialects;
/// an empty content string is a present input only in that profile.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPreviewSubmissionV1(
    CatalogPreviewTargetV1 Target,
    IReadOnlyList<CatalogPreviewParameterV1> Parameters,
    IReadOnlyList<CatalogPreviewInputV1> Inputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Profile = null);

/// <summary>Kind is <c>all</c>, <c>primary</c> or <c>named</c>; only <c>named</c> carries a name.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPreviewTargetV1(string Kind, string? Name = null);

/// <summary>Kind is <c>string</c>, <c>integer</c>, <c>float</c> or <c>boolean</c>; numbers are exact decimal text.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPreviewParameterV1(string Name, string Kind, string Value);

/// <summary>Content is base64; the primary input has no name and every later input names its source.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPreviewInputV1(string Content, string? Name = null);

/// <summary>
/// The submitter's view of one preview run. State is <c>queued</c>, <c>running</c>, <c>completed</c>
/// or <c>canceled</c>; a terminal run has an outcome, and only a succeeded run lists outputs.
/// </summary>
public sealed record CatalogPreviewRunV1(
    string RunId,
    CatalogRevisionScopeV1? Revision,
    string RequestFingerprint,
    string CurrentDateTime,
    string State,
    int Attempt,
    int MaxAttempts,
    bool CancelRequested,
    string? Outcome,
    CatalogPreviewFailureV1? Failure,
    CatalogPreviewProvenanceV1? Provenance,
    string? RecordsWritten,
    IReadOnlyList<CatalogPreviewOutputV1> Outputs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    CatalogPreviewDraftSubjectV1? Draft = null)
{
    public string SubjectKind => Draft is null ? "revision" : "draft";
}

public sealed record CatalogPreviewDraftSubjectV1(CatalogMappingScopeV1 Mapping, string DraftId, string Version,
    CatalogRevisionScopeV1 BaseRevision, string NativeDigest, int NativeByteCount);

public sealed record CatalogPreviewFailureV1(string Code, string Message, bool MessageTruncated, string? Subject, bool SubjectTruncated);

public sealed record CatalogPreviewProvenanceV1(bool Confirmed, string EngineRevision, string BackendBuild, string AdapterBuild, string Profile);

/// <summary>Metadata for one output; its exact bytes are read from the run's output route by ordinal.</summary>
public sealed record CatalogPreviewOutputV1(
    int Ordinal,
    string BoundaryId,
    string TargetName,
    bool TargetNameTruncated,
    string MediaType,
    string RecordsWritten,
    string Sha256,
    int ByteCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CsvDocumentDialectV1? CsvDialect = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] XmlDocumentMetadataV1? XmlMetadata = null);

public sealed record XmlDocumentMetadataV1(string Schema, string SafetyProfile, XmlSchemaHintsV1? SchemaHints);
public sealed record XmlSchemaHintsV1(string? NoNamespaceLocation, IReadOnlyList<XmlSchemaLocationV1> Locations);
public sealed record XmlSchemaLocationV1(string Namespace, string Location);


public sealed record CsvDocumentDialectV1(string Schema, string Delimiter, string? Quote, bool QuoteDisabled,
    bool HasHeaderRow, bool Utf8Bom, bool PreserveEmptyStrings);

/// <summary>Outcome is <c>created</c> or <c>replayed</c> for an identical resubmission of the same key.</summary>
public sealed record CatalogPreviewSubmitResultV1(string Outcome, CatalogPreviewRunV1 Run);

/// <summary>Outcome is <c>canceled</c>, <c>requested</c> (the run is executing) or <c>finished</c>.</summary>
public sealed record CatalogPreviewCancelResultV1(string Outcome, CatalogPreviewRunV1 Run);
