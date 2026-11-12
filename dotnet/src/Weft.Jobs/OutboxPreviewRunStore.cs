using Weft.Application;
using Weft.Persistence.Postgres;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Weft.Jobs;

/// <summary>
/// Commits a newly created preview run and its <see cref="RunPreview"/> command in one PostgreSQL
/// transaction through Wolverine's durable outbox. Replays and refusals publish nothing.
/// </summary>
public sealed class OutboxPreviewRunStore(PostgresPreviewRunStore inner, IWolverineRuntime runtime) : IPreviewRunStore
{
    private readonly PostgresPreviewRunStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IWolverineRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public Task RequireSubmissionAdmissionAsync(CancellationToken cancellationToken = default) =>
        _inner.RequireSubmissionAdmissionAsync(cancellationToken);

    public async Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var context = new MessageContext(_runtime);
        if (!context.TryFindMessageDatabase(out var found) || found is not { } database)
            throw new InvalidOperationException("Preview submission requires Wolverine's PostgreSQL message store.");
        var result = await _inner.SubmitAsync(actor, record, async (transaction, submitted, _) =>
        {
            if (submitted.Outcome != PreviewSubmitOutcome.Created)
                return;
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, transaction)).ConfigureAwait(false);
            await context.PublishAsync(new RunPreview(record.Submission.Subject.ExecutionScope.OrganizationId, submitted.RunId!.Value)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
        return result;
    }

    public Task<PreviewClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid owner, TimeSpan lease, CancellationToken cancellationToken = default) =>
        _inner.ClaimAsync(organizationId, runId, owner, lease, cancellationToken);

    public Task<IReadOnlyList<PreviewStoredInput>> ReadInputsAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default) =>
        _inner.ReadInputsAsync(claim, cancellationToken);

    public Task<PreviewClaimObservation> ObserveAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default) =>
        _inner.ObserveAsync(claim, cancellationToken);

    public Task<bool> CompleteAsync(PreviewRunClaim claim, PreviewRunCompletion completion, CancellationToken cancellationToken = default) =>
        _inner.CompleteAsync(claim, completion, cancellationToken);

    public Task<bool> ReleaseAsync(PreviewRunClaim claim, TimeSpan retryDelay, CancellationToken cancellationToken = default) =>
        _inner.ReleaseAsync(claim, retryDelay, cancellationToken);

    public Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default) =>
        _inner.CancelAsync(actor, organizationId, runId, cancellationToken);
}
