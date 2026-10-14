using Weft.Application;
using Weft.Domain;

namespace Weft.Server;

/// <summary>Writer configuration and its dedicated pool resolve only at a real metadata write.</summary>
internal sealed class LazyRevisionReportMetadataStore : IRevisionReportMetadataStore
{
    private readonly Lazy<IRevisionReportMetadataStore> _store;

    internal LazyRevisionReportMetadataStore(Func<IRevisionReportMetadataStore> storeFactory)
    {
        ArgumentNullException.ThrowIfNull(storeFactory);
        _store = new(() => storeFactory() ?? throw Unavailable());
    }

    public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
        Guid serverInstanceId, CancellationToken cancellationToken = default) =>
        InvokeAsync(store => store.TryClaimAsync(actor, key, serverInstanceId, cancellationToken), cancellationToken);

    public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion completion, CancellationToken cancellationToken = default) =>
        InvokeAsync(store => store.FinishAsync(actor, claim, completion, cancellationToken), cancellationToken);

    public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
        Guid serverInstanceId, CancellationToken cancellationToken = default) =>
        InvokeAsync(store => store.RepairAsync(actor, target, serverInstanceId, cancellationToken), cancellationToken);

    private async Task<RevisionReportStoreResult> InvokeAsync(Func<IRevisionReportMetadataStore, Task<RevisionReportStoreResult>> write,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await write(_store.Value); }
        catch (OperationCanceledException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw Unavailable(); }
    }

    private static RevisionReportServiceException Unavailable() => new(RevisionReportFailureCategory.BackendUnavailable);
}
