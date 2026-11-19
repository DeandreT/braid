using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

public sealed record BuildEnvironmentSelection(string ProfileId, long ProfileVersion)
{
    public bool Matches(BuildRuntimeProfile profile) => ProfileId == profile.Id && ProfileVersion == profile.Version;
}
public enum BuildEnvironmentRequestOutcome { Created, Replayed, Conflict, Refused, PreconditionFailed, Unavailable }
public sealed record BuildEnvironmentRequestResult(BuildEnvironmentRequestOutcome Outcome, RetainedBuildEnvironment? Environment = null);
public enum BuildApprovalRevocationOutcome { Observed, Refused, Unavailable }
public sealed record BuildApprovalRevocationResult(BuildApprovalRevocationOutcome Outcome, RetainedBuildApproval? Approval = null);

public interface IBuildEnvironmentCommands
{
    Task<BuildControlOutcome> SaveEnvironmentAsync(ActorContext actor, BuildEnvironment environment, CancellationToken token = default);
}
public interface IBuildApprovalRevocation
{
    Task<bool> RevokeAsync(ActorContext actor, Guid organizationId, Guid projectId, Guid approvalId, CancellationToken token = default);
}

/// <summary>Administrator version creation and own revocation never imply fresh approval or compilation.</summary>
public sealed class BuildAdministrationService(IBuildMetadataReader metadata, IBuildEnvironmentCommands environments,
    IBuildApprovalRevocation approvals, IProjectReader projects, BuildRuntimeProfile profile, BuildRuntimeProfile? documentProfile = null,
    BuildRuntimeProfile? interchangeProfile = null, BuildRuntimeProfile? csvProfile = null, BuildRuntimeProfile? xmlProfile = null)
{
    public async Task<BuildEnvironmentRequestResult> PutEnvironmentAsync(ActorContext actor, BuildEnvironmentReference reference,
        BuildEnvironmentSelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(reference); ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.ProfileId) || selection.ProfileVersion < 1) throw new ArgumentException("Invalid environment selectors.");
        if (!await Allowed(actor, reference.Project, true, token).ConfigureAwait(false)) return new(BuildEnvironmentRequestOutcome.Refused);
        var retained = await metadata.ReadEnvironmentAsync(actor, reference, token).ConfigureAwait(false);
        if (retained is not null)
        {
            if (retained.Environment.Reference != reference) throw Inconsistent();
            if (!selection.Matches(retained.Environment.Profile)) return new(BuildEnvironmentRequestOutcome.Conflict);
            return await Allowed(actor, reference.Project, true, token).ConfigureAwait(false)
                ? new(BuildEnvironmentRequestOutcome.Replayed, retained) : new(BuildEnvironmentRequestOutcome.Refused);
        }
        var selectedProfile = selection.Matches(profile) ? profile : documentProfile is not null && selection.Matches(documentProfile) ? documentProfile :
            interchangeProfile is not null && selection.Matches(interchangeProfile) ? interchangeProfile :
            csvProfile is not null && csvProfile.Id == BuildWorkerProtocol.CsvProfile && selection.Matches(csvProfile) ? csvProfile :
            xmlProfile?.Id == BuildWorkerProtocol.XmlProfile && selection.Matches(xmlProfile) ? xmlProfile : null;
        if (selectedProfile is null) return new(BuildEnvironmentRequestOutcome.PreconditionFailed);
        var result = await environments.SaveEnvironmentAsync(actor, new(reference, selectedProfile, true), token).ConfigureAwait(false);
        if (result == BuildControlOutcome.Refused) return new(BuildEnvironmentRequestOutcome.Refused);
        if (result == BuildControlOutcome.Conflict) return new(BuildEnvironmentRequestOutcome.Conflict);
        if (result is not (BuildControlOutcome.Created or BuildControlOutcome.Replayed)) throw Inconsistent();
        var saved = await metadata.ReadEnvironmentAsync(actor, reference, token).ConfigureAwait(false);
        if (saved is null) return new(BuildEnvironmentRequestOutcome.Unavailable);
        if (saved.Environment.Reference != reference || saved.Environment.Profile != selectedProfile) throw Inconsistent();
        return await Allowed(actor, reference.Project, true, token).ConfigureAwait(false)
            ? new(result == BuildControlOutcome.Created ? BuildEnvironmentRequestOutcome.Created : BuildEnvironmentRequestOutcome.Replayed, saved)
            : new(BuildEnvironmentRequestOutcome.Refused);
    }

    public async Task<BuildApprovalRevocationResult> RevokeOwnApprovalAsync(ActorContext actor, MappingScope mapping, Guid approvalId,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        if (approvalId == Guid.Empty) throw new ArgumentException("Invalid approval identity.");
        var project = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
        if (!await Allowed(actor, project, false, token).ConfigureAwait(false)) return new(BuildApprovalRevocationOutcome.Refused);
        var original = await metadata.ReadApprovalAsync(actor, mapping, approvalId, token).ConfigureAwait(false);
        if (original is null) return new(BuildApprovalRevocationOutcome.Refused);
        Check(original);
        if (!original.Revoked && !await approvals.RevokeAsync(actor, mapping.OrganizationId, mapping.ProjectId, approvalId, token).ConfigureAwait(false))
            return new(BuildApprovalRevocationOutcome.Refused);
        var saved = await metadata.ReadApprovalAsync(actor, mapping, approvalId, token).ConfigureAwait(false);
        if (saved is null) return new(BuildApprovalRevocationOutcome.Refused);
        Check(saved);
        if (saved.Intent.Fingerprint != original.Intent.Fingerprint) throw Inconsistent();
        if (!saved.Revoked) return new(BuildApprovalRevocationOutcome.Unavailable);
        return await Allowed(actor, project, false, token).ConfigureAwait(false)
            ? new(BuildApprovalRevocationOutcome.Observed, saved) : new(BuildApprovalRevocationOutcome.Refused);

        void Check(RetainedBuildApproval value)
        {
            if (value.Actor != actor.PrincipalId || value.Intent.ApprovalId != approvalId || value.Intent.Revision.OrganizationId != mapping.OrganizationId ||
                value.Intent.Revision.ProjectId != mapping.ProjectId || value.Intent.Revision.MappingId != mapping.MappingId) throw Inconsistent();
        }
    }

    private async Task<bool> Allowed(ActorContext actor, ProjectScope project, bool administrator, CancellationToken token)
    {
        var access = await projects.GetAccessAsync(actor, project, token).ConfigureAwait(false);
        return access is not null && access.PrincipalId == actor.PrincipalId && access.Scope == project &&
            (administrator ? access.Role == ProjectRole.Administrator : access.Can(ProjectAction.UploadRevision));
    }
    private static InvalidOperationException Inconsistent() => new("Build administration identity mismatch.");
}
