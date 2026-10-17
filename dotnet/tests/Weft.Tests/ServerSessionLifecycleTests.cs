using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Weft.Application;
using Weft.Identity;

namespace Weft.Tests;

public sealed class ServerSessionLifecycleTests
{
    [Theory]
    [InlineData(ServerSessionWriteOutcome.Denied, ServerSessionIdentityFailureKind.Denied)]
    [InlineData(ServerSessionWriteOutcome.Conflict, ServerSessionIdentityFailureKind.HandleCollision)]
    public async Task ClosedCreateRefusalsNeverRetryOrAdoptAnotherRow(ServerSessionWriteOutcome outcome,
        ServerSessionIdentityFailureKind expected)
    {
        using var h = new SessionStoreTestHarness();
        h.Ports.Create = (_, _) => Task.FromResult(ServerSessionWriteResult.WithoutFacts(outcome));
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Create());
        Assert.Equal(expected, error.Kind);
        Assert.True(h.Ports.Creates == 1 && h.Ports.Reads == 0, "Create retried or reconciled implicitly.");
    }

    [Fact]
    public async Task CreateFailureAfterSyntheticDurableCaptureNeverBecomesAcknowledgement()
    {
        using var h = new SessionStoreTestHarness();
        ServerSessionFacts? captured = null;
        h.Ports.Create = (intent, _) =>
        {
            captured = new(intent.Key, intent.BindingId, intent.BindingVersion, intent.LoginEpoch,
                ServerSessionPolicy.InitialLifetime(SessionStoreTestHarness.Now, intent.VerifiedLoginDeadlineUtc)!,
                1, intent.CopyProtectedTicket());
            throw new InvalidOperationException("private-ack-detail");
        };
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Create());
        Assert.Equal(ServerSessionIdentityFailureKind.Unavailable, error.Kind);
        Assert.True(captured is not null && h.Ports.Creates == 1 && h.Ports.Reads == 0,
            "Synthetic unknown acknowledgement was retried or adopted.");
        Assert.True(error.InnerException is null && !error.Message.Contains("private-ack-detail", StringComparison.Ordinal),
            "A private error detail escaped.");
        // Only a synthetic dispatch test: real lost-COMMIT acknowledgement needs actual PG qualification.
    }

    [Fact]
    public async Task CreatedAcknowledgementWithChangedEpochFailsIntegrity()
    {
        using var h = new SessionStoreTestHarness();
        h.Ports.Create = (intent, _) => Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(
            ServerSessionWriteOutcome.Created, new(intent.Key, intent.BindingId, intent.BindingVersion,
                Guid.Parse("50000000-0000-0000-0000-000000000005"),
                ServerSessionPolicy.InitialLifetime(SessionStoreTestHarness.Now, intent.VerifiedLoginDeadlineUtc)!,
                1, intent.CopyProtectedTicket())));
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Create());
        Assert.Equal(ServerSessionIdentityFailureKind.IntegrityFailure, error.Kind);
        Assert.True(h.Ports.Creates == 1, "Malformed acknowledgement was retried.");
    }

    [Fact]
    public async Task ReaderIsFreshAndCurrentDisabledOrInactiveMetadataReturnsNull()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        Assert.True((await h.Lifecycle.RetrieveAsync(created.Handle, default)) is not null,
            "Current synthetic metadata did not produce a ticket.");
        h.Ports.BindingEnabled = false;
        Assert.True((await h.Lifecycle.RetrieveAsync(created.Handle, default)) is null,
            "Disabled binding retained identity.");
        h.Ports.BindingEnabled = true;
        h.Ports.PrincipalActive = false;
        Assert.True((await h.Lifecycle.RetrieveAsync(created.Handle, default)) is null,
            "Inactive principal retained identity.");
        Assert.True(h.Ports.Reads == 3, "Positive identity was cached.");
    }

    [Fact]
    public async Task CorruptPositiveProtectedBytesAreIntegrityFailureNotAnonymous()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        var bytes = created.Facts.CopyProtectedTicket();
        try
        {
            bytes[^1] ^= 1;
            h.Ports.Row = Copy(created.Facts, bytes: bytes);
            var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() =>
                h.Lifecycle.RetrieveAsync(created.Handle, default));
            Assert.Equal(ServerSessionIdentityFailureKind.IntegrityFailure, error.Kind);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Assert.True(h.Ports.Reads == 1 && h.Ports.Creates == 1, "Corrupt metadata caused a replacement.");
    }

    [Fact]
    public async Task RenewalAcknowledgesOneVersionedExtensionWithImmutableFrameworkDates()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        var result = await h.Lifecycle.RenewAsync(created.Handle, ticket!, default);
        Assert.Equal(ServerSessionRenewalKind.AcknowledgedRenewed, result.Kind);
        Assert.True(h.Ports.Row!.Version == 2 && h.Ports.Renews == 1 && h.Ports.Reads == 2,
            "Renewal CAS dispatch changed.");
        Assert.True(result.CopyTicket().Properties.IssuedUtc == ticket!.Properties.IssuedUtc &&
            result.CopyTicket().Properties.ExpiresUtc == ticket.Properties.ExpiresUtc,
            "Renewal changed immutable projected framework dates.");
    }

    [Fact]
    public async Task NoOpRenewalPreservesVersionAndIsExplicitlyAcknowledged()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        h.Ports.Renew = (_, expected, _) =>
        {
            Assert.True(expected == 1, "CAS version changed.");
            return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Unchanged, created.Facts));
        };
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        var result = await h.Lifecycle.RenewAsync(created.Handle, ticket!, default);
        Assert.Equal(ServerSessionRenewalKind.AcknowledgedUnchanged, result.Kind);
        Assert.True(h.Ports.Row!.Version == 1 && h.Ports.Renews == 1, "No-op mutated or retried.");
    }

    [Fact]
    public async Task ConcurrentRenewalObservationIsDistinctAndNeverPerformsSecondWrite()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        h.Ports.Renew = (_, expected, _) =>
        {
            Assert.True(expected == 1, "CAS used another version.");
            h.Ports.Row = Copy(created.Facts, version: 2, idle: created.Facts.Lifetime.IdleExpiresAtUtc.AddMinutes(1));
            return Task.FromResult(ServerSessionWriteResult.WithoutFacts(ServerSessionWriteOutcome.Conflict));
        };
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        var result = await h.Lifecycle.RenewAsync(created.Handle, ticket!, default);
        Assert.Equal(ServerSessionRenewalKind.ObservedConcurrent, result.Kind);
        Assert.True(h.Ports.Renews == 1 && h.Ports.Reads == 3, "Conflict observation polled or rewrote.");
        var a = result.CopyTicket();
        var b = result.CopyTicket();
        a.Properties.Items["private-test-item"] = "private-test-value";
        Assert.True(!b.Properties.Items.ContainsKey("private-test-item"), "Renewal ticket copy aliases mutable properties.");
    }

    [Fact]
    public async Task ConflictWithoutStrictlyLaterVersionFailsWithoutRetry()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        h.Ports.Renew = (_, _, _) => Task.FromResult(ServerSessionWriteResult.WithoutFacts(ServerSessionWriteOutcome.Conflict));
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() =>
            h.Lifecycle.RenewAsync(created.Handle, ticket!, default));
        Assert.Equal(ServerSessionIdentityFailureKind.IntegrityFailure, error.Kind);
        Assert.True(h.Ports.Renews == 1 && h.Ports.Reads == 3, "Conflict retried or polled.");
    }

    [Fact]
    public async Task AcknowledgedRenewalCannotReplaceProtectedBytes()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        h.Ports.Renew = (_, _, _) => Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(
            ServerSessionWriteOutcome.Renewed, Copy(created.Facts, version: 2,
                idle: created.Facts.Lifetime.IdleExpiresAtUtc.AddMinutes(1), bytes: new byte[] { 1, 2, 3 })));
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => h.Lifecycle.RenewAsync(created.Handle, ticket!, default));
        Assert.Equal(ServerSessionIdentityFailureKind.IntegrityFailure, error.Kind);
        Assert.True(h.Ports.Renews == 1, "Invalid renewal caused another write.");
    }

    [Fact]
    public async Task RevokeBypassesLiveReaderEvenWithExpiredInactiveMetadata()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        h.Ports.BindingEnabled = false;
        h.Ports.PrincipalActive = false;
        h.Ports.Row = new(created.Facts.Key, created.Facts.BindingId, created.Facts.BindingVersion,
            created.Facts.LoginEpoch, new(SessionStoreTestHarness.Now.AddHours(-2),
                SessionStoreTestHarness.Now.AddHours(-1).AddMinutes(-40), SessionStoreTestHarness.Now.AddHours(-1)),
            1, created.Facts.CopyProtectedTicket());
        await h.Lifecycle.RevokeAsync(created.Handle, default);
        Assert.True(h.Ports.Reads == 0 && h.Ports.Revokes == 1 && h.Ports.Row!.RevokedAtUtc is not null,
            "Revoke depended on live identity or failed to retain tombstone.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionExhaustionNeverReportsRenewOrLogoutSuccess(bool revoke)
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        h.Ports.Row = Copy(created.Facts, version: long.MaxValue);
        h.Ports.Renew = (_, _, _) => Task.FromResult(ServerSessionWriteResult.WithoutFacts(ServerSessionWriteOutcome.VersionExhausted));
        h.Ports.Revoke = (_, _) => Task.FromResult(ServerSessionWriteResult.WithoutFacts(ServerSessionWriteOutcome.VersionExhausted));
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() => revoke
            ? h.Lifecycle.RevokeAsync(created.Handle, default)
            : h.Lifecycle.RenewAsync(created.Handle, ticket!, default));
        Assert.Equal(ServerSessionIdentityFailureKind.VersionExhausted, error.Kind);
        Assert.True(h.Ports.Renews + h.Ports.Revokes == 1, "Exhaustion caused a retry.");
    }

    [Fact]
    public async Task RenewChangedWholeSecondDateFailsBeforeWrite()
    {
        using var h = new SessionStoreTestHarness();
        var created = await h.Create();
        var ticket = await h.Lifecycle.RetrieveAsync(created.Handle, default);
        ticket!.Properties.ExpiresUtc = ticket.Properties.ExpiresUtc!.Value.AddSeconds(1);
        var error = await Assert.ThrowsAsync<ServerSessionIdentityException>(() =>
            h.Lifecycle.RenewAsync(created.Handle, ticket, default));
        Assert.Equal(ServerSessionIdentityFailureKind.IntegrityFailure, error.Kind);
        Assert.True(h.Ports.Renews == 0, "Changed framework deadline reached a write.");
    }

    private static ServerSessionFacts Copy(ServerSessionFacts row, long? version = null,
        DateTimeOffset? idle = null, byte[]? bytes = null) => new(row.Key, row.BindingId, row.BindingVersion,
        row.LoginEpoch, new(row.Lifetime.IssuedAtUtc, idle ?? row.Lifetime.IdleExpiresAtUtc,
            row.Lifetime.AbsoluteExpiresAtUtc), version ?? row.Version, bytes ?? row.CopyProtectedTicket(),
        row.RevokedAtUtc, row.PolicyVersion, row.CodecVersion);
}
