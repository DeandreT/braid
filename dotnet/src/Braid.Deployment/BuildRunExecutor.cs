using Braid.Application;

namespace Braid.Deployment;

/// <summary>Independent build boundary; leases, retries, publication and completion remain durable.</summary>
public sealed class BuildRunExecutor(IBuildRunStore store, IBuildRunExecution execution, TimeProvider? time = null)
{
    public async Task<BuildRunDispatchOutcome> ExecuteAsync(BuildRunKey key, Guid workerId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        if (execution is IExecutionAdmission { CanExecute: false }) return BuildRunDispatchOutcome.Unavailable;
        BuildRunClaim? claim;
        try { claim = await store.ClaimAsync(key.OrganizationId, key.BuildId, workerId, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Ordinary(error)) { return BuildRunDispatchOutcome.Unavailable; }
        if (claim is null) return BuildRunDispatchOutcome.NotClaimed;
        if (claim.Intent.Revision.OrganizationId != key.OrganizationId || claim.Intent.BuildId != key.BuildId || claim.WorkerId != workerId)
            return BuildRunDispatchOutcome.Unavailable;

        using var deadline = new CancellationTokenSource(BuildRunPolicy.MaximumExecution, time ?? TimeProvider.System);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lost = 0;
        var heartbeat = MonitorAsync();
        try
        {
            var result = await execution.ExecuteAsync(claim, work.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref lost) != 0) return BuildRunDispatchOutcome.LeaseLost;
            work.Token.ThrowIfCancellationRequested();
            if (result.Outcome is BuildExecutionOutcome.Unavailable or BuildExecutionOutcome.Busy)
                return await ReleaseAsync() ? BuildRunDispatchOutcome.Released : BuildRunDispatchOutcome.LeaseLost;
            if (!Valid(result, claim))
                return await ReleaseAsync() ? BuildRunDispatchOutcome.Released : BuildRunDispatchOutcome.LeaseLost;
            return await store.CompleteAsync(claim, result, token).ConfigureAwait(false)
                ? BuildRunDispatchOutcome.Completed : BuildRunDispatchOutcome.LeaseLost;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await ReleaseAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref lost) != 0) { return BuildRunDispatchOutcome.LeaseLost; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return await ReleaseAsync().ConfigureAwait(false) ? BuildRunDispatchOutcome.Released : BuildRunDispatchOutcome.LeaseLost;
        }
        catch (Exception error) when (Ordinary(error))
        {
            return await ReleaseAsync().ConfigureAwait(false) ? BuildRunDispatchOutcome.Released : BuildRunDispatchOutcome.Unavailable;
        }
        finally
        {
            await monitoring.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        async Task<bool> ReleaseAsync()
        {
            try { return await store.ReleaseAsync(claim, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (Ordinary(error)) { return false; }
        }
        async Task MonitorAsync()
        {
            using var timer = new PeriodicTimer(BuildRunPolicy.Heartbeat, time ?? TimeProvider.System);
            try
            {
                while (await timer.WaitForNextTickAsync(monitoring.Token).ConfigureAwait(false))
                {
                    if (await store.RenewAsync(claim, monitoring.Token).ConfigureAwait(false)) continue;
                    Interlocked.Exchange(ref lost, 1);
                    await work.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
            catch (Exception error) when (Ordinary(error))
            {
                Interlocked.Exchange(ref lost, 1);
                await work.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool Valid(BuildExecutionResult result, BuildRunClaim claim)
    {
        if (!Enum.IsDefined(result.Outcome)) return false;
        if (result.Outcome is BuildExecutionOutcome.Created or BuildExecutionOutcome.Replayed)
            return result.Publication is { } publication && publication.Actor == claim.Actor &&
                publication.Artifact.MatchesIntent(claim.Intent) && publication.Object.Id.Scope == claim.Intent.Revision &&
                publication.Object.Id.ObjectId == claim.Intent.BuildId && publication.Object.Id.Class == Braid.Domain.PrivateObjectClass.DerivedArtifact &&
                publication.Object.Digest == publication.Artifact.ContentDigest && publication.Object.ByteCount == publication.Artifact.ByteCount;
        return result.Publication is null && result.Outcome is BuildExecutionOutcome.Refused or BuildExecutionOutcome.Unsupported or
            BuildExecutionOutcome.Conflict or BuildExecutionOutcome.IntegrityFailure;
    }
    private static bool Ordinary(Exception error) => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
