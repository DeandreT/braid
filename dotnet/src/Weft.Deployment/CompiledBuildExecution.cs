using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Deployment;

public enum CompiledBuildOutcome { Completed, Refused, Unsupported, InvalidInput, InvalidParameter, ExecutionFailed, ResourceLimit, IntegrityFailure, Unavailable, Busy }
public sealed record CompiledBuildResult(CompiledBuildOutcome Outcome, RegressionOutputSet? Outputs = null)
{
    public override string ToString() => nameof(CompiledBuildResult);
}

public interface ICompiledBuildRuntime
{
    /// <summary>Trusted immutable image selection, distinct from the artifact's original compiler image.</summary>
    string RuntimeImage { get; }
    /// <summary>Executes only through a fixed isolated runtime. A normal server must never load the artifact assembly.</summary>
    Task<CompiledBuildResult> ExecuteAsync(BuildArtifactReference artifact, BuildBytes verifiedArtifact, RegressionReplay replay,
        CancellationToken cancellationToken = default);
}

/// <summary>Current own successful-build admission and verified artifact reads; no compiler or publication mutation.</summary>
public sealed class CompiledBuildExecution(IBuildRunReader runs, IBuildAdmissionReader admissions,
    IBuildPublicationCatalog publications, IPrivateObjectStore objects, ICompiledBuildRuntime runtime)
{
    public const int MaximumConcurrentInvocations = 4;
    public static readonly TimeSpan MaximumWallTime = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim slots = new(MaximumConcurrentInvocations, MaximumConcurrentInvocations);
    public string RuntimeImage => runtime.RuntimeImage;

    public Task<CompiledBuildResult> ExecuteAsync(ActorContext actor, MappingScope mapping, Guid buildId,
        RegressionReplay replay, CancellationToken token = default) => ExecuteAsync(actor, mapping, buildId, replay, null, null, null, token);

    internal Task<CompiledBuildResult> ExecuteQualifiedAsync(ActorContext actor, MappingScope mapping, BuildArtifactReference expected,
        RegressionReplay replay, string runtimeImage, CancellationToken token, Func<CancellationToken, Task<bool>>? additionalAdmission = null) =>
        ExecuteAsync(actor, mapping, expected.Intent.BuildId, replay, expected, runtimeImage, additionalAdmission, token);

    private async Task<CompiledBuildResult> ExecuteAsync(ActorContext actor, MappingScope mapping, Guid buildId,
        RegressionReplay replay, BuildArtifactReference? expected, string? runtimeImage, Func<CancellationToken, Task<bool>>? additionalAdmission, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping); ArgumentNullException.ThrowIfNull(replay);
        if (buildId == Guid.Empty) throw new ArgumentException("A build identifier is required.", nameof(buildId));
        token.ThrowIfCancellationRequested();
        if (!await slots.WaitAsync(0, token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Busy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(MaximumWallTime);
        try { return await ExecuteCoreAsync(actor, mapping, buildId, replay, expected, runtimeImage, additionalAdmission, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PrivateObjectStorageException error) when (error.Error is PrivateObjectStorageError.Integrity or PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Conflict)
        { return new(CompiledBuildOutcome.IntegrityFailure); }
        catch (BuildContractException) { return new(CompiledBuildOutcome.IntegrityFailure); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { return new(CompiledBuildOutcome.Unavailable); }
        finally { slots.Release(); }
    }

    private async Task<CompiledBuildResult> ExecuteCoreAsync(ActorContext actor, MappingScope mapping, Guid buildId,
        RegressionReplay replay, BuildArtifactReference? expectedArtifact, string? expectedImage, Func<CancellationToken, Task<bool>>? additionalAdmission, CancellationToken token)
    {
        var run = await runs.ReadAsync(actor, mapping.OrganizationId, buildId, token).ConfigureAwait(false);
        if (run is null || !Belongs(run.Intent.Revision, mapping)) return new(CompiledBuildOutcome.Refused);
        if (run.Actor != actor.PrincipalId || run.Intent.BuildId != buildId) return new(CompiledBuildOutcome.IntegrityFailure);
        if (run.State != BuildRunState.Succeeded || run.Artifact is not { } artifact) return new(CompiledBuildOutcome.Refused);
        if (artifact.Intent.Fingerprint != run.Intent.Fingerprint) return new(CompiledBuildOutcome.IntegrityFailure);
        if (expectedArtifact is not null && !Same(artifact, expectedArtifact)) return new(CompiledBuildOutcome.IntegrityFailure);
        if (!await Admitted(actor, run.Intent, token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        var publication = await publications.ReadAsync(actor, mapping.OrganizationId, buildId, token).ConfigureAwait(false);
        if (publication is null) return new(CompiledBuildOutcome.Refused);
        if (publication.Actor != actor.PrincipalId || !Same(publication.Artifact, artifact) || publication.Object.Id.Scope != run.Intent.Revision ||
            publication.Object.Id.ObjectId != buildId || publication.Object.Id.Class != PrivateObjectClass.DerivedArtifact ||
            publication.Object.Digest != artifact.ContentDigest || publication.Object.ByteCount != artifact.ByteCount) return new(CompiledBuildOutcome.IntegrityFailure);
        if (!await Admitted(actor, run.Intent, token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        if (additionalAdmission is not null && !await additionalAdmission(token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        var bytes = await objects.ReadVerifiedAsync(run.Intent.Revision, publication.Object, token).ConfigureAwait(false);
        if (bytes.LongLength != artifact.ByteCount || Sha256Digest.FromBytes(bytes) != artifact.ContentDigest)
            return new(CompiledBuildOutcome.IntegrityFailure);
        if (!await Admitted(actor, run.Intent, token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        if (additionalAdmission is not null && !await additionalAdmission(token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        var image = RuntimeImage;
        if (expectedImage is not null && image != expectedImage) return new(CompiledBuildOutcome.IntegrityFailure);
        var result = await runtime.ExecuteAsync(artifact, new(bytes, artifact.Intent.Profile.MaximumArtifactBytes), replay, token).ConfigureAwait(false);
        if (RuntimeImage != image) return new(CompiledBuildOutcome.IntegrityFailure);
        if (result is null || !Enum.IsDefined(result.Outcome) || (result.Outcome == CompiledBuildOutcome.Completed) != (result.Outputs is not null))
            return new(CompiledBuildOutcome.IntegrityFailure);
        if (result.Outputs is { } outputs)
        {
            var expected = run.Intent.Boundaries.Where(value => value.Kind is BuildBoundaryKind.PrimaryOutput or BuildBoundaryKind.NamedOutput)
                .Where(value => replay.Target.Kind == PreviewTargetKind.All || (replay.Target.Kind == PreviewTargetKind.Primary ? value.Kind == BuildBoundaryKind.PrimaryOutput : value.Name == replay.Target.Name))
                .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
            if (outputs.ByteCount > replay.Limits.MaximumOutputBytes || outputs.Outputs.Count > replay.Limits.MaximumArtifacts ||
                expected.Count == 0 || outputs.Outputs.Count != expected.Count || outputs.Outputs.Any(value => !expected.Remove(value.Key.Name)))
                return new(CompiledBuildOutcome.IntegrityFailure);
        }
        if (!await Admitted(actor, run.Intent, token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        var fresh = await runs.ReadAsync(actor, mapping.OrganizationId, buildId, token).ConfigureAwait(false);
        if (fresh is null) return new(CompiledBuildOutcome.Refused);
        if (fresh.Actor != run.Actor || fresh.State != BuildRunState.Succeeded || fresh.Intent.Fingerprint != run.Intent.Fingerprint || fresh.Artifact is null || !Same(fresh.Artifact, artifact))
            return new(CompiledBuildOutcome.IntegrityFailure);
        if (additionalAdmission is not null && !await additionalAdmission(token).ConfigureAwait(false)) return new(CompiledBuildOutcome.Refused);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<bool> Admitted(ActorContext actor, BuildIntent intent, CancellationToken token)
    {
        var admitted = await admissions.ReadAsync(actor, intent, token).ConfigureAwait(false);
        if (admitted is null) return false;
        if (admitted.Actor != actor.PrincipalId || admitted.IntentFingerprint != intent.Fingerprint || admitted.Native.Id.Scope != intent.Revision ||
            admitted.Native.Digest != intent.NativeDigest || admitted.Native.Id.Class != PrivateObjectClass.NativeArtifact)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        return true;
    }
    private static bool Belongs(RevisionScope value, MappingScope mapping) => value.OrganizationId == mapping.OrganizationId &&
        value.ProjectId == mapping.ProjectId && value.MappingId == mapping.MappingId;
    private static bool Same(BuildArtifactReference left, BuildArtifactReference right) => left.Intent.Fingerprint == right.Intent.Fingerprint &&
        left.ContentDigest == right.ContentDigest && left.ByteCount == right.ByteCount;
}
