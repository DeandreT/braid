namespace Weft.Contracts.Projections;

/// <summary>A revision-specific read model. It does not execute mapping expressions.</summary>
public sealed record ProjectionDocumentV1(
    ProjectionProvenance Provenance,
    ProjectionCoverage Coverage,
    IReadOnlyList<ProjectionBoundary> Boundaries,
    IReadOnlyList<ProjectionField> Fields,
    IReadOnlyList<ProjectionParameter> Parameters,
    IReadOnlyList<ProjectionRule> Rules,
    IReadOnlyList<ProjectionControl> Controls,
    IReadOnlyList<ProjectionRelationship> Relationships,
    IReadOnlyList<ProjectionDiagnostic> Diagnostics,
    IReadOnlyList<OperationCapability> Capabilities)
{
    public string SchemaVersion => "weft.mapping-projection.v1";
}
