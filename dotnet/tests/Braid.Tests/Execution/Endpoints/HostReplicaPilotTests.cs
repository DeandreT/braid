using System.Diagnostics;
using System.Text.Json;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Npgsql;
using Xunit.Abstractions;

namespace Braid.Tests;

public sealed class HostReplicaPilotFactAttribute : FactAttribute
{
    public HostReplicaPilotFactAttribute()
    {
        if (!PostgresFixture.IsConfigured || Environment.GetEnvironmentVariable("BRAID_HOST_REPLICA_PILOT") != "1")
            Skip = "Opt in with BRAID_HOST_REPLICA_PILOT=1 and a task-owned PostgreSQL fixture; waits for the real claim lease.";
    }
}

[Collection(PostgresCollection.Name)]
public sealed class HostReplicaPilotTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [HostReplicaPilotFact]
    public async Task Survivor_serves_B_during_A_saturation_and_recovers_the_killed_replica_after_real_expiry()
    {
        // Use the same complete schema as the scheduled intake/outbox rehearsal.
        await using var test = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, migrations: 65, documents: true);
        var a = test.Target.TargetId;
        var b = new LocalReleaseTarget(new(test.Target.Mapping, test.Target.Environment, Guid.NewGuid()), test.Releases.Target.Profile, test.Releases.Target.RuntimeImage);
        var store = test.Releases.Store;
        Assert.Equal(ReleaseWriteOutcome.Created, await store.RegisterAsync(test.Actor, b));
        var bRelease = new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, b.Reference, test.Releases.First, ReleaseAction.Activate, null);
        Assert.Equal(ReleaseWriteOutcome.Created, await store.RequestAsync(test.Actor, bRelease));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await store.ApplyAsync(test.Actor, b.Reference, bRelease.ReleaseId));
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), b.Reference, test.Caller)));
        var stores = new[] { "braid_invocation_writer", "braid_release_reader", "braid_build_run_reader", "braid_build_reader", "braid_run_reader", "braid_case_reader" }
            .ToDictionary(r => r, r => test.Case.RegressionConnectionString(r));
        stores.Add("braid_runtime", test.Case.RuntimeConnectionString); stores.Add("braid_report_reader", test.Case.ReportConnectionString);
        var setup = new HostOrderReplicaProbe.Setup(stores, test.Target, test.Caller, test.Application, b.RuntimeImage);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        await using var first = new Child(); await using var survivor = new Child();
        await first.Start(setup, token); await survivor.Start(setup, token);
        var lost = Guid.NewGuid(); var original = test.Release.ReleaseId;
        await first.Send(new(a, lost, true), token);
        Assert.Equal("dispatched", await first.Read(token));
        var loss = Stopwatch.StartNew();
        await first.Kill();
        Assert.Equal(MachineInvocationStatus.InProgress, (await survivor.Invoke(new(a, lost), token)).Status);
        var secondSlot = Guid.NewGuid(); var occupied = await test.Claim(secondSlot);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, occupied.Status);
        var times = new List<long>();
        // The generic test fixture uses one connection; the six flood clients must reach database admission, not queue on that pool.
        await using var load = new PostgresInvocationLedger(new NpgsqlConnectionStringBuilder(stores["braid_invocation_writer"]) { MaxPoolSize = 6 }.ConnectionString);
        for (var i = 0; i < 5; i++)
        {
            // Each timed call competes with six fresh A requests against the same database.
            var flood = Task.WhenAll(Enumerable.Range(0, 6).Select(_ => load.ClaimAsync(test.Key(Guid.NewGuid()), test.Application, Sha256Digest.FromBytes("body"u8), token)));
            var elapsed = Stopwatch.StartNew();
            var bResult = await survivor.Invoke(new(b.Reference.TargetId, Guid.NewGuid()), token);
            times.Add(elapsed.ElapsedMilliseconds);
            Assert.Equal(MachineInvocationStatus.Completed, bResult.Status);
            Assert.Equal(bRelease.ReleaseId, bResult.Release);
            Assert.All(await flood, c => Assert.Equal(MachineInvocationClaimStatus.Capacity, c.Status));
        }
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(secondSlot, occupied, MachineInvocationOutcome.ResourceLimit, null));
        await test.ActivateAsync();
        Assert.NotEqual(original, test.Release.ReleaseId);
        var remaining = await test.Case.ScalarAsync<TimeSpan>("SELECT greatest(lease_until-clock_timestamp(),interval '0') FROM braid.machine_invocations WHERE request_id=$1", lost);
        await Task.Delay(remaining + TimeSpan.FromMilliseconds(100), token);
        var result = await survivor.Invoke(new(a, lost), token);
        var recoveryMs = loss.ElapsedMilliseconds;
        Assert.Equal(MachineInvocationStatus.Completed, result.Status);
        Assert.Equal(original, result.Release); Assert.False(result.Replay);
        var replay = await survivor.Invoke(new(a, lost), token);
        Assert.Equal(result with { Replay = true }, replay);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1 AND kind='completed'", lost));
        output.WriteLine($"B round-trip ms=[{string.Join(',', times)}]; failures=0/5; A capacity refusals=30/30; kill-to-recovered-result ms={recoveryMs}");
        Assert.All(times, ms => Assert.InRange(ms, 0L, 30_000L));
        Assert.InRange(recoveryMs, 0L, 150_000L);
    }

    private sealed class Child : IAsyncDisposable
    {
        private readonly Process process;
        internal Child()
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(typeof(HostOrderReplicaProbe).Assembly.Location);
            start.ArgumentList.Add("host-replica-probe");
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "DOTNET_ROOT", "TMPDIR", "SystemRoot", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
            process = Process.Start(start) ?? throw new InvalidOperationException("Replica probe failed to start.");
        }
        internal async Task Start(HostOrderReplicaProbe.Setup setup, CancellationToken token)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(setup).AsMemory(), token);
            Assert.Equal("ready", await Read(token));
        }
        internal Task Send(HostOrderReplicaProbe.Order order, CancellationToken token) => process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(order).AsMemory(), token);
        internal async Task<string> Read(CancellationToken token) => await process.StandardOutput.ReadLineAsync(token) ?? throw new InvalidOperationException("Replica closed the pipe.");
        internal async Task<HostOrderReplicaProbe.Result> Invoke(HostOrderReplicaProbe.Order order, CancellationToken token)
        {
            await Send(order, token);
            return JsonSerializer.Deserialize<HostOrderReplicaProbe.Result>(await Read(token))!;
        }
        internal async Task Kill()
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        public async ValueTask DisposeAsync()
        {
            try { await Kill(); } finally { process.Dispose(); }
        }
    }
}
