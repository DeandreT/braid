using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Jobs;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresRegressionDispatchTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Only_a_committed_request_becomes_discoverable_and_rollback_leaves_no_work()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.WriterConnection) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var settings = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,true),set_config('braid.run_principal_id',@actor,true)", connection, transaction);
            settings.Parameters.AddWithValue("org", test.Organization.ToString("D"));
            settings.Parameters.AddWithValue("actor", test.Actor.PrincipalId.Value.ToString("D"));
            await settings.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand("SELECT braid.submit_regression_run(@org,@project,@mapping,@id,@case,@version,@owner,@revision,@native,@digest,@bytes)", connection, transaction);
            command.Parameters.AddWithValue("org", test.Organization);
            command.Parameters.AddWithValue("project", test.Reference.Revision.ProjectId);
            command.Parameters.AddWithValue("mapping", test.Reference.Revision.MappingId);
            command.Parameters.AddWithValue("id", test.RunId);
            command.Parameters.AddWithValue("case", test.Reference.CaseId);
            command.Parameters.AddWithValue("version", test.Reference.Version);
            command.Parameters.AddWithValue("owner", test.Reference.Owner.Value);
            command.Parameters.AddWithValue("revision", test.Reference.Revision.RevisionId);
            command.Parameters.AddWithValue("native", test.Reference.NativeDigest.Value);
            command.Parameters.AddWithValue("digest", test.Reference.ContentDigest.Value);
            command.Parameters.AddWithValue("bytes", test.Reference.ByteCount);
            Assert.Equal(0, await command.ExecuteScalarAsync());
            Assert.Empty(await test.Queue.ListReadyAsync(16));
            await transaction.RollbackAsync();
        }
        Assert.Empty(await test.Queue.ListReadyAsync(16));
        Assert.Equal(RegressionRunSubmitOutcome.Created, await test.SubmitAsync());
        Assert.Equal(new RegressionRunReady(test.Organization, test.RunId), Assert.Single(await test.Queue.ListReadyAsync(16)));
    }

    [PostgresFact]
    public async Task Live_claims_and_terminal_runs_are_not_dispatched_again()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        await test.SubmitAsync();
        Assert.Single(await test.Queue.ListReadyAsync(16));
        var claim = await test.ClaimAsync();
        Assert.Empty(await test.Queue.ListReadyAsync(16));
        Assert.True(await test.Writer.CompleteAsync(claim, test.Success()));
        Assert.Empty(await test.Queue.ListReadyAsync(16));
        Assert.Equal(RegressionRunSubmitOutcome.Replayed, await test.SubmitAsync());
        Assert.Empty(await test.Queue.ListReadyAsync(16));
    }

    [PostgresFact]
    public async Task Release_waits_for_the_database_retry_time_before_rediscovery()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        await test.SubmitAsync();
        Assert.True(await test.Writer.ReleaseAsync(await test.ClaimAsync()));
        Assert.Empty(await test.Queue.ListReadyAsync(16));
        await Task.Delay(1100);
        Assert.Single(await test.Queue.ListReadyAsync(16));
    }

    [PostgresFact]
    public async Task Discovery_is_bounded_and_rejects_reader_credentials_and_scope_reuse()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        for (var index = 0; index < 20; index++)
            Assert.Equal(RegressionRunSubmitOutcome.Created, await test.Writer.SubmitAsync(new(test.Actor, Guid.NewGuid(), test.Reference)));
        var page = await test.Queue.ListReadyAsync(16);
        Assert.Equal(16, page.Count);
        Assert.Equal(16, page.Distinct().Count());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => test.Queue.ListReadyAsync(17));
        await using var invalid = new PostgresRegressionRunQueue(test.ReaderConnection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ListReadyAsync(16));
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.WriterConnection) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT set_config('braid.organization_id',@org,false); SELECT * FROM braid.list_ready_regression_runs(16)", connection);
        command.Parameters.AddWithValue("org", test.Organization.ToString("D"));
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [PostgresFact]
    public async Task Fresh_queue_discovers_retained_work_after_database_restart()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        await test.SubmitAsync();
        Assert.Single(await test.Queue.ListReadyAsync(16));
        await test.Queue.DisposeAsync();
        await test.Writer.DisposeAsync();
        await fixture.RestartAsync();
        await using var fresh = new PostgresRegressionRunQueue(test.WriterConnection);
        Assert.Equal(test.RunId, Assert.Single(await fresh.ListReadyAsync(16)).RunId);
    }

    [PostgresFact]
    public async Task A_lost_completion_acknowledgement_does_not_requeue_the_committed_result()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        await test.SubmitAsync();
        var claim = await test.ClaimAsync();
        await using var relay = new ServerSessionCommitAckLossRelay(test.WriterConnection);
        await using var routed = new PostgresRegressionRunStore(relay.RoutedConnectionString, writer: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = routed.CompleteAsync(claim, test.Success(), deadline.Token);
        try
        {
            await Task.WhenAny(pending, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !pending.IsCompleted);
            Assert.Empty(await test.Queue.ListReadyAsync(16, deadline.Token));
            relay.DropAcknowledgement();
            await Assert.ThrowsAnyAsync<NpgsqlException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.Equal(RegressionRunClaimOutcome.Finished, (await test.Writer.ClaimAsync(test.Organization, test.RunId, Guid.NewGuid())).Outcome);
        }
        finally { relay.DropAcknowledgement(); try { await pending; } catch (NpgsqlException) { } }
    }

    [PostgresFact]
    public async Task The_background_worker_discovers_a_run_committed_after_start_and_never_reexecutes_it()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        var execution = new Execution(_ => Task.FromResult(test.Success()));
        using var host = HostFor(test, execution);
        await host.StartAsync();
        try
        {
            await test.SubmitAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId, deadline.Token))?.State != RegressionRunState.Completed)
                await Task.Delay(100, deadline.Token);
            await Task.Delay(1200, deadline.Token);
            Assert.Equal(1, execution.Calls);
            Assert.Empty(await test.Queue.ListReadyAsync(16, deadline.Token));
        }
        finally { await host.StopAsync(); }
    }

    [PostgresFact]
    public async Task Host_shutdown_cancels_execution_and_leaves_the_owned_run_retryable()
    {
        await using var test = await RegressionRunPostgresTestSupport.CreateAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new Execution(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
            return test.Success();
        });
        await test.SubmitAsync();
        using var host = HostFor(test, execution);
        await host.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await host.StopAsync(deadline.Token);
            Assert.True(canceled.Task.IsCompletedSuccessfully);
            Assert.Equal(RegressionRunState.Queued, (await test.Reader.ReadAsync(test.Actor, test.Organization, test.RunId, deadline.Token))!.State);
        }
        finally { await host.StopAsync(); }
    }

    private static IHost HostFor(RegressionRunPostgresTestSupport test, IRegressionCaseExecutor execution)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IRegressionRunQueue>(test.Queue);
        builder.Services.AddSingleton<IRegressionRunDispatcher>(new RegressionRunExecutor(test.Writer, execution));
        builder.Services.AddSingleton<RegressionRunPump>();
        builder.Services.AddHostedService<RegressionWorkerService>();
        return builder.Build();
    }

    private sealed class Execution(Func<CancellationToken, Task<RegressionCaseExecution>> callback) : IRegressionCaseExecutor
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public Task<RegressionCaseExecution> ExecuteAsync(ActorContext actor, RegressionCaseReference expected, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return callback(cancellationToken);
        }
    }
}
