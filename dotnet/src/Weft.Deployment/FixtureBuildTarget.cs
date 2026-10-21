using Weft.Application;
using Weft.Domain;

namespace Weft.Deployment;

public enum FixtureDeploymentOutcome { Created, Replayed, Refused, Conflict, IntegrityFailure }
public sealed record FixtureDeploymentReceipt(Guid DeploymentId, PrincipalId Actor, BuildEnvironmentReference Environment,
    Sha256Digest IntentFingerprint, Sha256Digest ArtifactDigest);
public sealed record FixtureDeploymentResult(FixtureDeploymentOutcome Outcome, FixtureDeploymentReceipt? Receipt = null);

/// <summary>In-memory target for exercising publication/retry contracts; never registered as a production target.</summary>
public sealed class FixtureBuildTarget(IBuildAdmissionReader admissions, IPrivateObjectStore objects)
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid, Guid), FixtureDeploymentReceipt> records = [];
    public int Count { get { lock (gate) return records.Count; } }

    public async Task<FixtureDeploymentResult> DeployAsync(ActorContext actor, Guid deploymentId, BuildPublication publication, CancellationToken token = default)
    {
        if (deploymentId == Guid.Empty) throw new ArgumentException("A deployment identity is required.");
        var intent = publication.Artifact.Intent;
        if (publication.Actor != actor.PrincipalId) return new(FixtureDeploymentOutcome.Refused);
        if (!await AdmittedAsync(actor, intent, token).ConfigureAwait(false)) return new(FixtureDeploymentOutcome.Refused);
        if (intent.Backend != FixtureBuildCompiler.Backend || intent.Profile != FixtureBuildCompiler.Profile ||
            publication.Object.Id.Scope != intent.Revision || publication.Object.Id.Class != PrivateObjectClass.DerivedArtifact ||
            publication.Object.Id.ObjectId != intent.BuildId || publication.Object.Digest != publication.Artifact.ContentDigest || publication.Object.ByteCount != publication.Artifact.ByteCount)
            return new(FixtureDeploymentOutcome.IntegrityFailure);
        var bytes = await objects.ReadVerifiedAsync(intent.Revision, publication.Object, token).ConfigureAwait(false);
        if (bytes.LongLength != publication.Artifact.ByteCount || Sha256Digest.FromBytes(bytes) != publication.Artifact.ContentDigest)
            return new(FixtureDeploymentOutcome.IntegrityFailure);
        try { _ = FixtureBuildCompiler.ReadVerified(intent, bytes); }
        catch (BuildContractException) { return new(FixtureDeploymentOutcome.IntegrityFailure); }
        if (!await AdmittedAsync(actor, intent, token).ConfigureAwait(false)) return new(FixtureDeploymentOutcome.Refused);
        var receipt = new FixtureDeploymentReceipt(deploymentId, actor.PrincipalId, intent.Environment, intent.Fingerprint, publication.Artifact.ContentDigest);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (records.TryGetValue((intent.Revision.OrganizationId, deploymentId), out var existing))
                return existing == receipt ? new(FixtureDeploymentOutcome.Replayed, existing) : new(FixtureDeploymentOutcome.Conflict);
            records.Add((intent.Revision.OrganizationId, deploymentId), receipt);
            return new(FixtureDeploymentOutcome.Created, receipt);
        }
    }

    private async Task<bool> AdmittedAsync(ActorContext actor, BuildIntent intent, CancellationToken token)
    {
        var value = await admissions.ReadAsync(actor, intent, token).ConfigureAwait(false);
        return value is not null && value.Actor == actor.PrincipalId && value.IntentFingerprint == intent.Fingerprint &&
            value.Native.Id.Class == PrivateObjectClass.NativeArtifact && value.Native.Id.Scope == intent.Revision && value.Native.Digest == intent.NativeDigest;
    }
}
