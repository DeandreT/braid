using Braid.Application;
using Braid.Domain;

namespace Braid.Server;

/// <summary>Read-only composition never creates or falls back to a writer pool.</summary>
internal sealed class UnavailableRevisionReportMetadataStore : IRevisionReportMetadataStore
{
    public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
        Guid serverInstanceId, CancellationToken cancellationToken = default) => Refuse(cancellationToken);

    public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion completion, CancellationToken cancellationToken = default) => Refuse(cancellationToken);

    public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
        Guid serverInstanceId, CancellationToken cancellationToken = default) => Refuse(cancellationToken);

    private static Task<RevisionReportStoreResult> Refuse(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new RevisionReportServiceException(RevisionReportFailureCategory.BackendUnavailable);
    }
}
