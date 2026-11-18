using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>Facts reloaded from current authority, approval, environment and verified regression records.</summary>
public sealed record BuildAdmission(PrincipalId Actor, Sha256Digest IntentFingerprint, PrivateObjectReference Native);

public interface IBuildAdmissionReader
{
    /// <summary>
    /// Requires current build permission and active approval/environment versions. Regression evidence must
    /// be read and recomputed from its exact case/result, not accepted from a client or a stored pass flag.
    /// Returns null on any refusal; implementations must not cache successful admission.
    /// </summary>
    Task<BuildAdmission?> ReadAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default);
}

public sealed class BuildBytes
{
    private readonly byte[] bytes;
    public BuildBytes(ReadOnlySpan<byte> bytes, long maximumBytes)
    {
        if (bytes.Length < 1 || bytes.Length > maximumBytes || maximumBytes > BuildRuntimeProfile.MaximumAllowedArtifactBytes)
            throw new BuildContractException(BuildContractFailure.ResourceLimit);
        this.bytes = bytes.ToArray();
        Digest = Sha256Digest.FromBytes(this.bytes);
    }
    public int Length => bytes.Length;
    public Sha256Digest Digest { get; }
    public byte[] Copy() => (byte[])bytes.Clone();
    public override string ToString() => nameof(BuildBytes);
}

public interface IBuildCompiler
{
    /// <summary>Server-owned capabilities for this exact source. Inspection/preview success is not support.</summary>
    Task<BuildCapabilitySnapshot> InspectAsync(BuildIntent intent, CancellationToken token = default);
    /// <summary>Bounded, cancellation-aware artifact production without deployment or external side effects.</summary>
    Task<BuildBytes> BuildAsync(BuildIntent intent, BuildBytes native, CancellationToken token = default);
}

public sealed record BuildPublication(PrincipalId Actor, BuildArtifactReference Artifact, PrivateObjectReference Object)
{
    // Derived only from retained publication facts; accepting an uploaded manifest cannot create a publication.
    public MappingPackageManifest Manifest => MappingPackageManifest.Create(Artifact);
    public override string ToString() => nameof(BuildPublication);
}

public enum BuildPublicationOutcome { Created, Replayed, Conflict, Refused }
public sealed record StagedBuild(BuildPublication Publication, BuildBytes Bytes);
public interface IBuildPublicationCatalog
{
    Task<BuildPublication?> ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default);
    Task<StagedBuild?> ReadStagedAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default);
    /// <summary>
    /// Atomically retains the first approved artifact and its exact bytes before object publication.
    /// A same-intent race returns the already staged bytes, not the losing compiler output. Retention
    /// ownership covers the object from this point through any ambiguous write and committed publication.
    /// Return null on refusal. Different intent/actor cannot replace a stage; callers verify the returned identity.
    /// </summary>
    Task<StagedBuild?> StageAsync(ActorContext actor, StagedBuild build, CancellationToken token = default);
    /// <summary>
    /// Atomically rechecks admission and records a create-only publication, including its retention ownership.
    /// Same organization/build ID with different actor or intent conflicts; exact retries return the original.
    /// Failed or ambiguous publication must never replace committed metadata or release retained object ownership.
    /// </summary>
    Task<BuildPublicationOutcome> PublishAsync(ActorContext actor, BuildPublication publication, CancellationToken token = default);
}

public enum BuildExecutionOutcome { Created, Replayed, Refused, Unsupported, Conflict, IntegrityFailure, Unavailable, Busy }
public sealed record BuildExecutionResult(BuildExecutionOutcome Outcome, BuildPublication? Publication = null);

