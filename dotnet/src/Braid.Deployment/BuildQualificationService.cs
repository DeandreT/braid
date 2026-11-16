using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Deployment;

public enum BuildQualificationWriteOutcome { Created, Replayed, Conflict, Refused, RegressionRequired }
public interface IBuildQualificationStore
{
    Task<BuildQualification?> ReadAsync(ActorContext actor, MappingScope mapping, Guid qualificationId, CancellationToken token = default);
    /// <summary>Atomically recheck authority and original build/evidence identities; retain exact first-writer proof/result bytes.</summary>
    Task<BuildQualificationWriteOutcome> SaveAsync(ActorContext actor, BuildQualification qualification, CancellationToken token = default);
}
public enum BuildQualificationRequestOutcome { Created, Replayed, Conflict, Refused, NotQualified, Unavailable }
public sealed record BuildQualificationRequestResult(BuildQualificationRequestOutcome Outcome, BuildQualification? Qualification = null);

/// <summary>Qualification comes from actual compiled outputs, never a caller-supplied pass flag or current preview output.</summary>
public sealed class BuildQualificationService(IBuildQualificationStore store, IBuildRunReader builds,
    IRegressionRunReader evidence, IRegressionCaseReader cases, CompiledBuildExecution execution, IProjectReader projects)
{
    public async Task<BuildQualificationRequestResult> PutAsync(ActorContext actor, MappingScope mapping, Guid qualificationId,
        Guid buildId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        token.ThrowIfCancellationRequested();
        if (qualificationId == Guid.Empty || buildId == Guid.Empty) throw new ArgumentException("Qualification and build identifiers are required.");
        if (!await Allowed(actor, mapping, token).ConfigureAwait(false)) return new(BuildQualificationRequestOutcome.Refused);
        var existing = await store.ReadAsync(actor, mapping, qualificationId, token).ConfigureAwait(false);
        if (existing is not null)
        {
            Check(existing, actor, mapping, qualificationId);
            if (existing.Artifact.Intent.BuildId != buildId) return new(BuildQualificationRequestOutcome.Conflict);
            return await Allowed(actor, mapping, token).ConfigureAwait(false)
                ? new(BuildQualificationRequestOutcome.Replayed, existing) : new(BuildQualificationRequestOutcome.Refused);
        }
        var build = await builds.ReadAsync(actor, mapping.OrganizationId, buildId, token).ConfigureAwait(false);
        if (build is null || build.Actor != actor.PrincipalId || build.Intent.BuildId != buildId || !Belongs(build.Intent.Revision, mapping))
            return new(BuildQualificationRequestOutcome.Refused);
        if (build.State != BuildRunState.Succeeded || build.Artifact is null) return new(BuildQualificationRequestOutcome.NotQualified);
        var original = await evidence.ReadAsync(actor, mapping.OrganizationId, build.Intent.RegressionEvidenceId, token).ConfigureAwait(false);
        if (original is null || original.Submitter != actor.PrincipalId || original.Case.Revision != build.Intent.Revision)
            return new(BuildQualificationRequestOutcome.Refused);
        var baseline = await cases.ReadAsync(actor, mapping, original.Case.CaseId, original.Case.Version, token).ConfigureAwait(false);
        if (baseline is null) return new(BuildQualificationRequestOutcome.Refused);
        try { _ = BuildQualification.ValidateSource(actor.PrincipalId, build, original, baseline, token); }
        catch (BuildQualificationException) { return new(BuildQualificationRequestOutcome.NotQualified); }
        var image = execution.RuntimeImage;
        var result = await execution.ExecuteQualifiedAsync(actor, mapping, build.Artifact, baseline.Replay, image, token).ConfigureAwait(false);
        if (result.Outcome == CompiledBuildOutcome.Refused) return new(BuildQualificationRequestOutcome.Refused);
        if (result.Outcome is CompiledBuildOutcome.Unavailable or CompiledBuildOutcome.Busy) return new(BuildQualificationRequestOutcome.Unavailable);
        if (result.Outcome != CompiledBuildOutcome.Completed) return new(BuildQualificationRequestOutcome.NotQualified);
        if (execution.RuntimeImage != image) throw new InvalidOperationException("Runtime identity changed during qualification.");
        BuildQualification candidate;
        try { candidate = BuildQualification.Create(qualificationId, actor.PrincipalId, build, original, baseline, image, result, token); }
        catch (BuildQualificationException) { return new(BuildQualificationRequestOutcome.NotQualified); }
        if (!await Allowed(actor, mapping, token).ConfigureAwait(false)) return new(BuildQualificationRequestOutcome.Refused);
        var write = await store.SaveAsync(actor, candidate, token).ConfigureAwait(false);
        if (write == BuildQualificationWriteOutcome.Refused) return new(BuildQualificationRequestOutcome.Refused);
        if (write == BuildQualificationWriteOutcome.RegressionRequired) return new(BuildQualificationRequestOutcome.NotQualified);
        if (write == BuildQualificationWriteOutcome.Conflict) return new(BuildQualificationRequestOutcome.Conflict);
        if (write is not (BuildQualificationWriteOutcome.Created or BuildQualificationWriteOutcome.Replayed)) throw new InvalidOperationException("Invalid qualification receipt.");
        var saved = await store.ReadAsync(actor, mapping, qualificationId, token).ConfigureAwait(false);
        if (saved is null) return new(BuildQualificationRequestOutcome.Unavailable);
        Check(saved, actor, mapping, qualificationId);
        if (saved.Fingerprint != candidate.Fingerprint) throw new InvalidOperationException("Qualification proof changed during persistence.");
        return await Allowed(actor, mapping, token).ConfigureAwait(false)
            ? new(write == BuildQualificationWriteOutcome.Created ? BuildQualificationRequestOutcome.Created : BuildQualificationRequestOutcome.Replayed, saved)
            : new(BuildQualificationRequestOutcome.Refused);
    }

    private async Task<bool> Allowed(ActorContext actor, MappingScope mapping, CancellationToken token)
    {
        var scope = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
        var access = await projects.GetAccessAsync(actor, scope, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return access is not null && access.PrincipalId == actor.PrincipalId && access.Scope == scope && access.Can(ProjectAction.UploadRevision);
    }
    private static bool Belongs(RevisionScope revision, MappingScope mapping) => revision.OrganizationId == mapping.OrganizationId &&
        revision.ProjectId == mapping.ProjectId && revision.MappingId == mapping.MappingId;
    private static void Check(BuildQualification value, ActorContext actor, MappingScope mapping, Guid id)
    {
        if (value.Id != id || value.Actor != actor.PrincipalId || !Belongs(value.Artifact.Intent.Revision, mapping))
            throw new InvalidOperationException("Qualification identity mismatch.");
    }
}
