using System.Text.Json.Serialization;
using Braid.Domain;

namespace Braid.Application;

/// <summary>A private exact lookup key. Organization comes from trusted server configuration.</summary>
public sealed record ServerSessionKey
{
    public ServerSessionKey(Guid organizationId, Sha256Digest handleDigest)
    {
        ServerSessionValidation.RequireId(organizationId, nameof(organizationId));
        ArgumentNullException.ThrowIfNull(handleDigest);
        OrganizationId = organizationId;
        HandleDigest = handleDigest;
    }

    [JsonIgnore]
    public Guid OrganizationId { get; }
    [JsonIgnore]
    public Sha256Digest HandleDigest { get; }
    public override string ToString() => nameof(ServerSessionKey);
}

/// <summary>Finite UTC metadata bounds, not a clock, lease or authenticated identity.</summary>
public sealed record ServerSessionLifetime
{
    public ServerSessionLifetime(DateTimeOffset issuedAtUtc, DateTimeOffset idleExpiresAtUtc,
        DateTimeOffset absoluteExpiresAtUtc)
    {
        ServerSessionValidation.RequireInstant(issuedAtUtc, nameof(issuedAtUtc));
        ServerSessionValidation.RequireInstant(idleExpiresAtUtc, nameof(idleExpiresAtUtc));
        ServerSessionValidation.RequireInstant(absoluteExpiresAtUtc, nameof(absoluteExpiresAtUtc));
        if (issuedAtUtc >= idleExpiresAtUtc || idleExpiresAtUtc > absoluteExpiresAtUtc ||
            absoluteExpiresAtUtc - issuedAtUtc > ServerSessionPolicy.AbsoluteWindow)
            throw new ArgumentException("Session lifetime bounds are inconsistent with the fixed policy.");
        IssuedAtUtc = issuedAtUtc;
        IdleExpiresAtUtc = idleExpiresAtUtc;
        AbsoluteExpiresAtUtc = absoluteExpiresAtUtc;
    }

    [JsonIgnore]
    public DateTimeOffset IssuedAtUtc { get; }
    [JsonIgnore]
    public DateTimeOffset IdleExpiresAtUtc { get; }
    [JsonIgnore]
    public DateTimeOffset AbsoluteExpiresAtUtc { get; }
    public override string ToString() => nameof(ServerSessionLifetime);
}

/// <summary>
/// One immutable snapshot of durable session metadata. No principal owner is duplicated here;
/// current binding ownership is a separate fresh store observation. Ticket bytes are opaque,
/// copied on input/output, and do not establish protection or identity until the later codec.
/// </summary>
public sealed class ServerSessionFacts
{
    private readonly byte[] protectedTicket;

    public ServerSessionFacts(ServerSessionKey key, Guid bindingId, long bindingVersion, Guid loginEpoch,
        ServerSessionLifetime lifetime, long version, ReadOnlyMemory<byte> protectedTicket,
        DateTimeOffset? revokedAtUtc = null, int policyVersion = ServerSessionPolicy.PolicyVersion,
        int codecVersion = ServerSessionPolicy.CodecVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        ServerSessionValidation.RequireId(bindingId, nameof(bindingId));
        ArgumentOutOfRangeException.ThrowIfLessThan(bindingVersion, 1);
        ServerSessionValidation.RequireId(loginEpoch, nameof(loginEpoch));
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ServerSessionValidation.RequireVersions(policyVersion, codecVersion);
        if (revokedAtUtc is { } revoked)
        {
            ServerSessionValidation.RequireInstant(revoked, nameof(revokedAtUtc));
            if (revoked < lifetime.IssuedAtUtc || version < 2)
                throw new ArgumentException("Session revocation history is inconsistent.", nameof(revokedAtUtc));
        }
        this.protectedTicket = ServerSessionValidation.CopyTicket(protectedTicket.Span);
        Key = key;
        BindingId = bindingId;
        BindingVersion = bindingVersion;
        LoginEpoch = loginEpoch;
        Lifetime = lifetime;
        Version = version;
        RevokedAtUtc = revokedAtUtc;
        PolicyVersion = policyVersion;
        CodecVersion = codecVersion;
    }

    [JsonIgnore]
    public ServerSessionKey Key { get; }
    [JsonIgnore]
    public Guid BindingId { get; }
    [JsonIgnore]
    public long BindingVersion { get; }
    [JsonIgnore]
    public Guid LoginEpoch { get; }
    [JsonIgnore]
    public ServerSessionLifetime Lifetime { get; }
    [JsonIgnore]
    public long Version { get; }
    [JsonIgnore]
    public DateTimeOffset? RevokedAtUtc { get; }
    [JsonIgnore]
    public int PolicyVersion { get; }
    [JsonIgnore]
    public int CodecVersion { get; }
    [JsonIgnore]
    public int ProtectedTicketLength => protectedTicket.Length;
    public byte[] CopyProtectedTicket() => (byte[])protectedTicket.Clone();
    public override string ToString() => nameof(ServerSessionFacts);
}

/// <summary>
/// Trusted metadata intent only. A later issuance boundary supplies a fresh random handle digest
/// and independent epoch after verified login; this contract neither generates nor accepts handles.
/// ExpectedPrincipalId is a fresh consistency check, not duplicated persisted ownership or a grant.
/// </summary>
public sealed class ServerSessionCreateIntent
{
    private readonly byte[] protectedTicket;

