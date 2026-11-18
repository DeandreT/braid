using Braid.Domain;

namespace Braid.Application.Lifecycle;

/// <summary>The exact database-issued claim on one deletion job; it grants no catalog authority beyond that job.</summary>
public sealed record DeletionJobClaim(Guid OrganizationId, Guid JobId, Guid InstanceId, long Generation, Guid Token,
    DateTimeOffset LeaseExpiresAtUtc, DeletionJobState State);

public enum DeletionJobStepOutcome { Advanced = 1, Waiting, Objects, Blocked, Completed, ClaimLost }

/// <summary>
/// Physical deletion authority for one exact object of a committed job, issued only by the
/// database object claim. It is a separate capability from an orphan cleanup claim.
/// </summary>
public sealed record CatalogObjectDeletionClaim
{
    public CatalogObjectDeletionClaim(Guid jobId, Guid objectTargetId, long generation, Guid instanceId, Guid token,
        DateTimeOffset leaseExpiresAtUtc, ObjectCleanupTarget target, PrivatePublicationRecord? publication)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (jobId == Guid.Empty || objectTargetId == Guid.Empty || instanceId == Guid.Empty || token == Guid.Empty || generation < 1 ||
            (target.Kind == CleanupTargetKind.TrackedTemporary) != (publication is not null) ||
            publication is not null && (publication.Reservation.PublicationId != target.PublicationAttemptId ||
                publication.Reservation.Key.Object != target.Object))
            throw new ArgumentException("A deletion claim requires its exact target and, for a temporary, its original publication.");
        JobId = jobId; ObjectTargetId = objectTargetId; Generation = generation; InstanceId = instanceId; Token = token;
        LeaseExpiresAtUtc = leaseExpiresAtUtc; Target = target; Publication = publication;
    }

    public Guid JobId { get; }
    public Guid ObjectTargetId { get; }
    public long Generation { get; }
    public Guid InstanceId { get; }
    public Guid Token { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }
    public ObjectCleanupTarget Target { get; }
    public PrivatePublicationRecord? Publication { get; }
}

/// <summary>Fences and erases exactly one claimed target; returns the actual physical observation.</summary>
public interface IPrivateObjectDeletionStore
{
    Task<PrivateObjectRetirementOutcome> DeleteAsync(CatalogObjectDeletionClaim claim, CancellationToken cancellationToken = default);
}

/// <summary>Worker-only job operations; no actor context and no content-returning reads.</summary>
public interface IDeletionJobStore
{
    Task<DeletionJobClaim?> ClaimAsync(Guid instanceId, CancellationToken token);
    Task<DeletionJobStepOutcome> AdvanceAsync(DeletionJobClaim claim, CancellationToken token);
    Task<IReadOnlyList<CatalogObjectDeletionClaim>> ClaimObjectsAsync(DeletionJobClaim claim, int limit, CancellationToken token);
    Task<bool> FinishObjectAsync(DeletionJobClaim claim, CatalogObjectDeletionClaim target, PrivateObjectRetirementOutcome observed, CancellationToken token);
    Task ReleaseAsync(DeletionJobClaim claim, CancellationToken token);
}

/// <summary>
/// Runs one claimed job as far as it can go now: each database step is its own short
/// transaction, and storage I/O happens only between an acknowledged object claim and
/// its finish. A lost reply is resolved by the next claim, never by assuming success.
/// </summary>
public sealed class DeletionJobService(IDeletionJobStore store, IPrivateObjectDeletionStore storage, Guid instanceId)
{
    public const int ObjectPage = 16;
    private const int StepBudget = 64;

    /// <summary>Returns whether a job was claimed, so a host can poll again at once.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claim = await store.ClaimAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (claim is null) return false;
        try
        {
            for (var step = 0; step < StepBudget && DateTimeOffset.UtcNow < claim.LeaseExpiresAtUtc.AddSeconds(-15); step++)
            {
                var outcome = await store.AdvanceAsync(claim, cancellationToken).ConfigureAwait(false);
                if (outcome == DeletionJobStepOutcome.Advanced) continue;
                if (outcome != DeletionJobStepOutcome.Objects) break;
                var targets = await store.ClaimObjectsAsync(claim, ObjectPage, cancellationToken).ConfigureAwait(false);
                if (targets.Count == 0) break;
                foreach (var target in targets)
                {
                    var observed = await storage.DeleteAsync(target, cancellationToken).ConfigureAwait(false);
                    if (!await store.FinishObjectAsync(claim, target, observed, cancellationToken).ConfigureAwait(false)) return true;
                }
            }
        }
        finally
        {
            // Releasing is best effort: an unreleased claim simply expires.
            try { await store.ReleaseAsync(claim, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return true;
    }
}
