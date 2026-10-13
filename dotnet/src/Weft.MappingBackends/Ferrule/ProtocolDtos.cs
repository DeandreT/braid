using System.Text.Json.Serialization;
using Weft.Contracts.Projections;

namespace Weft.MappingBackends.Ferrule;

internal sealed record HostRevision(string OrganizationId, string ProjectId, string MappingId, string RevisionId);
internal sealed record HostLimits(int MaximumDiagnostics, int MaximumItems, int MaximumTextBytes);
internal sealed record HostRequest(string ProtocolVersion, string RequestId, string Operation, HostRevision Revision, string NativeDigest, string DocumentId, HostLimits Limits);
internal sealed record HostResponse(string ProtocolVersion, string? RequestId, string? Operation, HostRevision? Revision, string? NativeDigest, string? DocumentId, string EngineRevision, HostResult? Result, HostFailure? Failure);
internal sealed record HostFailure(string Code, string Message, bool MessageTruncated, NativeLocator? NativeLocator);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HostInspectionResult), "inspect")]
[JsonDerivedType(typeof(HostValidationResult), "validate")]
[JsonDerivedType(typeof(HostProjectionResult), "project")]
internal abstract record HostResult;

internal sealed record HostProjectionResult(
    ProjectionCoverage Coverage,
    IReadOnlyList<ProjectionBoundary> Boundaries,
    IReadOnlyList<ProjectionField> Fields,
    IReadOnlyList<ProjectionParameter> Parameters,
    IReadOnlyList<ProjectionRule> Rules,
    IReadOnlyList<ProjectionControl> Controls,
    IReadOnlyList<ProjectionRelationship> Relationships,
    IReadOnlyList<ProjectionDiagnostic> Diagnostics,
    IReadOnlyList<OperationCapability> Capabilities) : HostResult;

internal sealed record HostInspectionResult(
    IReadOnlyList<HostBoundary> Boundaries,
    HostCounts Counts,
    IReadOnlyList<HostResource> UnresolvedResources,
    IReadOnlyList<OperationCapability> Capabilities,
    bool Truncated) : HostResult;

internal sealed record HostBoundary(string Id, string Role, string Name, bool NameTruncated, string RootName, bool RootNameTruncated, string Shape, bool Repeating, NativeLocator NativeLocator);
internal sealed record HostCounts(uint SchemaNodes, uint GraphNodes, uint ScopeNodes, uint UserFunctions, uint FailureRules, uint TotalItems);
internal sealed record HostResource(string BoundaryId, string Kind, string Resolution, NativeLocator NativeLocator);
internal sealed record HostValidationResult(bool IsValid, uint TotalIssueCount, uint EmittedIssueCount, bool Truncated, IReadOnlyList<HostValidationIssue> Issues) : HostResult;
internal sealed record HostValidationIssue(string Code, string Location, bool LocationTruncated, string Message, bool MessageTruncated, MappingDiagnosticOwner? Owner, NativeLocator NativeLocator);
