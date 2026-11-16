using Npgsql;
using Braid.Deployment;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MachineInvocationClockTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData("finish")]
    [InlineData("reclaim")]
    [InlineData("readCompleted")]
    public async Task Expiry_is_rechecked_after_waiting_for_the_invocation_row(string operation)
    {
        await using var test = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var claim = await test.Claim(request);
        var completed = operation == "readCompleted";
        if (completed)
            Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(request, claim, MachineInvocationOutcome.Completed, [1]));
        var table = completed ? "machine_invocation_results" : "machine_invocations";
        var trigger = completed ? "machine_invocation_result_guard" : "machine_invocation_guard";
        await using var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString)
        {
            Pooling = false
        }.ConnectionString);
        await owner.OpenAsync();
        await using var barrier = await owner.BeginTransactionAsync();
        // Time travel is local to the test-owned database. Other sessions still see the admitted definitions.
        await using (var disable = new NpgsqlCommand($"ALTER TABLE braid.{table} DISABLE TRIGGER {trigger}", owner, barrier))
            await disable.ExecuteNonQueryAsync();
        await using (var row = new NpgsqlCommand("SELECT claim_id FROM braid.machine_invocations WHERE request_id=$1 FOR UPDATE", owner, barrier))
        {
            row.Parameters.AddWithValue(request);
            Assert.Equal(claim.ClaimId, await row.ExecuteScalarAsync());
        }
        async Task<object> CallAsync() => operation == "finish"
            ? await test.Finish(request, claim, MachineInvocationOutcome.Completed, [1])
            : await test.Claim(request);
        var pending = CallAsync();
        var released = false;
        try
        {
            var waiting = false;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                await using var observation = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                        WHERE datname=current_database() AND usename='braid_invocation_writer'
                        AND pg_backend_pid()=ANY(pg_catalog.pg_blocking_pids(pid)))
                    """, owner, barrier);
                if (await observation.ExecuteScalarAsync() is true) { waiting = true; break; }
                await Task.Delay(25);
            }
            Assert.True(waiting, "The call must start before expiry and wait for this row.");
            var update = completed
                ? "WITH expiry AS (SELECT clock_timestamp()+interval '200 milliseconds' AS value) UPDATE braid.machine_invocation_results SET retained_at=expiry.value-interval '24 hours',expires_at=expiry.value FROM expiry WHERE request_id=$1"
                : "UPDATE braid.machine_invocations SET lease_until=clock_timestamp()+interval '200 milliseconds' WHERE request_id=$1";
            await using (var move = new NpgsqlCommand(update, owner, barrier))
            {
                move.Parameters.AddWithValue(request);
                await move.ExecuteNonQueryAsync();
            }
            await using (var enable = new NpgsqlCommand($"SET CONSTRAINTS ALL IMMEDIATE; ALTER TABLE braid.{table} ENABLE TRIGGER {trigger}", owner, barrier))
                await enable.ExecuteNonQueryAsync();
            await Task.Delay(300);
            var expiryColumn = completed ? "expires_at" : "lease_until";
            await using (var expiry = new NpgsqlCommand($"SELECT clock_timestamp()>{expiryColumn} FROM braid.{table} WHERE request_id=$1", owner, barrier))
            {
                expiry.Parameters.AddWithValue(request);
                Assert.Equal(true, await expiry.ExecuteScalarAsync());
            }
            await barrier.CommitAsync();
            released = true;
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            if (operation == "finish")
            {
                Assert.Equal(MachineInvocationFinishStatus.Superseded, Assert.IsType<MachineInvocationFinishStatus>(result));
                Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_results"));
            }
            else
            {
                var observed = Assert.IsType<MachineInvocationClaim>(result);
                if (completed)
                {
                    Assert.Equal(MachineInvocationClaimStatus.Recorded, observed.Status);
                    Assert.Equal(MachineInvocationOutcome.Completed, observed.Outcome);
                    Assert.Null(observed.RetainedResponse);
                }
                else
                {
                    Assert.Equal(MachineInvocationClaimStatus.Claimed, observed.Status);
                    Assert.Equal(2, observed.Attempt);
                    Assert.NotEqual(claim.ClaimId, observed.ClaimId);
                    Assert.Equal(MachineInvocationFinishStatus.Superseded, await test.Finish(request, claim, MachineInvocationOutcome.Completed, [1]));
                }
            }
        }
        finally
        {
            if (!released) await barrier.RollbackAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException) { }
        }
    }
}
