using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;
using static Weft.Persistence.Postgres.PrivateObjectMaintenancePersistence;

namespace Weft.Persistence.Postgres;

/// <summary>Committed bounded metadata claims and exact acknowledgements; never filesystem operations.</summary>
public sealed partial class PostgresPrivateObjectMaintenanceStore : IPrivateObjectMaintenanceMetadata
{
    private readonly PostgresPrivateObjectMaintenanceDatabase _database;

    public PostgresPrivateObjectMaintenanceStore(PostgresPrivateObjectMaintenanceDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<ObjectCleanupClaimBatch> ClaimAsync(ProjectScope project, Guid serverInstanceId,
        ObjectCleanupScanCursor? after = null, CancellationToken cancellationToken = default)
    {
        ValidateClaim(project, serverInstanceId, after);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(project, cancellationToken);
            var result = await ClaimInTransactionAsync(transaction, project, serverInstanceId, after, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }

    public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        _ = Disposition(disposition);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() => FinishCommittedAsync(claim, disposition, cancellationToken), cancellationToken);
    }

    public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken, CancellationToken settlementCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        _ = Disposition(disposition);
        cancellationToken.ThrowIfCancellationRequested();
        settlementCancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(() => FinishCommittedAsync(claim, disposition, cancellationToken, settlementCancellationToken), cancellationToken);
    }

    private async Task<ObjectCleanupFinishResult> FinishCommittedAsync(ObjectCleanupClaim claim,
        ObjectCleanupDisposition disposition, CancellationToken cancellationToken,
        CancellationToken settlementCancellationToken = default)
    {
        var project = Project(claim);
        var commitStarted = false;
        ObjectCleanupFinishResult? staged = null;
        try
        {
            // This inner async scope settles/disposes the original transaction and connection
            // before the catch may inspect the retained exact generation.
            return await CommitAsync();
        }
        catch (NpgsqlException) when (commitStarted && !cancellationToken.IsCancellationRequested &&
            staged?.Outcome is ObjectCleanupFinishOutcome.Applied or ObjectCleanupFinishOutcome.AlreadyApplied)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var sharedRecovery = CancellationTokenSource.CreateLinkedTokenSource(recovery.Token, settlementCancellationToken);
            ObjectCleanupAttempt? retained;
            try
            {
                retained = await GetAttemptAsync(project, claim.Retirement.RetirementId, claim.Generation, sharedRecovery.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            catch (OperationCanceledException) when (settlementCancellationToken.IsCancellationRequested) { throw new OperationCanceledException(settlementCancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (retained is null || retained.Claim != claim || retained.Disposition != disposition || retained.FinishedAtUtc is null)
                throw Unavailable();
            return ObjectCleanupFinishResult.ForAttempt(ObjectCleanupFinishOutcome.AlreadyApplied, retained);
        }

        async Task<ObjectCleanupFinishResult> CommitAsync()
        {
            await using var transaction = await _database.BeginTransactionAsync(project, cancellationToken);
            var result = await FinishInTransactionAsync(transaction, claim, disposition, cancellationToken);
            staged = result;
            commitStarted = true;
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    public Task<ObjectCleanupAttempt?> GetAttemptAsync(ProjectScope project, Guid retirementId, long generation,
        CancellationToken cancellationToken = default)
    {
        ValidateGet(project, retirementId, generation);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(project, cancellationToken);
            var result = await GetAttemptInTransactionAsync(transaction, project, retirementId, generation, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }

    internal async Task<ObjectCleanupClaimBatch> ClaimInTransactionAsync(PostgresPrivateObjectMaintenanceTransaction transaction,
        ProjectScope project, Guid serverInstanceId, ObjectCleanupScanCursor? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateClaim(project, serverInstanceId, after);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireProjectAsync(project, cancellationToken);
        await using var command = transaction.CreateCommand(
            "SELECT weft.claim_private_object_cleanup_v1(@organizationId,@projectId,@instanceId,@after)::text");
        AddProject(command, project);
        Add(command, "instanceId", NpgsqlDbType.Uuid, serverInstanceId);
        Add(command, "after", NpgsqlDbType.Jsonb, after is null ? null : CursorJson(after, cancellationToken));
        var json = await ReadJsonAsync(command, cancellationToken) ?? throw InvalidFacts();
        var result = Restore(json, row => Batch(row, project, serverInstanceId, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal async Task<ObjectCleanupFinishResult> FinishInTransactionAsync(PostgresPrivateObjectMaintenanceTransaction transaction,
        ObjectCleanupClaim claim, ObjectCleanupDisposition disposition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(claim);
        var dispositionText = Disposition(disposition);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireProjectAsync(Project(claim), cancellationToken);
        await using var command = transaction.CreateCommand(
            "SELECT weft.finish_private_object_cleanup_v1(@organizationId,@projectId,@claim,@disposition)::text");
        AddProject(command, Project(claim));
        Add(command, "claim", NpgsqlDbType.Jsonb, ClaimJson(claim, cancellationToken));
        Add(command, "disposition", NpgsqlDbType.Text, dispositionText);
        var json = await ReadJsonAsync(command, cancellationToken) ?? throw InvalidFacts();
        var result = Restore(json, row =>
        {
            var outcome = Text(row, "outcome") switch
            {
                "applied" => ObjectCleanupFinishOutcome.Applied,
                "already_applied" => ObjectCleanupFinishOutcome.AlreadyApplied,
                "claim_lost" => ObjectCleanupFinishOutcome.ClaimLost,
                "safety_refusal" => ObjectCleanupFinishOutcome.SafetyRefusal,
                _ => throw InvalidFacts()
            };
            if (outcome is ObjectCleanupFinishOutcome.ClaimLost or ObjectCleanupFinishOutcome.SafetyRefusal)
            {
                if (!Null(row, "attempt")) throw InvalidFacts();
                return ObjectCleanupFinishResult.WithoutAttempt(outcome);
            }
            var attempt = Attempt(row.GetProperty("attempt"));
            if (attempt.Claim != claim || attempt.Disposition != disposition) throw InvalidFacts();
            return ObjectCleanupFinishResult.ForAttempt(outcome, attempt);
        });
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal async Task<ObjectCleanupAttempt?> GetAttemptInTransactionAsync(PostgresPrivateObjectMaintenanceTransaction transaction,
        ProjectScope project, Guid retirementId, long generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateGet(project, retirementId, generation);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireProjectAsync(project, cancellationToken);
        await using var command = transaction.CreateCommand(
            "SELECT weft.get_private_object_cleanup_attempt_v1(@organizationId,@projectId,@retirementId,@generation)::text");
        AddProject(command, project);
        Add(command, "retirementId", NpgsqlDbType.Uuid, retirementId);
        Add(command, "generation", NpgsqlDbType.Bigint, generation);
        var json = await ReadJsonAsync(command, cancellationToken);
        if (json is null || json == "null") return null;
        var result = Restore(json, Attempt);
        if (Project(result.Claim) != project || result.Claim.Retirement.RetirementId != retirementId || result.Claim.Generation != generation)
            throw InvalidFacts();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static ProjectScope Project(ObjectCleanupClaim claim) =>
        new(claim.Retirement.Target.Scope.OrganizationId, claim.Retirement.Target.Scope.ProjectId);

    private static void ValidateClaim(ProjectScope project, Guid instance, ObjectCleanupScanCursor? cursor)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (instance == Guid.Empty) throw new ArgumentException("A cleanup instance identifier cannot be empty.", nameof(instance));
        if (cursor is not null && cursor.Project != project)
            throw new ArgumentException("A cleanup cursor must belong to the requested project.", nameof(cursor));
    }

    private static void ValidateGet(ProjectScope project, Guid retirementId, long generation)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (retirementId == Guid.Empty) throw new ArgumentException("A retirement identifier cannot be empty.", nameof(retirementId));
        if (generation is < 1 or > ObjectCleanupPolicy.MaximumAttempts) throw new ArgumentOutOfRangeException(nameof(generation));
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try { return await operation(); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException) { throw Unavailable(); }
    }
}
