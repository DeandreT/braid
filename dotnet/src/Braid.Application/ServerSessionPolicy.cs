namespace Braid.Application;

/// <summary>
/// Pure prospective metadata decisions only: no ambient clock, storage, commit, identity
/// authentication or authority for later work. Current store facts must share one statement.
/// </summary>
public static class ServerSessionPolicy
{
    public const int PolicyVersion = 1;
    public const int CodecVersion = 1;
    public const int MaximumProtectedTicketBytes = 16384;
    public static TimeSpan IdleWindow { get; } = TimeSpan.FromMinutes(20);
    public static TimeSpan AbsoluteWindow { get; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Computes the fixed creation bounds from an explicit database-time sample and trusted
    /// verified-login deadline. Expired deadline returns null; arithmetic overflow is a failure.
    /// </summary>
    public static ServerSessionLifetime? InitialLifetime(DateTimeOffset observedUtcNow,
        DateTimeOffset verifiedLoginDeadlineUtc)
    {
        ServerSessionValidation.RequireInstant(observedUtcNow, nameof(observedUtcNow));
        ServerSessionValidation.RequireInstant(verifiedLoginDeadlineUtc, nameof(verifiedLoginDeadlineUtc));
        if (verifiedLoginDeadlineUtc <= observedUtcNow)
            return null;
        var absolute = Earlier(observedUtcNow.Add(AbsoluteWindow), verifiedLoginDeadlineUtc);
        var idle = Earlier(observedUtcNow.Add(IdleWindow), absolute);
        return new(observedUtcNow, idle, absolute);
    }

    /// <summary>Strict expiry equality, with no grace period or clock-skew extension.</summary>
    public static bool IsLiveAt(ServerSessionFacts facts, DateTimeOffset observedUtcNow)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ServerSessionValidation.RequireInstant(observedUtcNow, nameof(observedUtcNow));
        return facts.RevokedAtUtc is null && facts.Lifetime.IssuedAtUtc <= observedUtcNow &&
            observedUtcNow < facts.Lifetime.IdleExpiresAtUtc &&
            observedUtcNow < facts.Lifetime.AbsoluteExpiresAtUtc;
    }

    /// <summary>
    /// Classifies one fresh store observation, never an old access result plus a later lookup.
    /// A missing row returns null. A returned foreign key or broken parent reference is an
    /// integrity failure; disabled/inactive/version-mismatched/expired observations return null.
    /// Binding owner comes from the current binding, not a duplicated session principal column.
    /// </summary>
    public static CurrentServerSession? ObserveCurrent(ServerSessionKey key, DateTimeOffset observedUtcNow,
        ServerSessionFacts? stored, ServerSessionIdentityObservation? identity)
    {
        ArgumentNullException.ThrowIfNull(key);
        ServerSessionValidation.RequireInstant(observedUtcNow, nameof(observedUtcNow));
        if (stored is null)
        {
            if (identity is not null)
                throw InconsistentObservation();
            return null;
        }
        RequireKey(key, stored);
        if (identity is null || identity.BindingId != stored.BindingId)
            throw InconsistentObservation();
        return identity.BindingEnabled && identity.PrincipalActive &&
            identity.BindingVersion == stored.BindingVersion && IsLiveAt(stored, observedUtcNow)
            ? new(stored, identity.PrincipalId) : null;
    }

