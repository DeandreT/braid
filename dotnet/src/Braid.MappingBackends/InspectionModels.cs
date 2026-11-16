using System.Text.Json.Serialization;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.MappingBackends;

public enum MappingOperationStatus
{
    Succeeded,
    InvalidMapping,
    UnsupportedArtifact,
    ResourceLimit,
    ProtocolFailure,
    BackendUnavailable,
    BackendFailure,
    TimedOut,
    Canceled,
    IsolationFailure
}

public sealed record MappingOperationProvenance(
    RevisionScope Scope,
    Sha256Digest NativeDigest,
    string BackendId,
    string EngineRevision,
    string BackendBuild,
    string AdapterBuild,
    bool Confirmed);

public sealed record MappingDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    bool MessageTruncated,
    NativeLocator? NativeLocator = null,
    MappingDiagnosticOwner? Owner = null,
    string? Location = null,
    bool LocationTruncated = false);

public enum InspectionBoundaryShape
{
    Scalar,
    ScalarUnion,
    Object
}

public sealed record MappingBoundaryDescription(
    string Id,
    BoundaryRole Role,
    string Name,
    bool NameTruncated,
    string RootName,
    bool RootNameTruncated,
    InspectionBoundaryShape Shape,
    bool Repeating,
    NativeLocator NativeLocator);

public sealed record MappingInspectionCounts(
    int SchemaNodes,
    int GraphNodes,
    int ScopeNodes,
    int UserFunctions,
    int FailureRules,
    int TotalItems);

public enum UnresolvedResourceKind
{
    DesignTimeInput,
    DesignTimeOutput,
    DynamicInput,
    DynamicOutput,
    EdiConfiguration,
    ExternalSource,
    XmlSchemaHint,
    SchemaReference
}

public sealed record UnresolvedMappingResource(string BoundaryId, UnresolvedResourceKind Kind, NativeLocator NativeLocator);

public sealed record MappingInspectionData(
    IReadOnlyList<MappingBoundaryDescription> Boundaries,
    MappingInspectionCounts Counts,
    IReadOnlyList<UnresolvedMappingResource> UnresolvedResources,
    IReadOnlyList<OperationCapability> Capabilities,
    bool Truncated);

public sealed record MappingInspectionResult(
    MappingOperationStatus Status,
    MappingOperationProvenance Provenance,
    MappingInspectionData? Inspection,
    IReadOnlyList<MappingDiagnostic> Diagnostics);

public sealed record MappingValidationResult(
    MappingOperationStatus Status,
    MappingOperationProvenance Provenance,
    bool? IsValid,
    int TotalIssueCount,
    int EmittedIssueCount,
    bool Truncated,
    IReadOnlyList<MappingDiagnostic> Diagnostics);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GraphNodeDiagnosticOwner), "graphNode")]
[JsonDerivedType(typeof(UserFunctionDiagnosticOwner), "userFunction")]
[JsonDerivedType(typeof(EndpointDiagnosticOwner), "endpoint")]
[JsonDerivedType(typeof(SchemaNodeDiagnosticOwner), "schemaNode")]
[JsonDerivedType(typeof(ScopeDiagnosticOwner), "scope")]
[JsonDerivedType(typeof(FailureRuleDiagnosticOwner), "failureRule")]
public abstract record MappingDiagnosticOwner;

public sealed record GraphNodeDiagnosticOwner(string? FunctionId, string NodeId) : MappingDiagnosticOwner;
public sealed record UserFunctionDiagnosticOwner(string FunctionId) : MappingDiagnosticOwner;
public sealed record EndpointDiagnosticOwner(MappingEndpoint Endpoint) : MappingDiagnosticOwner;
public sealed record SchemaNodeDiagnosticOwner(MappingEndpoint Endpoint, IReadOnlyList<SchemaOwnerStep> Path) : MappingDiagnosticOwner;
public sealed record ScopeDiagnosticOwner(MappingEndpoint Target, IReadOnlyList<ScopeOwnerStep> Path) : MappingDiagnosticOwner;
public sealed record FailureRuleDiagnosticOwner(string Index) : MappingDiagnosticOwner;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SourceMappingEndpoint), "source")]
[JsonDerivedType(typeof(TargetMappingEndpoint), "target")]
[JsonDerivedType(typeof(NamedSourceMappingEndpoint), "namedSource")]
[JsonDerivedType(typeof(NamedTargetMappingEndpoint), "namedTarget")]
public abstract record MappingEndpoint;

public sealed record SourceMappingEndpoint : MappingEndpoint;
public sealed record TargetMappingEndpoint : MappingEndpoint;
public sealed record NamedSourceMappingEndpoint(string Index, string Name) : MappingEndpoint;
public sealed record NamedTargetMappingEndpoint(string Index, string Name) : MappingEndpoint;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChildSchemaOwnerStep), "child")]
[JsonDerivedType(typeof(DynamicFieldSchemaOwnerStep), "dynamicField")]
[JsonDerivedType(typeof(ContainsSchemaOwnerStep), "contains")]
[JsonDerivedType(typeof(DependentSchemaOwnerStep), "dependentSchema")]
public abstract record SchemaOwnerStep;

public sealed record ChildSchemaOwnerStep(string Index) : SchemaOwnerStep;
public sealed record DynamicFieldSchemaOwnerStep : SchemaOwnerStep;
public sealed record ContainsSchemaOwnerStep(string Index) : SchemaOwnerStep;
public sealed record DependentSchemaOwnerStep(string Index) : SchemaOwnerStep;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChildScopeOwnerStep), "child")]
[JsonDerivedType(typeof(DynamicChildScopeOwnerStep), "dynamicChild")]
[JsonDerivedType(typeof(SegmentScopeOwnerStep), "segment")]
public abstract record ScopeOwnerStep;

public sealed record ChildScopeOwnerStep(string Index) : ScopeOwnerStep;
public sealed record DynamicChildScopeOwnerStep(string Index) : ScopeOwnerStep;
public sealed record SegmentScopeOwnerStep(string Index) : ScopeOwnerStep;
