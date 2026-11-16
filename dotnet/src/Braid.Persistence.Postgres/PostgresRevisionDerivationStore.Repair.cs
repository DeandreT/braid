using Braid.Application;
using Braid.Domain;
using static Braid.Persistence.Postgres.RevisionReportPersistence;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresRevisionDerivationStore
{
    private static async Task<RevisionReportStoreResult> RepairCoreAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportRepairTarget target, Guid serverInstanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireOrganizationAsync(target.Key.Scope.OrganizationId, cancellationToken);
        var locked = await LockEntryAsync(transaction, actor, target.Key, target.CacheEntryId, cancellationToken);
        var row = await ReadCurrentAsync(transaction, actor, target.Key.Scope, target.Key.Operation,
            target.Key.Fingerprint.Value, target.CacheEntryId, cancellationToken);
        if (row is null) return await DeniedAsync(transaction, cancellationToken);
        var entry = row.Entry;
        if (locked is null || entry is null || entry.Key != target.Key || row.Selected?.Result is not { } selected ||
            selected.CacheEntryId != target.CacheEntryId || selected.DerivationId != target.DerivationId ||
            selected.DerivedObject != target.DerivedObject)
            return RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.SelectionChanged);
        if (entry.Generation == long.MaxValue)
            return RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.GenerationExhausted);
        // The inserted header records the exact old success while it is still
        // selected; only the following guarded pointer transition deselects it.
        return await InsertAndAdvanceAsync(transaction, actor, target.Key, entry, serverInstanceId,
            target.DerivationId, cancellationToken);
    }
}
