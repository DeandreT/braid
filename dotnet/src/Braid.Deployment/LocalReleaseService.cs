using Braid.Application;
using Braid.Application.Regression;

namespace Braid.Deployment;

public sealed record LocalReleaseRequest(Guid ReleaseId, Guid QualificationId, Guid? ExpectedActiveReleaseId,
    ReleaseAction Action = ReleaseAction.Activate, Guid? RollbackOfReleaseId = null);
public sealed record LocalReleaseRequestResult(ReleaseWriteOutcome Outcome, LocalReleaseSnapshot? Release = null);

/// <summary>Explicit local release admission. There is no external deployment effect or automatic replacement request.</summary>
public sealed class LocalReleaseService(ILocalReleaseStore releases, IBuildQualificationStore qualifications)
{
    public async Task<LocalReleaseRequestResult> RequestAsync(ActorContext actor, ReleaseTargetReference target,
        LocalReleaseRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        if (request.ReleaseId == Guid.Empty || request.QualificationId == Guid.Empty || !Enum.IsDefined(request.Action))
            throw new ArgumentException("Invalid local release request.");
        var old = await releases.ReadAsync(actor, target, request.ReleaseId, token).ConfigureAwait(false);
        if (old is not null)
        {
            Check(old, actor, target, request.ReleaseId);
            return Same(old.Intent, request) ? new(ReleaseWriteOutcome.Replayed, old) : new(ReleaseWriteOutcome.Conflict);
        }
        var intent = await PrepareIntentAsync(actor, target, request, token).ConfigureAwait(false);
        if (intent is null) return new(ReleaseWriteOutcome.Refused);
        var result = await releases.RequestAsync(actor, intent, token).ConfigureAwait(false);
        if (result is not (ReleaseWriteOutcome.Created or ReleaseWriteOutcome.Replayed)) return new(result);
        var saved = await releases.ReadAsync(actor, target, request.ReleaseId, token).ConfigureAwait(false);
        if (saved is null) return new(ReleaseWriteOutcome.Refused);
        Check(saved, actor, target, request.ReleaseId);
        if (saved.Intent.Fingerprint != intent.Fingerprint) throw new InvalidOperationException("Local release request changed during persistence.");
        token.ThrowIfCancellationRequested();
        return new(result, saved);
    }

    /// <summary>Shared admission without a write, for callers that commit a target-version CAS with the original release functions.</summary>
    internal async Task<ReleaseIntent?> PrepareIntentAsync(ActorContext actor, ReleaseTargetReference target,
        LocalReleaseRequest request, CancellationToken token)
    {
        var qualified = await qualifications.ReadAsync(actor, target.Mapping, request.QualificationId, token).ConfigureAwait(false);
        if (qualified is null) return null;
        if (qualified.Id != request.QualificationId || qualified.Actor != actor.PrincipalId)
            throw new InvalidOperationException("Local release qualification identity mismatch.");
        var registration = await releases.ReadTargetAsync(actor, target, token).ConfigureAwait(false);
        if (registration is null || !registration.Enabled || registration.Target.Reference != target ||
            registration.Target.RuntimeImage != qualified.RuntimeImage || registration.Target.Profile != qualified.Artifact.Intent.Profile ||
            qualified.Artifact.Intent.Environment != target.Environment)
            return null;
        return new ReleaseIntent(request.ReleaseId, actor.PrincipalId, target, qualified, request.Action,
            request.ExpectedActiveReleaseId, request.RollbackOfReleaseId);
    }

    public Task<ReleaseTransitionOutcome> ApplyAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default) =>
        releases.ApplyAsync(actor, target, releaseId, token);
    public Task<ReleaseTransitionOutcome> CancelAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default) =>
        releases.CancelAsync(actor, target, releaseId, token);

    private static bool Same(ReleaseIntent value, LocalReleaseRequest request) => value.Qualification.Id == request.QualificationId &&
        value.ExpectedActiveReleaseId == request.ExpectedActiveReleaseId && value.Action == request.Action && value.RollbackOfReleaseId == request.RollbackOfReleaseId;
    private static void Check(LocalReleaseSnapshot value, ActorContext actor, ReleaseTargetReference target, Guid id)
    {
        if (value.Intent.Actor != actor.PrincipalId || value.Intent.Target != target || value.Intent.ReleaseId != id)
            throw new InvalidOperationException("Local release identity mismatch.");
    }
}

/// <summary>Local invocation through the active pointer, retaining build authority and output-delivery fences.</summary>
public sealed class LocalReleaseInvocation(ILocalReleaseStore releases, CompiledBuildExecution execution)
{
    public async Task<CompiledBuildResult> ExecuteAsync(ActorContext actor, ReleaseTargetReference target, RegressionReplay replay, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(replay);
        token.ThrowIfCancellationRequested();
        var before = await releases.ReadTargetAsync(actor, target, token).ConfigureAwait(false);
        if (before is null || !before.Enabled || before.Target.Reference != target || before.ActiveReleaseId is not { } active)
            return new(CompiledBuildOutcome.Refused);
        var release = await releases.ReadAsync(actor, target, active, token).ConfigureAwait(false);
        if (release is null || release.Phase != ReleasePhase.Active || release.Intent.ReleaseId != active || release.Intent.Target != target ||
            release.Intent.Actor != actor.PrincipalId || release.Intent.Qualification.RuntimeImage != before.Target.RuntimeImage ||
            release.Intent.Qualification.Artifact.Intent.Profile != before.Target.Profile) return new(CompiledBuildOutcome.Refused);
        var qualified = release.Intent.Qualification;
        async Task<bool> StillActive(CancellationToken currentToken)
        {
            var current = await releases.ReadTargetAsync(actor, target, currentToken).ConfigureAwait(false);
            return current is not null && current.Enabled && current.Target == before.Target && current.ActiveReleaseId == active;
        }
        var result = await execution.ExecuteQualifiedAsync(actor, target.Mapping, qualified.Artifact, replay, qualified.RuntimeImage, token, StillActive).ConfigureAwait(false);
        var after = await releases.ReadTargetAsync(actor, target, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return after is not null && after.Enabled && after.Target == before.Target && after.ActiveReleaseId == active
            ? result : new(CompiledBuildOutcome.Refused);
    }
}