    public ServerSessionCreateIntent(ServerSessionKey key, Guid bindingId, long bindingVersion,
        PrincipalId expectedPrincipalId, Guid loginEpoch, DateTimeOffset verifiedLoginDeadlineUtc,
        ReadOnlyMemory<byte> protectedTicket, int policyVersion = ServerSessionPolicy.PolicyVersion,
        int codecVersion = ServerSessionPolicy.CodecVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        ServerSessionValidation.RequireId(bindingId, nameof(bindingId));
        ArgumentOutOfRangeException.ThrowIfLessThan(bindingVersion, 1);
        ArgumentNullException.ThrowIfNull(expectedPrincipalId);
        ServerSessionValidation.RequireId(loginEpoch, nameof(loginEpoch));
        ServerSessionValidation.RequireInstant(verifiedLoginDeadlineUtc, nameof(verifiedLoginDeadlineUtc));
        ServerSessionValidation.RequireVersions(policyVersion, codecVersion);
        this.protectedTicket = ServerSessionValidation.CopyTicket(protectedTicket.Span);
        Key = key;
        BindingId = bindingId;
        BindingVersion = bindingVersion;
        ExpectedPrincipalId = expectedPrincipalId;
        LoginEpoch = loginEpoch;
        VerifiedLoginDeadlineUtc = verifiedLoginDeadlineUtc;
        PolicyVersion = policyVersion;
        CodecVersion = codecVersion;
    }

    [JsonIgnore]
    public ServerSessionKey Key { get; }
    [JsonIgnore]
    public Guid BindingId { get; }
    [JsonIgnore]
    public long BindingVersion { get; }
    [JsonIgnore]
    public PrincipalId ExpectedPrincipalId { get; }
    [JsonIgnore]
    public Guid LoginEpoch { get; }
    [JsonIgnore]
    public DateTimeOffset VerifiedLoginDeadlineUtc { get; }
    [JsonIgnore]
    public int PolicyVersion { get; }
    [JsonIgnore]
    public int CodecVersion { get; }
    [JsonIgnore]
    public int ProtectedTicketLength => protectedTicket.Length;
    public byte[] CopyProtectedTicket() => (byte[])protectedTicket.Clone();
    public override string ToString() => nameof(ServerSessionCreateIntent);
}

/// <summary>
/// Neutral synthetic/current-store facts. The store must obtain binding/version/enabled and its
/// existing principal owner/activity together; null represents absence, never a broken reference.
/// No provider fields, roles, membership, cookie validation or ActorContext conversion live here.
/// </summary>
public sealed class ServerSessionIdentityObservation
{
    public ServerSessionIdentityObservation(Guid bindingId, long bindingVersion, PrincipalId principalId,
        bool bindingEnabled, bool principalActive)
    {
        ServerSessionValidation.RequireId(bindingId, nameof(bindingId));
        ArgumentOutOfRangeException.ThrowIfLessThan(bindingVersion, 1);
        ArgumentNullException.ThrowIfNull(principalId);
        BindingId = bindingId;
        BindingVersion = bindingVersion;
        PrincipalId = principalId;
        BindingEnabled = bindingEnabled;
        PrincipalActive = principalActive;
    }

    [JsonIgnore]
    public Guid BindingId { get; }
    [JsonIgnore]
    public long BindingVersion { get; }
    [JsonIgnore]
    public PrincipalId PrincipalId { get; }
    [JsonIgnore]
    public bool BindingEnabled { get; }
    [JsonIgnore]
    public bool PrincipalActive { get; }
    public override string ToString() => nameof(ServerSessionIdentityObservation);
}

/// <summary>Private live store observation only; not cookie authentication or later authority.</summary>
public sealed class CurrentServerSession
{
    internal CurrentServerSession(ServerSessionFacts facts, PrincipalId principalId)
    {
        Facts = facts;
        PrincipalId = principalId;
    }

    [JsonIgnore]
    public ServerSessionFacts Facts { get; }
    [JsonIgnore]
    public PrincipalId PrincipalId { get; }
    public override string ToString() => nameof(CurrentServerSession);
}

internal static class ServerSessionValidation
{
    internal static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A session identifier cannot be empty.", parameterName);
    }

    internal static void RequireInstant(DateTimeOffset value, string parameterName)
    {
        // Reserve the endpoint sentinels used by database infinity conversions. The DAL must
        // additionally check PostgreSQL isfinite before materializing any timestamp.
        if (value.Offset != TimeSpan.Zero || value.Ticks % 10 != 0 ||
            value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue)
            throw new ArgumentException("A session instant must be finite UTC at microsecond precision.", parameterName);
    }

    internal static void RequireVersions(int policyVersion, int codecVersion)
    {
        if (policyVersion != ServerSessionPolicy.PolicyVersion)
            throw new ArgumentOutOfRangeException(nameof(policyVersion), "The session policy version is unsupported.");
        if (codecVersion != ServerSessionPolicy.CodecVersion)
            throw new ArgumentOutOfRangeException(nameof(codecVersion), "The session codec version is unsupported.");
    }

    internal static byte[] CopyTicket(ReadOnlySpan<byte> protectedTicket)
    {
        if (protectedTicket.IsEmpty || protectedTicket.Length > ServerSessionPolicy.MaximumProtectedTicketBytes)
            throw new ArgumentException("The protected session ticket exceeds its accepted bounds.", nameof(protectedTicket));
        return protectedTicket.ToArray();
    }
}
