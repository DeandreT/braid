using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

public static class BuildRunPolicy
{
    public const int MaximumAttempts = 3;
    public const int ActiveLimitPerActorOrganization = 32;
    public const int MaximumDiscovery = 32;
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumExecution = TimeSpan.FromMinutes(6);
}

public enum BuildRunState { Queued, Running, Succeeded, Failed, Canceled, Exhausted, Revoked, Uncertain }
public enum BuildRunSubmitOutcome { Created, Replayed, Conflict, Refused, Busy }
public enum BuildRunDispatchOutcome { NotClaimed, Completed, Released, LeaseLost, Unavailable }
public sealed record BuildRunKey(Guid OrganizationId, Guid BuildId);
public sealed record BuildRunSnapshot(PrincipalId Actor, BuildIntent Intent, BuildRunState State, int Attempt,
    DateTimeOffset CreatedAt, BuildExecutionOutcome? Outcome, BuildArtifactReference? Artifact);

/// <summary>Private worker capability, never part of an API receipt or ordinary run read.</summary>
public sealed record BuildRunClaim
{
    public BuildRunClaim(PrincipalId actor, BuildIntent intent, Guid workerId, Guid token, int attempt, DateTimeOffset leaseExpires)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (actor.Value == Guid.Empty || workerId == Guid.Empty || token == Guid.Empty ||
            attempt is < 1 or > BuildRunPolicy.MaximumAttempts || leaseExpires.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid build claim.");
        Actor = actor; Intent = intent; WorkerId = workerId; Token = token; Attempt = attempt; LeaseExpires = leaseExpires;
    }
    public PrincipalId Actor { get; }
    public BuildIntent Intent { get; }
    public Guid WorkerId { get; }
    public Guid Token { get; }
    public int Attempt { get; }
    public DateTimeOffset LeaseExpires { get; }
    public override string ToString() => nameof(BuildRunClaim);
}

public interface IBuildRunCommands
{
    Task<BuildRunSubmitOutcome> SubmitAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default);
    Task<bool> CancelAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default);
}

public interface IBuildRunReader
{
    Task<BuildRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default);
}

public interface IBuildRunStore : IBuildRunCommands, IBuildRunReader
{
    Task<BuildRunClaim?> ClaimAsync(Guid organizationId, Guid buildId, Guid workerId, CancellationToken token = default);
    Task<bool> RenewAsync(BuildRunClaim claim, CancellationToken token = default);
    Task<bool> ReleaseAsync(BuildRunClaim claim, CancellationToken token = default);
    /// <summary>Success requires the exact committed artifact; all transitions independently fence the claim.</summary>
    Task<bool> CompleteAsync(BuildRunClaim claim, BuildExecutionResult result, CancellationToken token = default);
}

public interface IBuildRunQueue
{
    /// <summary>Bounded machine-only discovery of IDs, not sample, source, actor or approval content.</summary>
    Task<IReadOnlyList<BuildRunKey>> DiscoverAsync(int maximum, CancellationToken token = default);
}

public interface IBuildRunExecution
{
    /// <summary>Compose actor-bound controls/storage with the claim's atomic publication fence.</summary>
    Task<BuildExecutionResult> ExecuteAsync(BuildRunClaim claim, CancellationToken token = default);
}
