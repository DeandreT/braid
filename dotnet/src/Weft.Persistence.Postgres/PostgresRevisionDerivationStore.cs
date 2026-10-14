using Weft.Application;
using Weft.Domain;
using static Weft.Persistence.Postgres.RevisionReportPersistence;

namespace Weft.Persistence.Postgres;

/// <summary>Short scoped metadata transitions. Only committed replies leave the public boundary.</summary>
public sealed partial class PostgresRevisionDerivationStore : IRevisionReportMetadataStore
{
    private readonly PostgresRevisionDerivationDatabase _database;

    public PostgresRevisionDerivationStore(PostgresRevisionDerivationDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
        Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        Validate(actor, key, serverInstanceId);
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(key.Scope.OrganizationId, cancellationToken);
            var result = await TryClaimInTransactionAsync(transaction, actor, key, serverInstanceId, cancellationToken);
            if (result.Outcome != RevisionReportStoreOutcome.NotAuthorizedOrNotFound)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion completion, CancellationToken cancellationToken = default)
    {
        ValidateFinish(actor, claim, completion);
        if (actor.PrincipalId != claim.OriginalActor || !CompletionMatchesClaim(claim, completion))
            return Task.FromResult(RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost));
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(claim.Key.Scope.OrganizationId, cancellationToken);
            var result = await FinishInTransactionAsync(transaction, actor, claim, completion, cancellationToken);
            if (result.Outcome != RevisionReportStoreOutcome.NotAuthorizedOrNotFound)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
        Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Validate(actor, target.Key, serverInstanceId);
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(target.Key.Scope.OrganizationId, cancellationToken);
            var result = await RepairInTransactionAsync(transaction, actor, target, serverInstanceId, cancellationToken);
            if (result.Outcome != RevisionReportStoreOutcome.NotAuthorizedOrNotFound)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    // Friend tests hold the identical production transition to observe actual
    // key waits/commit/rollback. Success stays uncommitted in these typed seams.
    internal Task<RevisionReportStoreResult> TryClaimInTransactionAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportKey key, Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Validate(actor, key, serverInstanceId);
        return ExecuteAsync(() => TryClaimCoreAsync(transaction, actor, key, serverInstanceId, cancellationToken), cancellationToken);
    }

    internal Task<RevisionReportStoreResult> FinishInTransactionAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportClaim claim, RevisionReportCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateFinish(actor, claim, completion);
        if (actor.PrincipalId != claim.OriginalActor || !CompletionMatchesClaim(claim, completion))
            return Task.FromResult(RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.ClaimLost));
        return ExecuteAsync(() => FinishCoreAsync(transaction, actor, claim, completion, cancellationToken), cancellationToken);
    }

    internal Task<RevisionReportStoreResult> RepairInTransactionAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportRepairTarget target, Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(target);
        Validate(actor, target.Key, serverInstanceId);
        return ExecuteAsync(() => RepairCoreAsync(transaction, actor, target, serverInstanceId, cancellationToken), cancellationToken);
    }

    private static void Validate(ActorContext actor, RevisionReportKey key, Guid instance)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(key);
        if (instance == Guid.Empty) throw new ArgumentException("A report server instance cannot be empty.", nameof(instance));
    }

    private static void ValidateFinish(ActorContext actor, RevisionReportClaim claim, RevisionReportCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Observation is { } observation && observation.Operation != claim.Key.Operation)
            throw new ArgumentException("The completion observation belongs to another operation.", nameof(completion));
    }

    private static bool CompletionMatchesClaim(RevisionReportClaim claim, RevisionReportCompletion completion) =>
        !completion.IsUsable || completion.Key == claim.Key && completion.DerivedObject?.Id == claim.CandidateObjectId;

    private static async Task<RevisionReportStoreResult> DeniedAsync(PostgresTransaction transaction,
        CancellationToken cancellationToken)
    {
        // In particular, an authority loss after an INSERT/unique wait must not
        // leave a committable orphan key, including through the friend seam.
        await transaction.RollbackAsync(cancellationToken);
        return RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.NotAuthorizedOrNotFound);
    }
}
