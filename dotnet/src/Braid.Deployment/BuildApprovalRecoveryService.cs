using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

public enum BuildApprovalRequestOutcome { Created, Replayed, Conflict, Refused, PreconditionFailed, Unavailable }
public sealed record BuildApprovalRequestResult(BuildApprovalRequestOutcome Outcome, RetainedBuildApproval? Approval = null);

public interface IBuildIntentFactory
{
    /// <summary>Resolve exact trusted source/report/environment/runtime facts without deriving reports or starting a worker.</summary>
    Task<BuildIntent?> CreateAsync(ActorContext actor, MappingScope mapping, Guid approvalId, BuildApprovalSelection selection,
        CancellationToken token = default);
}

/// <summary>Recover the original own approval before considering today's configuration or execution admission.</summary>
public sealed class BuildApprovalRecoveryService(IBuildMetadataReader metadata, IBuildIntentFactory intents,
    IBuildApprovalWriter controls, IProjectReader projects)
{
    public async Task<BuildApprovalRequestResult> PutAsync(ActorContext actor, MappingScope mapping, Guid approvalId,
        BuildApprovalSelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(selection);
        if (approvalId == Guid.Empty || selection.BuildId == Guid.Empty || selection.RevisionId == Guid.Empty ||
            selection.EnvironmentId == Guid.Empty || selection.RegressionEvidenceId == Guid.Empty || selection.EnvironmentVersion < 1)
            throw new ArgumentException("Invalid build approval selectors.");
        if (!await Allowed(actor, mapping, token).ConfigureAwait(false)) return new(BuildApprovalRequestOutcome.Refused);
        var retained = await metadata.ReadApprovalAsync(actor, mapping, approvalId, token).ConfigureAwait(false);
        if (retained is not null)
        {
            Check(retained, actor, mapping, approvalId);
            if (!selection.Matches(retained.Intent)) return new(BuildApprovalRequestOutcome.Conflict);
            return await Allowed(actor, mapping, token).ConfigureAwait(false)
                ? new(BuildApprovalRequestOutcome.Replayed, retained) : new(BuildApprovalRequestOutcome.Refused);
        }
        var intent = await intents.CreateAsync(actor, mapping, approvalId, selection, token).ConfigureAwait(false);
        if (intent is null) return new(BuildApprovalRequestOutcome.PreconditionFailed);
        if (!selection.Matches(intent) || intent.ApprovalId != approvalId || !Belongs(intent, mapping))
            throw new InvalidOperationException("Build intent scope mismatch.");
        if (!await Allowed(actor, mapping, token).ConfigureAwait(false)) return new(BuildApprovalRequestOutcome.Refused);
        var result = await controls.ApproveAsync(actor, intent, token).ConfigureAwait(false);
        if (result == BuildControlOutcome.Conflict) return new(BuildApprovalRequestOutcome.Conflict);
        if (result == BuildControlOutcome.Refused) return new(BuildApprovalRequestOutcome.Refused);
        if (result == BuildControlOutcome.RegressionRequired) return new(BuildApprovalRequestOutcome.PreconditionFailed);
        if (result is not (BuildControlOutcome.Created or BuildControlOutcome.Replayed)) throw new InvalidOperationException("Build approval receipt refused.");
        var saved = await metadata.ReadApprovalAsync(actor, mapping, approvalId, token).ConfigureAwait(false);
        if (saved is null) return new(BuildApprovalRequestOutcome.Unavailable);
        Check(saved, actor, mapping, approvalId);
        if (saved.Intent.Fingerprint != intent.Fingerprint) throw new InvalidOperationException("Build approval identity mismatch.");
        return await Allowed(actor, mapping, token).ConfigureAwait(false)
            ? new(result == BuildControlOutcome.Created ? BuildApprovalRequestOutcome.Created : BuildApprovalRequestOutcome.Replayed, saved)
            : new(BuildApprovalRequestOutcome.Refused);
    }

    private async Task<bool> Allowed(ActorContext actor, MappingScope mapping, CancellationToken token)
    {
        var project = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
        var access = await projects.GetAccessAsync(actor, project, token).ConfigureAwait(false);
        return access is not null && access.PrincipalId == actor.PrincipalId && access.Scope == project && access.Can(ProjectAction.UploadRevision);
    }
    private static bool Belongs(BuildIntent intent, MappingScope mapping) => intent.Revision.OrganizationId == mapping.OrganizationId &&
        intent.Revision.ProjectId == mapping.ProjectId && intent.Revision.MappingId == mapping.MappingId;
    private static void Check(RetainedBuildApproval value, ActorContext actor, MappingScope mapping, Guid id)
    {
        if (value.Actor != actor.PrincipalId || value.Intent.ApprovalId != id || !Belongs(value.Intent, mapping))
            throw new InvalidOperationException("Build approval scope mismatch.");
    }
}
