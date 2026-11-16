using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.MappingBackends;

/// <summary>Private authored bytes, not an admitted revision and never save authority.</summary>
public sealed class MappingAuthoringCheckpoint
{
    private readonly byte[] native;
    internal MappingAuthoringCheckpoint(ProjectScope project, Guid authoringId, Sha256Digest planDigest,
        byte[] native, MappingAuthoringValidation validation, MappingAuthoringProjection projection, string workerBuild)
    {
        Project = project; AuthoringId = authoringId; PlanDigest = planDigest;
        this.native = native.ToArray(); NativeDigest = Sha256Digest.FromBytes(native);
        Validation = validation; Projection = projection; WorkerBuild = workerBuild;
    }
    public ProjectScope Project { get; }
    public Guid AuthoringId { get; }
    public Sha256Digest PlanDigest { get; }
    public Sha256Digest NativeDigest { get; }
    public int NativeByteCount => native.Length;
    public byte[] CopyNativeBytes() => native.ToArray();
    public string EngineRevision => FerruleInspectionBackend.EngineRevision;
    public string WorkerBuild { get; }
    public MappingAuthoringValidation Validation { get; }
    public MappingAuthoringProjection Projection { get; }
    public override string ToString() => nameof(MappingAuthoringCheckpoint);
}

/// <summary>Reports have project/session provenance, not an invented revision identity.</summary>
public sealed record MappingAuthoringValidation(bool IsValid, int TotalIssueCount, bool Truncated,
    IReadOnlyList<MappingDiagnostic> Issues);
public sealed record MappingAuthoringProjection(ProjectionCoverage Coverage,
    IReadOnlyList<ProjectionBoundary> Boundaries, IReadOnlyList<ProjectionField> Fields,
    IReadOnlyList<ProjectionParameter> Parameters, IReadOnlyList<ProjectionRule> Rules,
    IReadOnlyList<ProjectionControl> Controls, IReadOnlyList<ProjectionRelationship> Relationships,
    IReadOnlyList<ProjectionDiagnostic> Diagnostics, IReadOnlyList<OperationCapability> Capabilities);
public sealed record MappingAuthoringResult(MappingAuthoringCheckpoint? Checkpoint, MappingEditFailure? Failure);

public static class MappingAuthoringPolicy
{
    public const int MaximumPlanBytes = 1024 * 1024;
    public const int MaximumRequestBytes = InspectionPolicy.MaximumHeaderBytes + MaximumPlanBytes + 8;
    public const int MaximumResponseBytes = MappingEditPolicy.MaximumReportBytes + MappingEditPolicy.MaximumNativeBytes + 8;
}
