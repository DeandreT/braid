namespace Weft.Application.Regression;

public enum RegressionRunDispatchOutcome { Completed, Deferred, Finished, Missing, ClaimLost }

/// <summary>Executes at most one leased case; a failed case never gates another run.</summary>
public sealed class RegressionRunExecutor(IRegressionRunStore runs, IRegressionCaseExecutor execution) : IRegressionRunDispatcher
{
    public async Task<RegressionRunDispatchOutcome> ExecuteAsync(Guid organizationId, Guid runId, Guid worker,
        CancellationToken token = default)
    {
        var admission = await runs.ClaimAsync(organizationId, runId, worker, token).ConfigureAwait(false);
        if (admission.Outcome != RegressionRunClaimOutcome.Claimed)
            return admission.Outcome switch
            {
                RegressionRunClaimOutcome.Busy => RegressionRunDispatchOutcome.Deferred,
                RegressionRunClaimOutcome.Finished => RegressionRunDispatchOutcome.Finished,
                RegressionRunClaimOutcome.Missing => RegressionRunDispatchOutcome.Missing,
                _ => throw new InvalidOperationException("Regression claim refused.")
            };
        var claim = admission.Claim ?? throw new InvalidOperationException("Regression claim is missing.");
        if (claim.RunId != runId || claim.Case.Revision.OrganizationId != organizationId || claim.Worker != worker)
            throw new InvalidOperationException("Regression claim scope mismatch.");
        using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watching = new CancellationTokenSource();
        var watch = WatchAsync(claim, dispatch, watching.Token);
        var observed = WatchResult.Stopped;
        RegressionCaseExecution? result = null;
        try
        {
            result = await execution.ExecuteAsync(new(claim.Submitter), claim.Case, dispatch.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (dispatch.IsCancellationRequested) { }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            observed = await watch.ConfigureAwait(false);
        }
        if (observed == WatchResult.ClaimLost) return RegressionRunDispatchOutcome.ClaimLost;
        if (token.IsCancellationRequested || observed == WatchResult.Unavailable || result is null ||
            result.Outcome == RegressionCaseExecutionOutcome.InfrastructureFailure)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var released = await runs.ReleaseAsync(claim, cleanup.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return released ? RegressionRunDispatchOutcome.Deferred : RegressionRunDispatchOutcome.ClaimLost;
        }
        return await runs.CompleteAsync(claim, result, token).ConfigureAwait(false)
            ? RegressionRunDispatchOutcome.Completed : RegressionRunDispatchOutcome.ClaimLost;
    }

    private enum WatchResult { Stopped, ClaimLost, Unavailable }

    private async Task<WatchResult> WatchAsync(RegressionRunClaim claim, CancellationTokenSource dispatch, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                if (!await runs.ObserveAsync(claim, stop).ConfigureAwait(false))
                {
                    await dispatch.CancelAsync().ConfigureAwait(false);
                    return WatchResult.ClaimLost;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return WatchResult.Stopped; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            await dispatch.CancelAsync().ConfigureAwait(false);
            return WatchResult.Unavailable;
        }
    }
}

public sealed record RegressionRunView(RegressionRunSnapshot Run, RegressionCaseExecution? Result);

/// <summary>Reopens retained exact outputs against the original baseline, then rechecks current authority.</summary>
public sealed class RegressionRunReadService(IRegressionRunStore runs, IRegressionCaseReader cases)
{
    public async Task<RegressionRunView?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default)
    {
        var run = await runs.ReadAsync(actor, organizationId, runId, token).ConfigureAwait(false);
        if (run is null) return null;
        if (run.RunId != runId || run.Case.Revision.OrganizationId != organizationId || run.Submitter != actor.PrincipalId)
            throw new InvalidOperationException("Regression run read scope mismatch.");
        if (run.State != RegressionRunState.Completed) return new(run, null);
        var revision = run.Case.Revision;
        var value = await cases.ReadAsync(actor, new(revision.OrganizationId, revision.ProjectId, revision.MappingId),
            run.Case.CaseId, run.Case.Version, token).ConfigureAwait(false);
        if (value is null) return null;
        if (new RegressionCaseEnvelopeCodec().Encode(value, token).Reference != run.Case)
            throw new InvalidOperationException("Regression run case mismatch.");
        var result = new RegressionRunResultCodec().Decode(runId, value, run.CopyCompletion()!, token);
        var fresh = await runs.ReadAsync(actor, organizationId, runId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (fresh is null) return null;
        if (fresh.RunId != run.RunId || fresh.Submitter != actor.PrincipalId || fresh.State != run.State ||
            fresh.Case != run.Case || fresh.CompletionDigest != run.CompletionDigest)
            throw new InvalidOperationException("Regression run changed while reading.");
        return new(fresh, result);
    }
}
