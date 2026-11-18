using System.Text.Json.Serialization;
using Braid.Contracts.Catalog;

namespace Braid.Contracts.Regression;

public sealed record RegressionRuntimeV1(string BackendId, string EngineRevision, string BackendBuild, string AdapterBuild, string Profile);
public sealed record RegressionLimitsV1(int MaximumInputBytes, int MaximumOutputBytes, int MaximumArtifacts, int MaximumTextBytes);
public sealed record RegressionTargetV1(string Kind, string? Name);
public sealed record RegressionInputDocumentV1(string? Name, string BytesBase64Url);
public sealed record RegressionDocumentV1(string? Name, string BytesBase64Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MediaType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CsvDocumentDialectV1? CsvDialect = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] XmlDocumentMetadataV1? XmlMetadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeDocumentSettingsV1? DocumentSettings = null);
public sealed record RegressionEdiComparisonOptionsV1(bool CompareSeparators, bool IgnoreControlNumbers, bool IgnoreDates);
public sealed record RegressionParameterV1(string Name, string Kind, string Value);

/// <summary>Only sample data and explicit selectors come from the caller; no owner, digest or execution authority.</summary>
public sealed record RegressionCasePutV1(string Name, string RevisionId, RegressionRuntimeV1 Runtime,
    string CurrentDateTime, RegressionTargetV1 Target, RegressionLimitsV1 Limits,
    string PrimaryBase64Url, IReadOnlyList<RegressionInputDocumentV1> NamedInputs,
    IReadOnlyList<RegressionParameterV1> Parameters, string ComparisonPolicy, IReadOnlyList<RegressionDocumentV1> Expected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionEdiComparisonOptionsV1? ComparisonOptions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeDocumentSettingsV1? PrimaryDocumentSettings = null);

public sealed record RegressionCaseCapabilitiesV1(RegressionRuntimeV1 Runtime, RegressionLimitsV1 Limits,
    string ComparisonPolicy, bool ReadSamples, bool PublishCases,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionRuntimeV1? DocumentRuntime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ComparisonPolicies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionRuntimeV1? CsvRuntime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionRuntimeV1? XmlRuntime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RegressionRuntimeV1>? NativeRuntimes = null);

public sealed record RegressionCaseMetadataV1(string CaseId, string Version, CatalogRevisionScopeV1 Revision,
    string Name, string CreatedAtUtc);
public sealed record RegressionCasePageV1(IReadOnlyList<RegressionCaseMetadataV1> Cases, string? NextCaseId);
public sealed record RegressionCaseSavedV1(string CaseId, string Version, CatalogRevisionScopeV1 Revision, string Outcome);
public sealed record RegressionCaseDetailV1(string CaseId, string Version, CatalogRevisionScopeV1 Revision,
    RegressionCasePutV1 Definition);
