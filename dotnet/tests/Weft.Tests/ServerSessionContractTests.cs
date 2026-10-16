using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Pure metadata/privacy vectors; no protected codec, authentication or durable store is exercised.</summary>
public sealed class ServerSessionContractTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Binding = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Epoch = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly PrincipalId Principal = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly DateTimeOffset Issued = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Ticket = Encoding.UTF8.GetBytes("synthetic-private-ticket");
    private static ServerSessionKey Key => new(Organization, Sha256Digest.Parse(new string('a', 64)));
    private static ServerSessionLifetime Lifetime => new(Issued, Issued.AddMinutes(20), Issued.AddMinutes(60));
    private static ServerSessionFacts Facts(long version = 1, DateTimeOffset? revoked = null) =>
        new(Key, Binding, 3, Epoch, Lifetime, version, Ticket, revoked);
    private static ServerSessionCreateIntent Intent() => new(Key, Binding, 3, Principal, Epoch,
        Issued.AddMinutes(90), Ticket);

    [Fact]
    public void KeyUsesExistingDigestValidationAndRetainsExactOrganization()
    {
        Assert.Throws<ArgumentException>(() => new ServerSessionKey(Guid.Empty, Key.HandleDigest));
        Assert.Throws<ArgumentNullException>(() => new ServerSessionKey(Organization, null!));
        Assert.Equal(Key, new ServerSessionKey(Organization, Sha256Digest.Parse(new string('A', 64))));
        Assert.NotEqual(Key, new ServerSessionKey(Binding, Key.HandleDigest));
        Assert.NotEqual(Key, new ServerSessionKey(Organization, Sha256Digest.Parse(new string('b', 64))));
    }

    [Fact]
    public void SessionFactsRequireNonemptyIdentityAndPositiveBoundedVersions()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerSessionFacts(null!, Binding, 3, Epoch, Lifetime, 1, Ticket));
        Assert.Throws<ArgumentException>(() => new ServerSessionFacts(Key, Guid.Empty, 3, Epoch, Lifetime, 1, Ticket));
        Assert.Throws<ArgumentException>(() => new ServerSessionFacts(Key, Binding, 3, Guid.Empty, Lifetime, 1, Ticket));
        Assert.Throws<ArgumentNullException>(() => new ServerSessionFacts(Key, Binding, 3, Epoch, null!, 1, Ticket));
        foreach (var version in new[] { long.MinValue, -1, 0 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionFacts(Key, Binding, version, Epoch, Lifetime, 1, Ticket));
            Assert.Throws<ArgumentOutOfRangeException>(() => Facts(version));
        }
        var maximum = new ServerSessionFacts(Key, Binding, long.MaxValue, Epoch, Lifetime, long.MaxValue, Ticket);
        Assert.Equal(long.MaxValue, maximum.BindingVersion);
        Assert.Equal(long.MaxValue, maximum.Version);
        Assert.Equal(Epoch, maximum.LoginEpoch);
    }

    [Fact]
    public void LifetimeRejectsEqualReversedOrOverOneHourBounds()
    {
        Assert.Throws<ArgumentException>(() => new ServerSessionLifetime(Issued, Issued, Issued.AddMinutes(60)));
        Assert.Throws<ArgumentException>(() => new ServerSessionLifetime(Issued, Issued.AddMinutes(21), Issued.AddMinutes(20)));
        Assert.Throws<ArgumentException>(() => new ServerSessionLifetime(Issued, Issued.AddMinutes(20), Issued.AddMinutes(60).AddTicks(10)));
        var exactMicrosecond = new ServerSessionLifetime(Issued, Issued.AddTicks(10), Issued.AddTicks(10));
        Assert.Equal(Issued.AddTicks(10), exactMicrosecond.IdleExpiresAtUtc);
    }

    [Fact]
    public void EveryTimeBoundaryRejectsNonUtcSubMicrosecondAndInfinitySentinels()
    {
        var invalid = new[] { Issued.ToOffset(TimeSpan.FromHours(1)), Issued.AddTicks(1),
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue };
        foreach (var instant in invalid)
        {
            Assert.Throws<ArgumentException>(() => new ServerSessionLifetime(instant, Issued.AddMinutes(20), Issued.AddMinutes(60)));
            Assert.Throws<ArgumentException>(() => new ServerSessionLifetime(Issued, instant, Issued.AddMinutes(60)));
            Assert.Throws<ArgumentException>(() => new ServerSessionLifetime(Issued, Issued.AddMinutes(20), instant));
            Assert.Throws<ArgumentException>(() => new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch, instant, Ticket));
            Assert.Throws<ArgumentException>(() => Facts(2, instant));
            Assert.Throws<ArgumentException>(() => ServerSessionPolicy.InitialLifetime(instant, Issued.AddMinutes(60)));
            Assert.Throws<ArgumentException>(() => ServerSessionPolicy.InitialLifetime(Issued, instant));
            Assert.Throws<ArgumentException>(() => ServerSessionPolicy.IsLiveAt(Facts(), instant));
            Assert.Throws<ArgumentException>(() => ServerSessionPolicy.ClassifyRevoke(Key, instant, null));
        }
    }

    [Fact]
    public void RevocationHasCoherentUtcHistoryAndCannotAppearAtVersionOne()
    {
        Assert.Throws<ArgumentException>(() => Facts(1, Issued));
        Assert.Throws<ArgumentException>(() => Facts(2, Issued.AddTicks(-10)));
        var revoked = Facts(2, Issued.AddMinutes(70));
        Assert.Equal(Issued.AddMinutes(70), revoked.RevokedAtUtc);
        Assert.False(ServerSessionPolicy.IsLiveAt(revoked, Issued));
    }

    [Fact]
    public void TicketIsNonemptyBoundedAndDefensivelyCopiedOnBothSides()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[16385] })
        {
            Assert.Throws<ArgumentException>(() => new ServerSessionFacts(Key, Binding, 3, Epoch, Lifetime, 1, bytes));
            Assert.Throws<ArgumentException>(() => new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch, Issued.AddMinutes(60), bytes));
        }
        var original = new byte[] { 1, 2, 3, 4 };
        var facts = new ServerSessionFacts(Key, Binding, 3, Epoch, Lifetime, 1, original);
        var intent = new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch, Issued.AddMinutes(60), original);
        original[0] = 99;
        facts.CopyProtectedTicket()[1] = 99;
        intent.CopyProtectedTicket()[2] = 99;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, facts.CopyProtectedTicket());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, intent.CopyProtectedTicket());
        var maximum = new ServerSessionFacts(Key, Binding, 3, Epoch, Lifetime, 1, new byte[16384]);
        Assert.Equal(16384, maximum.ProtectedTicketLength);
        Assert.Equal(16384, new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch,
            Issued.AddMinutes(60), new byte[16384]).ProtectedTicketLength);
    }

    [Fact]
    public void CreateIntentAndIdentityRequireExactNeutralExistingFacts()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerSessionCreateIntent(null!, Binding, 3, Principal, Epoch, Issued.AddMinutes(60), Ticket));
        Assert.Throws<ArgumentException>(() => new ServerSessionCreateIntent(Key, Guid.Empty, 3, Principal, Epoch, Issued.AddMinutes(60), Ticket));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionCreateIntent(Key, Binding, 0, Principal, Epoch, Issued.AddMinutes(60), Ticket));
        Assert.Throws<ArgumentNullException>(() => new ServerSessionCreateIntent(Key, Binding, 3, null!, Epoch, Issued.AddMinutes(60), Ticket));
        Assert.Throws<ArgumentException>(() => new ServerSessionCreateIntent(Key, Binding, 3, Principal, Guid.Empty, Issued.AddMinutes(60), Ticket));
        Assert.Throws<ArgumentException>(() => new ServerSessionIdentityObservation(Guid.Empty, 3, Principal, true, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionIdentityObservation(Binding, 0, Principal, true, true));
        Assert.Throws<ArgumentNullException>(() => new ServerSessionIdentityObservation(Binding, 3, null!, true, true));
        Assert.Equal(Principal, Intent().ExpectedPrincipalId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void UnknownPolicyOrCodecCannotBeMaterializedAsAnonymousState(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionFacts(Key, Binding, 3, Epoch, Lifetime, 1, Ticket, policyVersion: version));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionFacts(Key, Binding, 3, Epoch, Lifetime, 1, Ticket, codecVersion: version));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch, Issued.AddMinutes(60), Ticket, policyVersion: version));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch, Issued.AddMinutes(60), Ticket, codecVersion: version));
    }

    [Fact]
    public void DefaultDiagnosticsAndSerializationExposeNoPrivateFacts()
    {
        var facts = Facts();
        var identity = new ServerSessionIdentityObservation(Binding, 3, Principal, true, true);
        var observed = Assert.IsType<CurrentServerSession>(ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, identity));
        object[] privateFacts = [Key, Lifetime, facts, Intent(), identity, observed];
        foreach (var value in privateFacts)
        {
            Assert.True(string.Equals("{}", JsonSerializer.Serialize(value, value.GetType()), StringComparison.Ordinal),
                "A private session fact serialization changed.");
            Assert.True(string.Equals(value.GetType().Name, value.ToString(), StringComparison.Ordinal),
                "A private session fact diagnostic changed.");
        }
        var result = ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, facts);
        var serialized = JsonSerializer.Serialize(result);
        foreach (var privateText in new[] { Organization.ToString(), Binding.ToString(), Epoch.ToString(),
            Principal.Value.ToString(), Key.HandleDigest.Value, Convert.ToBase64String(Ticket), Encoding.UTF8.GetString(Ticket) })
        {
            Assert.False(serialized.Contains(privateText, StringComparison.Ordinal),
                "A session result serialization exposed private facts.");
            Assert.False(result.ToString().Contains(privateText, StringComparison.Ordinal),
                "A session result diagnostic exposed private facts.");
        }
        var failure = Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ObserveCurrent(
            new ServerSessionKey(Binding, Key.HandleDigest), Issued, facts, identity));
        Assert.False(failure.Message.Contains(Key.HandleDigest.Value, StringComparison.Ordinal),
            "A session failure diagnostic exposed a private digest.");
        Assert.False(failure.Message.Contains(Organization.ToString(), StringComparison.Ordinal),
            "A session failure diagnostic exposed a private organization.");
    }

    [Fact]
    public void StoreResultRequiresCoherentCommittedSuccessAndKeepsRefusalsEmpty()
    {
        foreach (var outcome in new[] { ServerSessionWriteOutcome.Conflict, ServerSessionWriteOutcome.Denied,
            ServerSessionWriteOutcome.VersionExhausted })
        {
            Assert.Null(ServerSessionWriteResult.WithoutFacts(outcome).Facts);
            Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(outcome, Facts()));
        }
        foreach (var outcome in new[] { ServerSessionWriteOutcome.Created, ServerSessionWriteOutcome.Renewed,
            ServerSessionWriteOutcome.Revoked })
            Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.WithoutFacts(outcome));
        Assert.Null(ServerSessionWriteResult.WithoutFacts(ServerSessionWriteOutcome.Unchanged).Facts);
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Renewed, Facts()));
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Revoked, Facts(2)));
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, Facts(2)));
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Renewed, Facts(2, Issued)));
        var noninitial = new ServerSessionFacts(Key, Binding, 3, Epoch,
            new(Issued, Issued.AddMinutes(10), Issued.AddMinutes(60)), 1, Ticket);
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, noninitial));
        Assert.Throws<ArgumentNullException>(() => ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, null!));
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.WithoutFacts((ServerSessionWriteOutcome)0));
        Assert.Throws<ArgumentException>(() => ServerSessionWriteResult.ForAcknowledgedFacts((ServerSessionWriteOutcome)999, Facts()));
        foreach (var (outcome, facts) in new[]
        {
            (ServerSessionWriteOutcome.Created, Facts()),
            (ServerSessionWriteOutcome.Renewed, Facts(2)),
            (ServerSessionWriteOutcome.Revoked, Facts(2, Issued)),
            (ServerSessionWriteOutcome.Unchanged, Facts(long.MaxValue, Issued))
        })
            Assert.Same(facts, ServerSessionWriteResult.ForAcknowledgedFacts(outcome, facts).Facts);
    }
}
