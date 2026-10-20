using Weft.Contracts.Catalog;

namespace Weft.Contracts.Regression;

public sealed record RegressionRuntimeV1(string BackendId, string EngineRevision, string BackendBuild, string AdapterBuild, string Profile);
public sealed record RegressionLimitsV1(int MaximumInputBytes, int MaximumOutputBytes, int MaximumArtifacts, int MaximumTextBytes);
public sealed record RegressionTargetV1(string Kind, string? Name);
public sealed record RegressionDocumentV1(string? Name, string BytesBase64Url);
public sealed record RegressionParameterV1(string Name, string Kind, string Value);

/// <summary>Only sample data and explicit selectors come from the caller; no owner, digest or execution authority.</summary>
public sealed record RegressionCasePutV1(string Name, string RevisionId, RegressionRuntimeV1 Runtime,
    string CurrentDateTime, RegressionTargetV1 Target, RegressionLimitsV1 Limits,
    string PrimaryBase64Url, IReadOnlyList<RegressionDocumentV1> NamedInputs,
    IReadOnlyList<RegressionParameterV1> Parameters, string ComparisonPolicy, IReadOnlyList<RegressionDocumentV1> Expected);

public sealed record RegressionCaseCapabilitiesV1(RegressionRuntimeV1 Runtime, RegressionLimitsV1 Limits,
    string ComparisonPolicy, bool ReadSamples, bool PublishCases);

public sealed record RegressionCaseMetadataV1(string CaseId, string Version, CatalogRevisionScopeV1 Revision,
    string Name, string CreatedAtUtc);
public sealed record RegressionCasePageV1(IReadOnlyList<RegressionCaseMetadataV1> Cases, string? NextCaseId);
public sealed record RegressionCaseSavedV1(string CaseId, string Version, CatalogRevisionScopeV1 Revision, string Outcome);
public sealed record RegressionCaseDetailV1(string CaseId, string Version, CatalogRevisionScopeV1 Revision,
    RegressionCasePutV1 Definition);
