using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Jobs;

namespace Braid.Tests;

public sealed class BuildRunExecutorTests
{
    [Fact]
    public async Task A_quarantined_service_releases_the_claim_and_the_same_pump_recovers_without_host_restart()
    {
        var connector = new FerruleServiceRecoveryTests.Connector();
        await using var runner = FerruleServiceRecoveryTests.Runner(connector);
        var store = new Store([Intent()]);
        var execution = new Execution(async (claim, token) =>
        {
            var result = await runner.RunAsync(new byte[] { 1 }, token);
            // FerruleBuildCompiler throws on every transport failure; the executor releases, not completes, it.
            if (result.Failure != Braid.MappingBackends.Workers.WorkerProcessFailure.None)
                throw new InvalidOperationException("Compiler worker unavailable.");
            return Publication(claim);
        })
        { Admission = () => runner.Available };
        var pump = new BuildRunPump(store, new(store, execution));
        Assert.Equal(0, await pump.RunOnceAsync());
        Assert.Equal(1, store.Releases);
        Assert.Empty(store.Completed);
        Assert.Equal(1, store.Claims);
        Assert.Equal(0, await pump.RunOnceAsync());
        Assert.Empty(store.Completed);
        Assert.Equal(1, store.Claims);
        Assert.Equal(1, store.Releases);
        Assert.Equal(1, connector.Deliveries);
        connector.Uncertain = false;
        await FerruleServiceRecoveryTests.Reopened(runner);
        Assert.Equal(1, await pump.RunOnceAsync());
        Assert.Single(store.Completed);
        Assert.Equal(2, connector.Deliveries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failure_at_each_position_preserves_success_and_retries_only_failed_build(int failed)
    {
        var store = new Store([Intent(), Intent(), Intent()]);
        var bad = store.Values[failed].BuildId;
        var fail = true;
        var execution = new Execution((claim, _) => fail && claim.Intent.BuildId == bad
            ? throw new IOException("Synthetic build failure.") : Task.FromResult(Publication(claim)));
        var pump = new BuildRunPump(store, new(store, execution));
        Assert.Equal(2, await pump.RunOnceAsync());
        Assert.Equal(2, store.Completed.Count);
        fail = false;
        Assert.Equal(1, await pump.RunOnceAsync());
        Assert.Equal(3, store.Completed.Count);
        Assert.Equal(2, execution.Calls.Count(id => id == bad));
        foreach (var value in store.Values.Where(value => value.BuildId != bad))
            Assert.Equal(1, execution.Calls.Count(id => id == value.BuildId));
    }

    [Theory]
    [InlineData(BuildExecutionOutcome.Unavailable)]
    [InlineData(BuildExecutionOutcome.Busy)]
    public async Task Transient_results_release_the_current_claim_without_fabricating_success(BuildExecutionOutcome outcome)
    {
        var store = new Store([Intent()]);
        var execution = new Execution((_, _) => Task.FromResult(new BuildExecutionResult(outcome)));
        Assert.Equal(BuildRunDispatchOutcome.Released, await new BuildRunExecutor(store, execution).ExecuteAsync(store.Key, Guid.NewGuid()));
        Assert.Equal(1, store.Releases);
        Assert.Empty(store.Completed);
    }

    [Theory]
    [InlineData(BuildExecutionOutcome.Refused)]
    [InlineData(BuildExecutionOutcome.Unsupported)]
    [InlineData(BuildExecutionOutcome.Conflict)]
    [InlineData(BuildExecutionOutcome.IntegrityFailure)]
    public async Task Explicit_failure_is_terminal_and_has_no_artifact(BuildExecutionOutcome outcome)
    {
        var store = new Store([Intent()]);
        Assert.Equal(BuildRunDispatchOutcome.Completed, await new BuildRunExecutor(store,
            new Execution((_, _) => Task.FromResult(new BuildExecutionResult(outcome)))).ExecuteAsync(store.Key, Guid.NewGuid()));
        Assert.Equal(outcome, Assert.Single(store.Completed.Values).Outcome);
        Assert.Null(Assert.Single(store.Completed.Values).Publication);
        Assert.Equal(0, store.Releases);
    }

    [Theory]
    [InlineData(BuildExecutionOutcome.Created)]
    [InlineData(BuildExecutionOutcome.Replayed)]
    [InlineData((BuildExecutionOutcome)999)]
    public async Task Missing_success_publication_or_unknown_outcome_cannot_complete(BuildExecutionOutcome outcome)
    {
        var store = new Store([Intent()]);
        Assert.Equal(BuildRunDispatchOutcome.Released, await new BuildRunExecutor(store,
            new Execution((_, _) => Task.FromResult(new BuildExecutionResult(outcome)))).ExecuteAsync(store.Key, Guid.NewGuid()));
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task A_lost_lease_prevents_success_even_when_execution_ignores_cancellation()
    {
        var clock = new MaintenanceServiceTestTimeProvider();
        var store = new Store([Intent()]) { Renewable = false };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new Execution(async (claim, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            started.TrySetResult();
            await release.Task;
            return Publication(claim);
        });
        var pending = new BuildRunExecutor(store, execution, clock).ExecuteAsync(store.Key, Guid.NewGuid());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(BuildRunPolicy.Heartbeat);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.Equal(BuildRunDispatchOutcome.LeaseLost, await pending);
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task Requested_shutdown_releases_and_preserves_requested_cancellation()
    {
        var store = new Store([Intent()]);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new BuildRunExecutor(store, new Execution(async (_, token) =>
        {
            started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(BuildExecutionOutcome.Unavailable);
        })).ExecuteAsync(store.Key, Guid.NewGuid(), stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, store.Releases);
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task A_healthy_heartbeat_cannot_remove_the_overall_execution_deadline()
    {
        var clock = new MaintenanceServiceTestTimeProvider();
        var store = new Store([Intent()]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new BuildRunExecutor(store, new Execution(async (_, token) =>
        {
            started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(BuildExecutionOutcome.Unavailable);
        }), clock).ExecuteAsync(store.Key, Guid.NewGuid());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(BuildRunPolicy.MaximumExecution);
        Assert.Equal(BuildRunDispatchOutcome.Released, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task A_substituted_claim_is_not_executed_or_released_as_someone_elses_work()
    {
        var store = new Store([Intent()]) { Substitute = true };
        var execution = new Execution((claim, _) => Task.FromResult(Publication(claim)));
        Assert.Equal(BuildRunDispatchOutcome.Unavailable, await new BuildRunExecutor(store, execution).ExecuteAsync(store.Key, Guid.NewGuid()));
        Assert.Empty(execution.Calls); Assert.Equal(0, store.Releases);
    }

    [Fact]
    public async Task Oversized_discovery_is_refused_before_claiming()
    {
        var store = new Store(Enumerable.Range(0, BuildRunPump.BatchSize + 1).Select(_ => Intent()).ToArray()) { Oversized = true };
        Assert.Equal(0, await new BuildRunPump(store, new(store, new Execution((claim, _) => Task.FromResult(Publication(claim))))).RunOnceAsync());
        Assert.Equal(0, store.Claims);
    }

    private static BuildIntent Intent()
    {
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        return new(Guid.NewGuid(), scope, Sha256Digest.FromBytes([1]), FixtureBuildCompiler.Backend, FixtureBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"), new(BuildBoundaryKind.PrimaryOutput, null, "fixture-bytes")],
            new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
    }
    private static BuildExecutionResult Publication(BuildRunClaim claim)
    {
        var bytes = new BuildBytes([1, 2], 1024);
        var artifact = BuildArtifactReference.Restore(claim.Intent, bytes.Digest, bytes.Length);
        return new(BuildExecutionOutcome.Created, new(claim.Actor, artifact,
            new(PrivateObjectId.Restore(claim.Intent.Revision, claim.Intent.BuildId, PrivateObjectClass.DerivedArtifact), bytes.Digest, bytes.Length)));
    }
    private sealed class Execution(Func<BuildRunClaim, CancellationToken, Task<BuildExecutionResult>> operation) : IBuildRunExecution, IExecutionAdmission
    {
        internal Func<bool>? Admission;
        public bool CanExecute => Admission?.Invoke() ?? true;
        internal readonly List<Guid> Calls = [];
        public Task<BuildExecutionResult> ExecuteAsync(BuildRunClaim claim, CancellationToken token = default)
        { Calls.Add(claim.Intent.BuildId); return operation(claim, token); }
    }
    private sealed class Store(BuildIntent[] values) : IBuildRunStore, IBuildRunQueue
    {
        private readonly PrincipalId actor = new(Guid.NewGuid());
        internal BuildIntent[] Values => values;
        internal BuildRunKey Key => new(values[0].Revision.OrganizationId, values[0].BuildId);
        internal readonly Dictionary<Guid, BuildExecutionResult> Completed = [];
        internal bool Renewable = true, Substitute, Oversized;
        internal int Releases, Claims;
        public Task<IReadOnlyList<BuildRunKey>> DiscoverAsync(int maximum, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<BuildRunKey>>(values.Where(value => !Completed.ContainsKey(value.BuildId))
                .Take(Oversized ? values.Length : maximum).Select(value => new BuildRunKey(value.Revision.OrganizationId, value.BuildId)).ToArray());
        public Task<BuildRunClaim?> ClaimAsync(Guid org, Guid id, Guid worker, CancellationToken token = default)
        {
            Claims++;
            var value = Substitute ? Intent() : values.Single(value => value.BuildId == id);
            return Task.FromResult<BuildRunClaim?>(Completed.ContainsKey(id) ? null :
                new(actor, value, worker, Guid.NewGuid(), 1, DateTimeOffset.UtcNow.Add(BuildRunPolicy.Lease)));
        }
        public Task<bool> RenewAsync(BuildRunClaim claim, CancellationToken token = default) => Task.FromResult(Renewable);
        public Task<bool> ReleaseAsync(BuildRunClaim claim, CancellationToken token = default) { Releases++; return Task.FromResult(true); }
        public Task<bool> CompleteAsync(BuildRunClaim claim, BuildExecutionResult result, CancellationToken token = default)
        { Completed.Add(claim.Intent.BuildId, result); return Task.FromResult(true); }
        public Task<BuildRunSubmitOutcome> SubmitAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BuildRunSnapshot?> ReadAsync(ActorContext actor, Guid org, Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> CancelAsync(ActorContext actor, Guid org, Guid id, CancellationToken token = default) => throw new NotSupportedException();
    }
}
