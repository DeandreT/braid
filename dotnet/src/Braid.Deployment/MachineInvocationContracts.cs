using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

/// <summary>One grant binds one machine principal to one registered release target. It is not a role or a credential.</summary>
public sealed record InvocationGrant
{
    public InvocationGrant(Guid grantId, ReleaseTargetReference target, MachinePrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(principal);
        if (grantId == Guid.Empty) throw new ArgumentException("A grant identifier is required.", nameof(grantId));
        GrantId = grantId; Target = target; Principal = principal;
    }
    public Guid GrantId { get; }
    public ReleaseTargetReference Target { get; }
    public MachinePrincipal Principal { get; }
}

/// <summary>A retained grant. Revocation is one-way; the history and its audit are append-only.</summary>
public sealed record InvocationGrantSnapshot(Guid GrantId, MachinePrincipal Principal, PrincipalId GrantedBy, DateTimeOffset GrantedAt,
    PrincipalId? RevokedBy, DateTimeOffset? RevokedAt)
{
    public bool Active => RevokedAt is null;
}

/// <summary>Applied means created or revoked now; replaying either returns Replayed and never re-enables a grant.</summary>
public enum InvocationGrantOutcome { Applied, Replayed, Conflict, Refused }

/// <summary>Administrator-only grant changes, checked against current project authority in each transaction.</summary>
public interface IInvocationGrantStore
{
    Task<InvocationGrantOutcome> GrantAsync(ActorContext administrator, InvocationGrant grant, CancellationToken token = default);
    Task<InvocationGrantOutcome> RevokeAsync(ActorContext administrator, ReleaseTargetReference target, Guid grantId, CancellationToken token = default);
    /// <summary>Every grant on the target, oldest first. Empty without current administrator authority.</summary>
    Task<IReadOnlyList<InvocationGrantSnapshot>> ReadAsync(ActorContext administrator, ReleaseTargetReference target, CancellationToken token = default);
}

/// <summary>The idempotency identity: one caller's request ID on one target. Another caller's IDs are independent.</summary>
public sealed record MachineInvocationKey
{
    public MachineInvocationKey(ReleaseTargetReference target, MachinePrincipal caller, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller);
        if (requestId == Guid.Empty) throw new ArgumentException("A request identifier is required.", nameof(requestId));
        Target = target; Caller = caller; RequestId = requestId;
    }
    public ReleaseTargetReference Target { get; }
    public MachinePrincipal Caller { get; }
    public Guid RequestId { get; }
}

/// <summary>
/// Completed and the failures through Unsupported are recorded outcomes. Unavailable, Busy and ReleaseChanged
/// release the attempt for a retry under the same request ID; Interrupted and AttemptsExhausted are recorded by the ledger.
/// </summary>
public enum MachineInvocationOutcome
{
    Completed, Refused, InvalidInput, InvalidParameter, ExecutionFailed, ResourceLimit, IntegrityFailure, Unsupported,
    Unavailable, Busy, ReleaseChanged, Interrupted, AttemptsExhausted
}

/// <summary>Claimed starts a new attempt. Recorded returns the original outcome. The others write nothing.</summary>
public enum MachineInvocationClaimStatus { Claimed, Recorded, Conflict, InProgress, Refused, NoRelease }

/// <summary>A recorded Completed outcome without a retained response has passed its retention period.</summary>
public sealed record MachineInvocationClaim(MachineInvocationClaimStatus Status, int Attempt = 0, Guid ClaimId = default,
    PrincipalId? Publisher = null, MachineInvocationOutcome? Outcome = null, Guid? ReleaseId = null, byte[]? RetainedResponse = null)
{
    public override string ToString() => nameof(MachineInvocationClaim);
}

/// <summary>The exact release and build an attempt ran. The ledger verifies it against the activated release.</summary>
public sealed record MachineInvocationProvenance(Guid ReleaseId, Guid QualificationId, Guid BuildId, Sha256Digest BuildIntentDigest,
    Sha256Digest ArtifactDigest, Guid RevisionId, string RuntimeImage)
{
    public static MachineInvocationProvenance From(ReleaseIntent release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var artifact = release.Qualification.Artifact;
        return new(release.ReleaseId, release.Qualification.Id, artifact.Intent.BuildId, artifact.Intent.Fingerprint,
            artifact.ContentDigest, artifact.Intent.Revision.RevisionId, release.Qualification.RuntimeImage);
    }
}

/// <summary>Revoked: the grant or target ended during the attempt, so nothing is delivered. Superseded: the claim lost its lease.</summary>
public enum MachineInvocationFinishStatus { Recorded, Revoked, Superseded }

public static class MachineInvocationPolicy
{
    public const int MaximumAttempts = 8;
    public const int MaximumRetainedBytes = 4 * 1024 * 1024;
    /// <summary>An unfinished attempt can be claimed again after its lease.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(120);
    /// <summary>A successful response is retained privately for replay, then can no longer be read.</summary>
    public static readonly TimeSpan ResultRetention = TimeSpan.FromHours(24);
}

/// <summary>Idempotent claims, append-only audit with digests only, and privately retained successful responses.</summary>
public interface IMachineInvocationLedger
{
    Task<MachineInvocationClaim> ClaimAsync(MachineInvocationKey key, Guid applicationId, Sha256Digest requestDigest, CancellationToken token = default);
    Task<MachineInvocationClaim> ClaimWithMetadataAsync(MachineInvocationKey key, Guid applicationId, Sha256Digest requestDigest,
        int inputBytes, CancellationToken token = default) => ClaimAsync(key, applicationId, requestDigest, token);
    Task<MachineInvocationFinishStatus> FinishAsync(MachineInvocationKey key, Guid claimId, Guid applicationId, MachineInvocationOutcome outcome,
        MachineInvocationProvenance? provenance, string? clock, byte[]? response, CancellationToken token = default);
    /// <summary>Removes up to <paramref name="maximum"/> expired retained responses for the target.</summary>
    Task<int> PurgeExpiredAsync(ReleaseTargetReference target, int maximum, CancellationToken token = default);
}
