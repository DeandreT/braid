using System.Security.Cryptography;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Braid.Triggers.As2;
using Npgsql;
using Xunit;

namespace Braid.As2.Tests;

public sealed class PostgresAs2FenceTests
{
    private sealed class FenceFixtureFactAttribute : FactAttribute
    {
        public FenceFixtureFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BRAID_AS2_FIXTURE_SOCKET") is null) Skip = "Run Fixtures/test_fence.py against a private Unix socket.";
        }
    }

    [FenceFixtureFact]
    public async Task OnlyOneConcurrentCallerMayDispatchAndNoStaleOrCrossScopeClaimCanFence()
    {
        static Guid Id(int n) => new(n, 0, 0, new byte[8]);
        byte[] bytes = [65];
        var target = new ReleaseTargetReference(new(Id(1), Id(2), Id(3)), new(new(Id(1), Id(2)), Id(4), 1), Id(5));
        var route = new TriggerRoute(null, Id(6), TriggerDestinationKind.As2, new("partner", 1));
        var delivery = new FrozenTriggerDelivery(Id(7), Id(8), Id(6), 1, DateTimeOffset.UtcNow, bytes, Sha256Digest.FromBytes(bytes), 1, "application/edi-x12");
        var claim = new TriggerDeliveryClaim(TriggerDeliveryClaimStatus.Claimed, Id(9), 1, DateTimeOffset.UtcNow.AddMinutes(1), delivery, route);
        var expectation = new As2Attempt(Id(7), As2OutputDestination.BindingVersion(route.Connector, Id(6)), delivery.ContentDigest.Value,
            $"<{Id(7):N}@braid.invalid>", Convert.ToBase64String(SHA256.HashData(bytes)), "FROM", "TO", new string('A', 64));
        var config = new NpgsqlConnectionStringBuilder { Host = Environment.GetEnvironmentVariable("BRAID_AS2_FIXTURE_SOCKET"),
            Username = "braid_trigger_worker", Database = "postgres", Port = 5678 };
        await using var source = NpgsqlDataSource.Create(config.ConnectionString);
        var fences = new PostgresAs2ClaimFenceStore(source);
        Assert.False(await fences.TryBeginAsync(new(target.Mapping, target.Environment, Id(15)), claim, expectation, default));
        Assert.False(await fences.TryBeginAsync(target, claim with { ClaimId = Id(15) }, expectation, default));
        Assert.False(await fences.TryBeginAsync(target, claim, expectation with { PayloadSha256 = new string('0', 64) }, default));
        var calls = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fences.TryBeginAsync(target, claim, expectation, default).AsTask()));
        Assert.Single(calls, value => value);
        Assert.False(await fences.TryBeginAsync(target, claim, expectation, default));
    }
}
