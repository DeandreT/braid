using Weft.Application;
using Weft.Domain;

namespace Weft.Deployment;

public enum BuildSubmissionRequestOutcome { Created, Replayed, Conflict, Refused, PreconditionFailed, Busy, Unavailable }
public sealed record BuildSubmissionRequestResult(BuildSubmissionRequestOutcome Outcome, BuildRunSnapshot? Run = null);

/// <summary>Recover an existing run before active approval/current configuration checks; new work uses the stored exact intent.</summary>
public sealed class BuildSubmissionService(IBuildMetadataReader metadata, IBuildRunReader reader, IBuildRunCommands commands,
    IBuildAdmissionReader admissions, IProjectReader projects, BuildBackendIdentity backend, BuildRuntimeProfile profile,
    BuildBackendIdentity? documentBackend = null, BuildRuntimeProfile? documentProfile = null, BuildRuntimeProfile? interchangeProfile = null,
    BuildBackendIdentity? csvBackend = null, BuildRuntimeProfile? csvProfile = null)
{
    public async Task<BuildSubmissionRequestResult> PutAsync(ActorContext actor, MappingScope mapping, Guid buildId, Guid approvalId,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        if (buildId == Guid.Empty || approvalId == Guid.Empty) throw new ArgumentException("Invalid build submission selectors.");
        if (!await Allowed(actor, mapping, token).ConfigureAwait(false)) return new(BuildSubmissionRequestOutcome.Refused);
        var existing = await reader.ReadAsync(actor, mapping.OrganizationId, buildId, token).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!Belongs(existing.Intent, mapping)) return new(BuildSubmissionRequestOutcome.Refused);
            Check(existing, actor, mapping, buildId);
            if (existing.Intent.ApprovalId != approvalId) return new(BuildSubmissionRequestOutcome.Conflict);
            return await Allowed(actor, mapping, token).ConfigureAwait(false)
                ? new(BuildSubmissionRequestOutcome.Replayed, existing) : new(BuildSubmissionRequestOutcome.Refused);
        }
        var approval = await metadata.ReadApprovalAsync(actor, mapping, approvalId, token).ConfigureAwait(false);
        if (approval is null) return new(BuildSubmissionRequestOutcome.Refused);
        var intent = approval.Intent;
        if (approval.Actor != actor.PrincipalId || intent.ApprovalId != approvalId || !Belongs(intent, mapping)) throw Inconsistent();
        if (intent.BuildId != buildId) return new(BuildSubmissionRequestOutcome.Conflict);
        if (approval.Revoked || !approval.EnvironmentActive ||
            !(intent.Backend == backend && intent.Profile == profile || documentBackend is not null &&
                intent.Backend == documentBackend && (intent.Profile == documentProfile || intent.Profile == interchangeProfile) ||
                csvBackend is not null && csvProfile is not null && intent.Backend == csvBackend && intent.Profile == csvProfile &&
                    intent.Profile.Id == BuildWorkerProtocol.CsvProfile && BuildWorkerProtocol.AdmitsCsvPlan(intent.Boundaries)))
            return new(BuildSubmissionRequestOutcome.PreconditionFailed);
        var admitted = await admissions.ReadAsync(actor, intent, token).ConfigureAwait(false);
        if (admitted is null) return new(BuildSubmissionRequestOutcome.Refused);
        if (admitted.Actor != actor.PrincipalId || admitted.IntentFingerprint != intent.Fingerprint || admitted.Native.Id.Scope != intent.Revision ||
            admitted.Native.Digest != intent.NativeDigest || admitted.Native.Id.Class != PrivateObjectClass.NativeArtifact) throw Inconsistent();
        var submitted = await commands.SubmitAsync(actor, intent, token).ConfigureAwait(false);
        if (submitted == BuildRunSubmitOutcome.Conflict) return new(BuildSubmissionRequestOutcome.Conflict);
        if (submitted == BuildRunSubmitOutcome.Refused) return new(BuildSubmissionRequestOutcome.Refused);
        if (submitted == BuildRunSubmitOutcome.Busy) return new(BuildSubmissionRequestOutcome.Busy);
        if (submitted is not (BuildRunSubmitOutcome.Created or BuildRunSubmitOutcome.Replayed)) throw Inconsistent();
        var saved = await reader.ReadAsync(actor, mapping.OrganizationId, buildId, token).ConfigureAwait(false);
        if (saved is null) return new(BuildSubmissionRequestOutcome.Unavailable);
        Check(saved, actor, mapping, buildId);
        if (saved.Intent.Fingerprint != intent.Fingerprint) throw Inconsistent();
        return await Allowed(actor, mapping, token).ConfigureAwait(false)
            ? new(submitted == BuildRunSubmitOutcome.Created ? BuildSubmissionRequestOutcome.Created : BuildSubmissionRequestOutcome.Replayed, saved)
            : new(BuildSubmissionRequestOutcome.Refused);
    }

    private async Task<bool> Allowed(ActorContext actor, MappingScope mapping, CancellationToken token)
    {
        var project = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
        var access = await projects.GetAccessAsync(actor, project, token).ConfigureAwait(false);
        return access is not null && access.PrincipalId == actor.PrincipalId && access.Scope == project && access.Can(ProjectAction.UploadRevision);
    }
    private static bool Belongs(BuildIntent intent, MappingScope mapping) => intent.Revision.OrganizationId == mapping.OrganizationId &&
        intent.Revision.ProjectId == mapping.ProjectId && intent.Revision.MappingId == mapping.MappingId;
    private static void Check(BuildRunSnapshot value, ActorContext actor, MappingScope mapping, Guid id)
    {
        if (value.Actor != actor.PrincipalId || value.Intent.BuildId != id || !Belongs(value.Intent, mapping)) throw Inconsistent();
    }
    private static InvalidOperationException Inconsistent() => new("Build submission identity mismatch.");
}