    /// <summary>
    /// Prospective create-only decision, not acknowledged creation. Returned identity integrity
    /// is checked first, then fresh organization/identity admission and initial lifetime. Expired
    /// deadlines deny and arithmetic overflow fails before any collision classification. Only a
    /// live admitted candidate conflicts with an existing digest across any organization, without
    /// exposing the collided row. Organization admission is separate and is never inferred.
    /// </summary>
    public static ServerSessionWriteOutcome ClassifyCreate(ServerSessionCreateIntent intent,
        DateTimeOffset observedUtcNow, bool organizationExists, bool handleExists,
        ServerSessionIdentityObservation? identity)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ServerSessionValidation.RequireInstant(observedUtcNow, nameof(observedUtcNow));
        if (identity is not null && (identity.BindingId != intent.BindingId ||
            identity.PrincipalId != intent.ExpectedPrincipalId))
            throw InconsistentObservation();
        if (!organizationExists || identity is null || !identity.BindingEnabled || !identity.PrincipalActive ||
            identity.BindingVersion != intent.BindingVersion)
            return ServerSessionWriteOutcome.Denied;
        if (InitialLifetime(observedUtcNow, intent.VerifiedLoginDeadlineUtc) is null)
            return ServerSessionWriteOutcome.Denied;
        return handleExists ? ServerSessionWriteOutcome.Conflict : ServerSessionWriteOutcome.Created;
    }

    /// <summary>
    /// Returns a prospective idle deadline only for a live row, retaining issue/absolute bounds.
    /// A regressed-but-still-live statement clock cannot move the existing idle deadline backward.
    /// Nothing updates a row, epoch, binding, ticket or version here.
    /// </summary>
    public static DateTimeOffset RenewalIdleExpiresAt(ServerSessionFacts facts, DateTimeOffset observedUtcNow)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!IsLiveAt(facts, observedUtcNow))
            throw new InvalidOperationException("Only a live session has a prospective renewal deadline.");
        var proposed = Earlier(observedUtcNow.Add(IdleWindow), facts.Lifetime.AbsoluteExpiresAtUtc);
        return proposed > facts.Lifetime.IdleExpiresAtUtc ? proposed : facts.Lifetime.IdleExpiresAtUtc;
    }

    /// <summary>
    /// Prospective renewal decision after a fresh live observation. Expected version is positive;
    /// stale CAS conflicts before same-state replay. Only a genuine change consumes a version.
    /// </summary>
    public static ServerSessionWriteOutcome ClassifyRenew(ServerSessionKey key, long expectedVersion,
        DateTimeOffset observedUtcNow, ServerSessionFacts? stored, ServerSessionIdentityObservation? identity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        if (ObserveCurrent(key, observedUtcNow, stored, identity) is not { } current)
            return ServerSessionWriteOutcome.Denied;
        if (current.Facts.Version != expectedVersion)
            return ServerSessionWriteOutcome.Conflict;
        if (RenewalIdleExpiresAt(current.Facts, observedUtcNow) == current.Facts.Lifetime.IdleExpiresAtUtc)
            return ServerSessionWriteOutcome.Unchanged;
        return current.Facts.Version == long.MaxValue ? ServerSessionWriteOutcome.VersionExhausted
            : ServerSessionWriteOutcome.Renewed;
    }

    /// <summary>
    /// Prospective one-way revocation, independent of current parent activity or expiry. A missing
    /// or already revoked exact row is unchanged; a genuine change advances once unless exhausted.
    /// </summary>
    public static ServerSessionWriteOutcome ClassifyRevoke(ServerSessionKey key, DateTimeOffset observedUtcNow,
        ServerSessionFacts? stored)
    {
        ArgumentNullException.ThrowIfNull(key);
        ServerSessionValidation.RequireInstant(observedUtcNow, nameof(observedUtcNow));
        if (stored is null)
            return ServerSessionWriteOutcome.Unchanged;
        RequireKey(key, stored);
        if (stored.RevokedAtUtc is not null)
            return ServerSessionWriteOutcome.Unchanged;
        if (observedUtcNow < stored.Lifetime.IssuedAtUtc)
            throw new InvalidOperationException("The observed revocation clock precedes session issuance.");
        return stored.Version == long.MaxValue ? ServerSessionWriteOutcome.VersionExhausted
            : ServerSessionWriteOutcome.Revoked;
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;

    private static void RequireKey(ServerSessionKey key, ServerSessionFacts stored)
    {
        if (key != stored.Key)
            throw InconsistentObservation();
    }

    private static InvalidOperationException InconsistentObservation() =>
        new("The current session observation has inconsistent ownership or references.");
}