/// <summary>
/// Produces and reopens immutable artifact bytes. It does not authorize from IDs, install a compiler,
/// provision a target or translate interpreted preview support into compilation support.
/// </summary>
public sealed class BuildWorkflow(IBuildAdmissionReader admissions, IBuildCompiler compiler,
    IBuildPublicationCatalog catalog, IPrivateObjectStore objects)
{
    private readonly SemaphoreSlim slots = new(4, 4);

    public async Task<BuildExecutionResult> ExecuteAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(intent);
        token.ThrowIfCancellationRequested();
        if (!await slots.WaitAsync(0, token).ConfigureAwait(false)) return new(BuildExecutionOutcome.Busy);
        try
        {
            var admission = await AdmitAsync(actor, intent, token).ConfigureAwait(false);
            if (admission is null) return new(BuildExecutionOutcome.Refused);
            var capability = await compiler.InspectAsync(intent, token).ConfigureAwait(false);
            if (new BuildPreflightService().Evaluate(intent, capability, token).Outcome != BuildPreflightOutcome.Supported)
                return new(BuildExecutionOutcome.Unsupported);
            var previous = await catalog.ReadAsync(actor, intent.Revision.OrganizationId, intent.BuildId, token).ConfigureAwait(false);
            if (previous is not null) return await ReopenAsync(actor, intent, previous, BuildExecutionOutcome.Replayed, token).ConfigureAwait(false);

            var staged = await catalog.ReadStagedAsync(actor, intent.Revision.OrganizationId, intent.BuildId, token).ConfigureAwait(false);
            if (staged is null)
            {
                var native = await objects.ReadVerifiedAsync(intent.Revision, admission.Native, token).ConfigureAwait(false);
                if (native.LongLength != admission.Native.ByteCount || Sha256Digest.FromBytes(native) != intent.NativeDigest)
                    return new(BuildExecutionOutcome.IntegrityFailure);
                var beforeBuild = await AdmitAsync(actor, intent, token).ConfigureAwait(false);
                if (beforeBuild is null || beforeBuild.Native != admission.Native) return new(BuildExecutionOutcome.Refused);
                var artifact = await compiler.BuildAsync(intent, new(native, admission.Native.ByteCount), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (artifact.Length > intent.Profile.MaximumArtifactBytes) return new(BuildExecutionOutcome.IntegrityFailure);
                var reference = BuildArtifactReference.Create(intent, capability, artifact.Digest, artifact.Length, token);
                var outputReference = new PrivateObjectReference(PrivateObjectId.Restore(intent.Revision, intent.BuildId, PrivateObjectClass.DerivedArtifact), artifact.Digest, artifact.Length);
                var beforeStage = await AdmitAsync(actor, intent, token).ConfigureAwait(false);
                if (beforeStage is null || beforeStage.Native != admission.Native) return new(BuildExecutionOutcome.Refused);
                staged = await catalog.StageAsync(actor, new(new(actor.PrincipalId, reference, outputReference), artifact), token).ConfigureAwait(false);
                if (staged is null) return new(BuildExecutionOutcome.Refused);
            }
            var publication = staged.Publication;
            if (publication.Actor != actor.PrincipalId || !publication.Artifact.MatchesIntent(intent)) return new(BuildExecutionOutcome.Conflict);
            if (!ValidObject(publication, intent) || staged.Bytes.Length != publication.Artifact.ByteCount ||
                staged.Bytes.Digest != publication.Artifact.ContentDigest || staged.Bytes.Length > intent.Profile.MaximumArtifactBytes)
                return new(BuildExecutionOutcome.IntegrityFailure);
            var output = publication.Object;
            var beforePublication = await AdmitAsync(actor, intent, token).ConfigureAwait(false);
            if (beforePublication is null || beforePublication.Native != admission.Native) return new(BuildExecutionOutcome.Refused);
            using (var stream = new MemoryStream(staged.Bytes.Copy(), writable: false))
                await objects.PublishCreateOnlyAsync(intent.Revision, output, stream, token).ConfigureAwait(false);
            var verified = await objects.ReadVerifiedAsync(intent.Revision, output, token).ConfigureAwait(false);
            if (verified.LongLength != publication.Artifact.ByteCount || Sha256Digest.FromBytes(verified) != publication.Artifact.ContentDigest)
                return new(BuildExecutionOutcome.IntegrityFailure);
            if (await AdmitAsync(actor, intent, token).ConfigureAwait(false) is null) return new(BuildExecutionOutcome.Refused);
            var outcome = await catalog.PublishAsync(actor, publication, token).ConfigureAwait(false);
            if (outcome == BuildPublicationOutcome.Refused) return new(BuildExecutionOutcome.Refused);
            if (outcome == BuildPublicationOutcome.Conflict) return new(BuildExecutionOutcome.Conflict);
            if (outcome is not (BuildPublicationOutcome.Created or BuildPublicationOutcome.Replayed)) return new(BuildExecutionOutcome.IntegrityFailure);
            var committed = await catalog.ReadAsync(actor, intent.Revision.OrganizationId, intent.BuildId, token).ConfigureAwait(false);
            if (committed is null) return new(BuildExecutionOutcome.Unavailable);
            return await ReopenAsync(actor, intent, committed, outcome == BuildPublicationOutcome.Created ? BuildExecutionOutcome.Created : BuildExecutionOutcome.Replayed, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PrivateObjectStorageException failure) when (failure.Error is PrivateObjectStorageError.Integrity or PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Conflict)
        { return new(BuildExecutionOutcome.IntegrityFailure); }
        catch (BuildContractException) { return new(BuildExecutionOutcome.IntegrityFailure); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { return new(BuildExecutionOutcome.Unavailable); }
        finally { slots.Release(); }
    }

    private async Task<BuildAdmission?> AdmitAsync(ActorContext actor, BuildIntent intent, CancellationToken token)
    {
        var result = await admissions.ReadAsync(actor, intent, token).ConfigureAwait(false);
        if (result is null) return null;
        if (result.Actor != actor.PrincipalId || result.IntentFingerprint != intent.Fingerprint || result.Native.Id.Scope != intent.Revision ||
            result.Native.Id.Class != PrivateObjectClass.NativeArtifact || result.Native.Digest != intent.NativeDigest || result.Native.ByteCount < 1)
            throw new BuildContractException(BuildContractFailure.InvalidInput);
        return result;
    }

    private async Task<BuildExecutionResult> ReopenAsync(ActorContext actor, BuildIntent intent, BuildPublication publication,
        BuildExecutionOutcome outcome, CancellationToken token)
    {
        if (publication.Actor != actor.PrincipalId || !publication.Artifact.MatchesIntent(intent)) return new(BuildExecutionOutcome.Conflict);
        if (!ValidObject(publication, intent))
            return new(BuildExecutionOutcome.IntegrityFailure);
        var bytes = await objects.ReadVerifiedAsync(intent.Revision, publication.Object, token).ConfigureAwait(false);
        if (bytes.LongLength != publication.Artifact.ByteCount || Sha256Digest.FromBytes(bytes) != publication.Artifact.ContentDigest)
            return new(BuildExecutionOutcome.IntegrityFailure);
        return await AdmitAsync(actor, intent, token).ConfigureAwait(false) is null ? new(BuildExecutionOutcome.Refused) : new(outcome, publication);
    }
    private static bool ValidObject(BuildPublication publication, BuildIntent intent) =>
        publication.Object.Id.Scope == intent.Revision && publication.Object.Id.Class == PrivateObjectClass.DerivedArtifact && publication.Object.Id.ObjectId == intent.BuildId &&
        publication.Object.Digest == publication.Artifact.ContentDigest && publication.Object.ByteCount == publication.Artifact.ByteCount;
}
