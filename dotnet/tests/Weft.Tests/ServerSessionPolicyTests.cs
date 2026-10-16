using System.Globalization;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// Fixed synthetic observations only. No SQL join/lock, concurrent commit, protected ticket,
/// handler, provider, process restart or cancellation propagation is qualified by these vectors.
/// </summary>
public sealed class ServerSessionPolicyTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Binding = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Epoch = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly PrincipalId Principal = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly byte[] Ticket = [1, 2, 3, 4];
    private static DateTimeOffset Utc(string time) => DateTimeOffset.Parse("2026-10-15T" + time + "Z", CultureInfo.InvariantCulture);
    private static DateTimeOffset Issued => Utc("12:00:00");
    private static ServerSessionKey Key => new(Organization, Sha256Digest.Parse(new string('a', 64)));
    private static ServerSessionFacts Facts(long version = 7, DateTimeOffset? revoked = null,
        ServerSessionLifetime? lifetime = null) =>
        new(Key, Binding, 3, Epoch, lifetime ?? new(Issued, Utc("12:20:00"), Utc("13:00:00")), version, Ticket, revoked);
    private static ServerSessionCreateIntent Intent(DateTimeOffset? verifiedDeadline = null) =>
        new(Key, Binding, 3, Principal, Epoch, verifiedDeadline ?? Utc("14:00:00"), Ticket);
    private static ServerSessionIdentityObservation Identity(long version = 3, bool enabled = true, bool active = true) =>
        new(Binding, version, Principal, enabled, active);

    [Theory]
    [InlineData("12:05:00", "12:05:00", "12:05:00")]
    [InlineData("12:20:00", "12:20:00", "12:20:00")]
    [InlineData("12:35:00", "12:20:00", "12:35:00")]
    [InlineData("13:00:00", "12:20:00", "13:00:00")]
    [InlineData("14:00:00", "12:20:00", "13:00:00")]
    public void InitialBoundsMatchFixedTwentySixtyAndVerifiedDeadlineVectors(string verified, string idle, string absolute)
    {
        var lifetime = Assert.IsType<ServerSessionLifetime>(ServerSessionPolicy.InitialLifetime(Issued, Utc(verified)));
        Assert.Equal(Issued, lifetime.IssuedAtUtc);
        Assert.Equal(Utc(idle), lifetime.IdleExpiresAtUtc);
        Assert.Equal(Utc(absolute), lifetime.AbsoluteExpiresAtUtc);
    }

    [Theory]
    [InlineData("11:59:59.999999")]
    [InlineData("12:00:00")]
    public void VerifiedDeadlineBeforeOrAtCreationCannotYieldLiveFacts(string verified)
    {
        Assert.Null(ServerSessionPolicy.InitialLifetime(Issued, Utc(verified)));
        foreach (var handleExists in new[] { false, true })
            Assert.Equal(ServerSessionWriteOutcome.Denied,
                ServerSessionPolicy.ClassifyCreate(Intent(Utc(verified)), Issued, true, handleExists, Identity()));
    }

    [Fact]
    public void OneMicrosecondPositiveLoginLifetimeIsNotRoundedOrExtended()
    {
        var deadline = Utc("12:00:00.000001");
        var lifetime = Assert.IsType<ServerSessionLifetime>(ServerSessionPolicy.InitialLifetime(Issued, deadline));
        Assert.Equal(deadline, lifetime.IdleExpiresAtUtc);
        Assert.Equal(deadline, lifetime.AbsoluteExpiresAtUtc);
        var facts = Facts(lifetime: lifetime);
        Assert.True(ServerSessionPolicy.IsLiveAt(facts, Issued));
        Assert.False(ServerSessionPolicy.IsLiveAt(facts, deadline));
    }

    [Fact]
    public void CreationAndRenewalArithmeticOverflowFailsRatherThanSaturating()
    {
        var nearMaximum = new DateTimeOffset(9999, 12, 31, 23, 30, 0, TimeSpan.Zero);
        var deadline = new DateTimeOffset(9999, 12, 31, 23, 59, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerSessionPolicy.InitialLifetime(nearMaximum, deadline));
        var intent = new ServerSessionCreateIntent(Key, Binding, 3, Principal, Epoch, deadline, Ticket);
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerSessionPolicy.ClassifyCreate(intent,
            nearMaximum, true, false, Identity()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerSessionPolicy.ClassifyCreate(intent,
            nearMaximum, true, true, Identity()));
        var future = new ServerSessionFacts(Key, Binding, 3, Epoch, new(
            new(9999, 12, 31, 23, 0, 0, TimeSpan.Zero),
            new(9999, 12, 31, 23, 50, 0, TimeSpan.Zero), deadline), 7, Ticket);
        var now = new DateTimeOffset(9999, 12, 31, 23, 49, 0, TimeSpan.Zero);
        Assert.True(ServerSessionPolicy.IsLiveAt(future, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerSessionPolicy.RenewalIdleExpiresAt(future, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerSessionPolicy.ClassifyRenew(Key, 7, now, future, Identity()));
    }

    [Theory]
    [InlineData("11:59:59.999999", false)]
    [InlineData("12:00:00", true)]
    [InlineData("12:19:59.999999", true)]
    [InlineData("12:20:00", false)]
    [InlineData("12:20:00.000001", false)]
    public void IdleAdmissionUsesExactBeforeAtAndAfterBoundaries(string now, bool expected)
    {
        Assert.Equal(expected, ServerSessionPolicy.IsLiveAt(Facts(), Utc(now)));
    }

    [Theory]
    [InlineData("12:04:59.999999", true)]
    [InlineData("12:05:00", false)]
    [InlineData("12:05:00.000001", false)]
    public void ClippedAbsoluteDeadlineHasNoLeeway(string now, bool expected)
    {
        var clipped = Facts(lifetime: new(Issued, Utc("12:05:00"), Utc("12:05:00")));
        Assert.Equal(expected, ServerSessionPolicy.IsLiveAt(clipped, Utc(now)));
    }

    [Fact]
    public void EachObservationRequiresCurrentEnabledBindingVersionAndActiveOwner()
    {
        var facts = Facts();
        var first = Assert.IsType<CurrentServerSession>(ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, Identity()));
        Assert.Same(facts, first.Facts);
        Assert.Equal(Principal, first.PrincipalId);
        Assert.Null(ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, Identity(enabled: false)));
        Assert.Null(ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, Identity(active: false)));
        Assert.Null(ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, Identity(version: 4)));
        Assert.Null(ServerSessionPolicy.ObserveCurrent(Key, Issued, null, null));
        // Prior positive metadata does not replace the next required fresh observation.
        Assert.Same(facts, first.Facts);
        Assert.Equal(Epoch, first.Facts.LoginEpoch);
    }

    [Fact]
    public void ForeignScopeHandleBindingOrBrokenReferenceIsFailureRatherThanAnonymousState()
    {
        var facts = Facts();
        foreach (var key in new[] { new ServerSessionKey(Binding, Key.HandleDigest),
            new ServerSessionKey(Organization, Sha256Digest.Parse(new string('b', 64))) })
        {
            Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ObserveCurrent(key, Issued, facts, Identity()));
            Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ClassifyRevoke(key, Issued, facts));
        }
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, null));
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ObserveCurrent(Key, Issued, null, Identity()));
        var foreignBinding = new ServerSessionIdentityObservation(Epoch, 3, Principal, true, true);
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ObserveCurrent(Key, Issued, facts, foreignBinding));
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ClassifyCreate(Intent(), Issued, true, false, foreignBinding));
        var foreignOwner = new ServerSessionIdentityObservation(Binding, 3, new PrincipalId(Epoch), true, true);
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ClassifyCreate(Intent(), Issued, true, false, foreignOwner));
    }

    [Fact]
    public void CreateRequiresFreshExistingOrganizationAndExactCurrentIdentityWithoutGrantInference()
    {
        var intent = Intent();
        Assert.Equal(ServerSessionWriteOutcome.Created, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, false, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyCreate(intent, Issued, false, false, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, false, null));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, false, Identity(enabled: false)));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, false, Identity(active: false)));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, false, Identity(version: 4)));
        // No organization admission, project role or payload grant is accepted or returned here.
        Assert.Equal(Principal, intent.ExpectedPrincipalId);
    }

    [Fact]
    public void CreateOnlyNeverUpsertsOrReplaysAnExistingDigest()
    {
        var intent = Intent();
        Assert.Equal(ServerSessionWriteOutcome.Conflict, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, true, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Conflict, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, true, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyCreate(intent, Issued, true, true, Identity(active: false)));
        // The same global digest also conflicts for a different valid organization namespace.
        var foreignNamespace = new ServerSessionCreateIntent(new ServerSessionKey(Epoch, Key.HandleDigest),
            Binding, 3, Principal, Epoch, intent.VerifiedLoginDeadlineUtc, Ticket);
        Assert.Equal(ServerSessionWriteOutcome.Conflict,
            ServerSessionPolicy.ClassifyCreate(foreignNamespace, Issued, true, true, Identity()));
        Assert.Equal(Epoch, intent.LoginEpoch);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, intent.CopyProtectedTicket());
    }

    [Fact]
    public void RenewalExtendsOnlyIdleAndClipsAtTheOriginalAbsoluteDeadline()
    {
        var facts = Facts();
        Assert.Equal(Utc("12:25:00"), ServerSessionPolicy.RenewalIdleExpiresAt(facts, Utc("12:05:00")));
        Assert.Equal(ServerSessionWriteOutcome.Renewed, ServerSessionPolicy.ClassifyRenew(Key, 7, Utc("12:05:00"), facts, Identity()));
        var nearAbsolute = Facts(lifetime: new(Issued, Utc("12:50:00"), Utc("13:00:00")));
        Assert.Equal(Utc("13:00:00"), ServerSessionPolicy.RenewalIdleExpiresAt(nearAbsolute, Utc("12:45:00")));
        var capped = Facts(lifetime: new(Issued, Utc("13:00:00"), Utc("13:00:00")));
        Assert.Equal(ServerSessionWriteOutcome.Unchanged, ServerSessionPolicy.ClassifyRenew(Key, 7, Utc("12:50:00"), capped, Identity()));
        Assert.Equal(Utc("12:20:00"), facts.Lifetime.IdleExpiresAtUtc);
        Assert.Equal(Issued, facts.Lifetime.IssuedAtUtc);
        Assert.Equal(Utc("13:00:00"), facts.Lifetime.AbsoluteExpiresAtUtc);
        Assert.Equal(7, facts.Version);
        Assert.Equal(Epoch, facts.LoginEpoch);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, facts.CopyProtectedTicket());
    }

    [Fact]
    public void RegressedButStillLiveClockDoesNotMoveIdleBackwardOrSpendAVersion()
    {
        var facts = Facts(lifetime: new(Issued, Utc("12:40:00"), Utc("13:00:00")));
        Assert.Equal(Utc("12:40:00"), ServerSessionPolicy.RenewalIdleExpiresAt(facts, Utc("12:10:00")));
        Assert.Equal(ServerSessionWriteOutcome.Unchanged,
            ServerSessionPolicy.ClassifyRenew(Key, 7, Utc("12:10:00"), facts, Identity()));
        Assert.False(ServerSessionPolicy.IsLiveAt(facts, Utc("11:59:59.999999")));
    }

    [Fact]
    public void PositiveExactCasIsRequiredEvenForUnchangedRenewal()
    {
        foreach (var expected in new[] { 1L, 6, 8, long.MaxValue })
            Assert.Equal(ServerSessionWriteOutcome.Conflict,
                ServerSessionPolicy.ClassifyRenew(Key, expected, Issued, Facts(), Identity()));
        foreach (var expected in new[] { long.MinValue, -1, 0 })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ServerSessionPolicy.ClassifyRenew(Key, expected, Issued, Facts(), Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Unchanged,
            ServerSessionPolicy.ClassifyRenew(Key, 7, Issued, Facts(), Identity()));
    }

    [Fact]
    public void MissingExpiredRevokedOrChangedIdentityCannotBeRenewedIntoALiveSession()
    {
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyRenew(Key, 7, Issued, null, null));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyRenew(Key, 7, Utc("12:20:00"), Facts(), Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyRenew(Key, 7, Issued, Facts(revoked: Issued), Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyRenew(Key, 7, Issued, Facts(), Identity(enabled: false)));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyRenew(Key, 7, Issued, Facts(), Identity(active: false)));
        Assert.Equal(ServerSessionWriteOutcome.Denied, ServerSessionPolicy.ClassifyRenew(Key, 7, Issued, Facts(), Identity(version: 4)));
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.RenewalIdleExpiresAt(Facts(), Utc("12:20:00")));
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.RenewalIdleExpiresAt(Facts(revoked: Issued), Issued));
    }

    [Fact]
    public void MaximumVersionAllowsNoopButRefusesGenuineRenewalAndRevocation()
    {
        var maximum = Facts(long.MaxValue);
        Assert.Equal(ServerSessionWriteOutcome.Unchanged, ServerSessionPolicy.ClassifyRenew(Key, long.MaxValue, Issued, maximum, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.VersionExhausted, ServerSessionPolicy.ClassifyRenew(Key, long.MaxValue, Utc("12:01:00"), maximum, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Conflict, ServerSessionPolicy.ClassifyRenew(Key, long.MaxValue - 1, Issued, maximum, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.VersionExhausted, ServerSessionPolicy.ClassifyRevoke(Key, Issued, maximum));
        Assert.Equal(ServerSessionWriteOutcome.Unchanged, ServerSessionPolicy.ClassifyRevoke(Key, Issued, Facts(long.MaxValue, Issued)));
        Assert.Equal(long.MaxValue, maximum.Version);
        Assert.Null(maximum.RevokedAtUtc);
    }

    [Fact]
    public void RevokeIsExactIdempotentAndDoesNotRequireALiveParentOrSession()
    {
        Assert.Equal(ServerSessionWriteOutcome.Unchanged, ServerSessionPolicy.ClassifyRevoke(Key, Issued, null));
        Assert.Equal(ServerSessionWriteOutcome.Revoked, ServerSessionPolicy.ClassifyRevoke(Key, Issued, Facts()));
        Assert.Equal(ServerSessionWriteOutcome.Revoked, ServerSessionPolicy.ClassifyRevoke(Key, Utc("13:01:00"), Facts()));
        Assert.Equal(ServerSessionWriteOutcome.Unchanged, ServerSessionPolicy.ClassifyRevoke(Key, Utc("13:01:00"), Facts(8, Utc("12:01:00"))));
        Assert.Throws<InvalidOperationException>(() => ServerSessionPolicy.ClassifyRevoke(Key, Utc("11:59:59.999999"), Facts()));
    }

    [Fact]
    public void FreshRevocationDefeatsAnEarlierSyntheticRenewalObservationWithoutChangingPastFacts()
    {
        var before = Facts();
        var prior = Assert.IsType<CurrentServerSession>(ServerSessionPolicy.ObserveCurrent(Key, Issued, before, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Renewed,
            ServerSessionPolicy.ClassifyRenew(Key, 7, Utc("12:01:00"), before, Identity()));
        var revoked = Facts(8, Utc("12:01:00"));
        Assert.Equal(ServerSessionWriteOutcome.Denied,
            ServerSessionPolicy.ClassifyRenew(Key, 7, Utc("12:02:00"), revoked, Identity()));
        Assert.Null(ServerSessionPolicy.ObserveCurrent(Key, Utc("12:02:00"), revoked, Identity()));
        Assert.Equal(ServerSessionWriteOutcome.Unchanged, ServerSessionPolicy.ClassifyRevoke(Key, Utc("12:02:00"), revoked));
        Assert.Same(before, prior.Facts);
        Assert.Null(before.RevokedAtUtc);
        Assert.Equal(7, before.Version);
    }

    [Fact]
    public void RequiredArgumentsRemainFailuresRatherThanDeniedObservations()
    {
        Assert.Throws<ArgumentNullException>(() => ServerSessionPolicy.IsLiveAt(null!, Issued));
        Assert.Throws<ArgumentNullException>(() => ServerSessionPolicy.ObserveCurrent(null!, Issued, null, null));
        Assert.Throws<ArgumentNullException>(() => ServerSessionPolicy.ClassifyCreate(null!, Issued, true, false, Identity()));
        Assert.Throws<ArgumentNullException>(() => ServerSessionPolicy.RenewalIdleExpiresAt(null!, Issued));
        Assert.Throws<ArgumentNullException>(() => ServerSessionPolicy.ClassifyRenew(null!, 1, Issued, null, null));
        Assert.Throws<ArgumentNullException>(() => ServerSessionPolicy.ClassifyRevoke(null!, Issued, null));
    }
}
