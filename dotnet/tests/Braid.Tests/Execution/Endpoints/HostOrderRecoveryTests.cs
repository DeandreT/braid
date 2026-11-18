using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed partial class MachineInvocationPersistenceTests
{
    [PostgresFact]
    public async Task Recovery_holds_a_pre_upgrade_unfinished_order_without_a_provable_release()
    {
        await using var test = await Context.CreateAsync(fixture, migrations: 47);
        var id = Guid.NewGuid();
        var original = await test.Claim(id);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, original.Status);
        Assert.Null(original.ReleaseId);
        await test.Case.MigrateAsync();
        await test.ExpireLeaseAsync(id);
        Assert.Equal(MachineInvocationClaimStatus.Refused, (await test.Claim(id)).Status);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", id));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.host_order_pins WHERE request_id=$1", id));
    }

    [PostgresFact]
    public async Task Replica_loss_retries_the_original_release_and_only_the_live_claim_can_finish()
    {
        await using var test = await Context.CreateAsync(fixture);
        var original = test.Release;
        var id = Guid.NewGuid();
        var abandoned = await test.Claim(id);
        Assert.Equal(original.ReleaseId, abandoned.ReleaseId);
        await test.ActivateAsync();
        Assert.NotEqual(original.ReleaseId, test.Release.ReleaseId);
        await test.ExpireLeaseAsync(id);
        await using var replica = new PostgresInvocationLedger(test.Case.RegressionConnectionString("braid_invocation_writer"));
        var key = test.Key(id);
        var digest = Sha256Digest.FromBytes("body"u8);
        var retry = await replica.ClaimAsync(key, test.Application, digest);
        Assert.Equal(original.ReleaseId, retry.ReleaseId);
        Assert.Equal(abandoned.Publisher, retry.Publisher);
        Task<MachineInvocationFinishStatus> Finish(Guid claim, ReleaseIntent release) => replica.FinishAsync(key, claim, test.Application,
            MachineInvocationOutcome.Completed, MachineInvocationProvenance.From(release), "2026-10-22T12:00:00Z", [42]);
        Assert.Equal(MachineInvocationFinishStatus.Superseded, await Finish(abandoned.ClaimId, original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Finish(retry.ClaimId, test.Release));
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await Finish(retry.ClaimId, original));
        Assert.Equal(new byte[] { 42 }, (await test.Claim(id)).RetainedResponse);
    }

    [PostgresFact]
    public async Task Target_capacity_is_shared_across_connections_and_busy_orders_do_not_consume_an_attempt()
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var replica = new PostgresInvocationLedger(test.Case.RegressionConnectionString("braid_invocation_writer"));
        var failed = Guid.NewGuid(); var sibling = Guid.NewGuid(); var next = Guid.NewGuid();
        var first = await test.Claim(failed);
        var second = await test.Claim(sibling);
        Task<MachineInvocationClaim> ClaimNext() => replica.ClaimAsync(test.Key(next), test.Application, Sha256Digest.FromBytes("body"u8));
        Assert.Equal(MachineInvocationClaimStatus.Capacity, (await ClaimNext()).Status);
        var targetB = new LocalReleaseTarget(new(test.Target.Mapping, test.Target.Environment, Guid.NewGuid()),
            test.Releases.Target.Profile, test.Releases.Target.RuntimeImage);
        var store = test.Releases.Store;
        Assert.Equal(ReleaseWriteOutcome.Created, await store.RegisterAsync(test.Actor, targetB));
        var bRelease = new ReleaseIntent(Guid.NewGuid(), test.Actor.PrincipalId, targetB.Reference, test.Releases.First, ReleaseAction.Activate, null);
        Assert.Equal(ReleaseWriteOutcome.Created, await store.RequestAsync(test.Actor, bRelease));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await store.ApplyAsync(test.Actor, targetB.Reference, bRelease.ReleaseId));
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), targetB.Reference, test.Caller)));
        var bKey = new MachineInvocationKey(targetB.Reference, test.Caller, next);
        var flood = Task.WhenAll(Enumerable.Range(0, 6).Select(_ => test.Claim(Guid.NewGuid())));
        var bClaim = await replica.ClaimAsync(bKey, test.Application, Sha256Digest.FromBytes("B"u8)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(MachineInvocationClaimStatus.Claimed, bClaim.Status);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await replica.FinishAsync(bKey, bClaim.ClaimId, test.Application,
            MachineInvocationOutcome.Completed, MachineInvocationProvenance.From(bRelease), Clock, [3]));
        Assert.All(await flood, result => Assert.Equal(MachineInvocationClaimStatus.Capacity, result.Status));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1 AND target_id=$2", next, test.Target.TargetId));
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(failed, first, MachineInvocationOutcome.ResourceLimit, null));
        var admitted = await ClaimNext();
        Assert.Equal(1, admitted.Attempt);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(sibling, second, MachineInvocationOutcome.Completed, [1]));
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(next, admitted, MachineInvocationOutcome.Completed, [2]));
        Assert.Equal(new byte[] { 1 }, (await test.Claim(sibling)).RetainedResponse);
        Assert.Equal(new byte[] { 2 }, (await test.Claim(next)).RetainedResponse);
    }
}
