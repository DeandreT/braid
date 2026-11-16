using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Retained metadata is not a fresh execution admission or a private artifact read.</summary>
public sealed record RetainedBuildEnvironment(BuildEnvironment Environment, PrincipalId Creator, DateTimeOffset CreatedAt);
public sealed record RetainedBuildApproval(PrincipalId Actor, BuildIntent Intent, bool Revoked, bool EnvironmentActive, DateTimeOffset CreatedAt);
public sealed record BuildEnvironmentCursor(Guid EnvironmentId, long Version);
public sealed record BuildEnvironmentPage(IReadOnlyList<RetainedBuildEnvironment> Environments, BuildEnvironmentCursor? Next);
public sealed record BuildApprovalPage(IReadOnlyList<RetainedBuildApproval> Approvals, Guid? NextApprovalId);
public sealed record BuildRunSummary(PrincipalId Actor, BuildIntent Intent, BuildRunState State, int Attempt,
    DateTimeOffset CreatedAt, BuildExecutionOutcome? Outcome, BuildArtifactReference? Artifact);
public sealed record BuildRunPage(IReadOnlyList<BuildRunSummary> Runs, Guid? NextBuildId);

/// <summary>Actor-bound, bounded metadata queries. No native/artifact bytes or worker claim capabilities.</summary>
public interface IBuildMetadataReader
{
    Task<RetainedBuildEnvironment?> ReadEnvironmentAsync(ActorContext actor, BuildEnvironmentReference environment, CancellationToken token = default);
    Task<BuildEnvironmentPage?> ListEnvironmentsAsync(ActorContext actor, ProjectScope project, int maximum,
        BuildEnvironmentCursor? after = null, CancellationToken token = default);
    Task<RetainedBuildApproval?> ReadApprovalAsync(ActorContext actor, MappingScope mapping, Guid approvalId, CancellationToken token = default);
    Task<BuildApprovalPage?> ListApprovalsAsync(ActorContext actor, MappingScope mapping, int maximum,
        Guid? afterApprovalId = null, CancellationToken token = default);
    Task<BuildRunPage?> ListRunsAsync(ActorContext actor, MappingScope mapping, int maximum,
        Guid? afterBuildId = null, CancellationToken token = default);
}

/// <summary>Exact client selections. Source, runtime and physical boundaries come from trusted readers.</summary>
public sealed record BuildApprovalSelection(Guid BuildId, Guid RevisionId, Guid EnvironmentId, long EnvironmentVersion, Guid RegressionEvidenceId)
{
    public bool Matches(BuildIntent intent) => BuildId == intent.BuildId && RevisionId == intent.Revision.RevisionId &&
        EnvironmentId == intent.Environment.EnvironmentId && EnvironmentVersion == intent.Environment.Version &&
        RegressionEvidenceId == intent.RegressionEvidenceId;
}
