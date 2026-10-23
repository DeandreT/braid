using Weft.Identity;

namespace Weft.Tests;

public sealed partial class EntraProtocolHttpsTests
{
    [Fact]
    public async Task PartialMetadataAndTokenResponseBodiesDrainOnDisposalBeforeBorrowedDependenciesAreReused()
    {
        foreach (bool metadata in new[] { true, false })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                var gate = new EntraProtocolGate();
                EntraProtocolFlow? flow = metadata ? null : await test.FlowAsync();
                if (metadata) test.Issuer.MetadataBodyGate = gate; else test.Issuer.TokenBodyGate = gate;
                var record = test.NewRequest(metadata ? EntraProtocolOperation.Begin : EntraProtocolOperation.Callback);
                int before = test.Ledger().Count;
                var pending = metadata ? test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin, record: record)
                    : test.CompleteAsync(flow!, record: record);
                Task? disposal = null;
                try
                {
                    // The facade's task is recorded only once its synchronous part returns,
                    // which can be after the issuer has already entered the stalled body.
                    await Task.WhenAll(gate.Entered.Task, record.OwnedStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
                    PartialBodyIsOutstanding(gate);
                    EntraProtocolFixturePolicy.Check(record.OwnedWork is { IsCompleted: false } && !pending.IsCompleted
                        && record.EvidencePublished == 0 && record.Challenge is null && record.Verified is null,
                        "A partial metadata/token response was treated as completed protocol work before its body settled.");
                    disposal = test.Facade.DisposeAsync().AsTask(); await disposal.WaitAsync(TimeSpan.FromSeconds(15));
                    var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10)); await gate.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    test.Denied(reply, ServerSessionIdentityFailureKind.Unavailable); test.CheckPrivacy(reply);
                    EntraProtocolFixturePolicy.Check(disposal.IsCompletedSuccessfully && record.OwnedWork is { IsFaulted: true }
                        && record.Challenge is null && Volatile.Read(ref gate.Canceled) == 1 && !gate.Release.Task.IsCompleted
                        && test.Ledger().Count == before + 1 && test.Ports.Calls == 0,
                        "Disposal returned without draining the original partial response read, or relied on releasing its stalled tail.");
                    if (flow is not null) EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
                    int settled = test.Ledger().Count;
                    var refused = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin);
                    test.Denied(refused, ServerSessionIdentityFailureKind.Unavailable);
                    EntraProtocolFixturePolicy.Check(test.Ledger().Count == settled,
                        "A closed partial-response runtime admitted later HTTP after its disposal completed.");
                }
                finally
                {
                    gate.Release.TrySetResult(); test.Issuer.MetadataBodyGate = null; test.Issuer.TokenBodyGate = null;
                    await DrainAsync(pending); if (disposal is not null) await DrainAsync(disposal);
                }
                await ReuseBorrowedDependenciesAsync(test);
            });
    }

    [Fact]
    public Task PartialTokenResponseBodyCancellationDrainsTheOriginalOperationWithoutResend() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var flow = await test.FlowAsync(); var gate = new EntraProtocolGate(); test.Issuer.TokenBodyGate = gate;
        var record = test.NewRequest(ownCancellation: true); int before = test.Ledger().Count;
        var pending = test.CompleteAsync(flow, record: record); Task? disposal = null;
        try
        {
            await Task.WhenAll(gate.Entered.Task, record.OwnedStarted.Task).WaitAsync(TimeSpan.FromSeconds(10)); PartialBodyIsOutstanding(gate);
            EntraProtocolFixturePolicy.Check(record.OwnedWork is { IsCompleted: false } && !pending.IsCompleted && record.EvidencePublished == 0,
                "A partial token response published evidence or released the original callback before cancellation.");
            record.OriginalCancellation!.Cancel();
            var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10)); await gate.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            EntraProtocolFixturePolicy.Check(record.Canceled && record.CancellationWasOriginal && record.OwnedWork is { IsCanceled: true }
                && record.Failure is null && record.Verified is null && record.EvidencePublished == 0
                && Volatile.Read(ref gate.Canceled) == 1 && !gate.Release.Task.IsCompleted && test.Ledger().Count == before + 1
                && test.Ports.Calls == 0, "Partial-body cancellation lost its original cause, left a live read or retried redemption.");
            test.CheckPrivacy(reply); EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
            disposal = test.Facade.DisposeAsync().AsTask(); await disposal.WaitAsync(TimeSpan.FromSeconds(15));
            EntraProtocolFixturePolicy.Check(test.Ledger().Count == before + 1,
                "Disposing a canceled partial-body runtime introduced a delayed outbound resend.");
        }
        finally
        {
            gate.Release.TrySetResult(); test.Issuer.TokenBodyGate = null;
            await DrainAsync(pending); if (disposal is not null) await DrainAsync(disposal);
        }
        await ReuseBorrowedDependenciesAsync(test);
    });

    [Fact]
    public async Task PartialUnknownKeyRefreshBodiesDrainAfterCallbackAndBeforeBorrowedDependenciesAreReused()
    {
        foreach (bool metadata in new[] { true, false })
            await EntraProtocolHttpsFixture.RunAsync(async test =>
            {
                var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed);
                var current = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
                EntraProtocolFixturePolicy.Check(current.SigningKeys.Any(key => key.KeyId == "weft-k1")
                    && current.SigningKeys.All(key => key.KeyId != "weft-k2"),
                    "The partial-refresh control did not start with an admitted K1-only configuration.");
                var flow = await test.FlowAsync(); var gate = new EntraProtocolGate();
                if (metadata) test.Issuer.MetadataBodyGate = gate; else test.Issuer.KeysBodyGate = gate;
                test.Issuer.PublishSecond = true; test.Issuer.OnlySecond = true; test.Issuer.SignSecond = true;
                int tokenBefore = test.Ledger().Count(value => value.LogicalPath == EntraProtocolFixturePolicy.Token);
                int wireTokenBefore = test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Token);
                var pending = test.CompleteAsync(flow); Task? disposal = null;
                try
                {
                    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); PartialBodyIsOutstanding(gate);
                    // The standard refresh is nonblocking. Its body must still be owned
                    // even after the original unknown-kid callback has already refused.
                    var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10)); test.Denied(reply); test.CheckPrivacy(reply);
                    EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow); PartialBodyIsOutstanding(gate);
                    EntraProtocolFixturePolicy.Check(reply.Request.OwnedWork is { IsFaulted: true }
                        && test.Ledger().Count(value => value.LogicalPath == EntraProtocolFixturePolicy.Token) == tokenBefore + 1
                        && test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Token) == wireTokenBefore + 1,
                        "A stalled key refresh retained callback ownership, published an unadmitted key or resent the original token redemption.");
                    int beforeDisposal = test.Ledger().Count;
                    disposal = test.Facade.DisposeAsync().AsTask(); await disposal.WaitAsync(TimeSpan.FromSeconds(15));
                    await gate.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    EntraProtocolFixturePolicy.Check(disposal.IsCompletedSuccessfully && Volatile.Read(ref gate.Canceled) == 1
                        && !gate.Release.Task.IsCompleted && test.Ledger().Count == beforeDisposal && test.Ports.Calls == 0,
                        "Runtime disposal failed to cancel/drain the outstanding refresh body before borrowed dependencies could be reused.");
                    var refused = await test.SendAsync("/api/auth/login", operation: EntraProtocolOperation.Begin);
                    test.Denied(refused, ServerSessionIdentityFailureKind.Unavailable);
                    EntraProtocolFixturePolicy.Check(test.Ledger().Count == beforeDisposal,
                        "A closed refresh runtime sent queued or later HTTP after disposal.");
                }
                finally
                {
                    gate.Release.TrySetResult(); test.Issuer.MetadataBodyGate = null; test.Issuer.KeysBodyGate = null;
                    await DrainAsync(pending); if (disposal is not null) await DrainAsync(disposal);
                }
                await ReuseBorrowedDependenciesAsync(test);
            });
    }

    private static void PartialBodyIsOutstanding(EntraProtocolGate gate) => EntraProtocolFixturePolicy.Check(
        gate.PrefixBytes > 0 && gate.PrefixBytes < gate.DeclaredBytes && !gate.Release.Task.IsCompleted && !gate.Ended.Task.IsCompleted,
        "The controlled response had not flushed a genuine partial body, or its unread tail was already released/settled.");

    private static async Task ReuseBorrowedDependenciesAsync(EntraProtocolHttpsFixture test)
    {
        var originalProfile = test.Profile; var originalCertificate = test.Crypto.ClientCertificate;
        test.Files.AssertEncryptedRing();
        await test.ReplaceApplicationAsync(independentProfile: false);
        EntraProtocolFixturePolicy.Check(ReferenceEquals(originalProfile, test.Profile)
            && ReferenceEquals(originalCertificate, test.Crypto.ClientCertificate) && test.SecondProfile is null,
            "The post-drain positive control replaced rather than reused borrowed profile/certificate resources.");
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
    }
}
