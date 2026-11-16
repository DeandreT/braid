using System.Text.Json.Serialization;

namespace Braid.Application;

public enum ServerSessionWriteOutcome
{
    Created = 1,
    Renewed = 2,
    Revoked = 3,
    Unchanged = 4,
    Conflict = 5,
    Denied = 6,
    VersionExhausted = 7
}

/// <summary>
/// Store acknowledgement only. Policy classification using this enum is prospective and must
/// not be fabricated into a committed result. A result never authenticates a request or grants
/// later authority; SQL/storage/integrity/cancellation/ambiguous commit failures are exceptions.
/// </summary>
public sealed class ServerSessionWriteResult
{
    private ServerSessionWriteResult(ServerSessionWriteOutcome outcome, ServerSessionFacts? facts)
    {
        Outcome = outcome;
        Facts = facts;
    }

    public ServerSessionWriteOutcome Outcome { get; }
    [JsonIgnore]
    public ServerSessionFacts? Facts { get; }
    public override string ToString() => nameof(ServerSessionWriteResult);

    public static ServerSessionWriteResult ForAcknowledgedFacts(ServerSessionWriteOutcome outcome,
        ServerSessionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var coherent = outcome switch
        {
            ServerSessionWriteOutcome.Created => facts.Version == 1 && facts.RevokedAtUtc is null &&
                facts.Lifetime == ServerSessionPolicy.InitialLifetime(facts.Lifetime.IssuedAtUtc,
                    facts.Lifetime.AbsoluteExpiresAtUtc),
            ServerSessionWriteOutcome.Renewed => facts.Version >= 2 && facts.RevokedAtUtc is null,
            ServerSessionWriteOutcome.Revoked => facts.Version >= 2 && facts.RevokedAtUtc is not null,
            ServerSessionWriteOutcome.Unchanged => true,
            _ => false
        };
        if (!coherent)
            throw new ArgumentException("Only coherent acknowledged session facts may accompany success.", nameof(outcome));
        return new(outcome, facts);
    }

    public static ServerSessionWriteResult WithoutFacts(ServerSessionWriteOutcome outcome)
    {
        if (outcome is not (ServerSessionWriteOutcome.Unchanged or ServerSessionWriteOutcome.Conflict or
            ServerSessionWriteOutcome.Denied or ServerSessionWriteOutcome.VersionExhausted))
            throw new ArgumentException("Only an absent no-op or closed refusal may omit session facts.", nameof(outcome));
        return new(outcome, null);
    }
}

/// <summary>
/// Separate restricted store reader, never cookie authentication or an identity/grant cache.
/// Each call leases an organization-bound READ ONLY transaction with bounded timeouts/reset and
/// observes exact configured organization/handle, finite session deadlines/revocation, matching
/// current enabled binding/version and its active owner in one fresh SQL statement. Check finite
/// source timestamps explicitly before materializing .NET values and verify every returned key.
/// Missing/expired/revoked/inactive/version-mismatched returns null; malformed schema/facts or
/// broken references, unavailable storage and cancellation remain exceptions, never anonymous success.
/// Positive facts are private metadata; the later protected-ticket boundary must verify their owner.
/// Subsequent sensitive operations recheck their own current authority and explicit payload grant.
/// </summary>
public interface IServerSessionReader
{
    /// <summary>
    /// Reader profile gets only required session columns, binding(binding_id,principal_id,enabled,version)
    /// and principal(id,active) SELECT under trusted configured-organization RLS. No provider keys,
    /// memberships, artifacts, parent writes, owner/membership powers, schema creation, broad helper
    /// EXECUTE or journal rights. No provider, HTTP, filesystem or other I/O spans the transaction.
    /// </summary>
    Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dedicated restricted session writer; never a public mint route or migration-owner runtime pool.
/// Adds only session INSERT and mutable idle/version/revocation UPDATE to required reader rights,
/// plus bounded organizations(id) SELECT solely for configured-organization existence at creation.
/// That read grants no organization admission. No parent/grant writes, DELETE/TRUNCATE,
/// schema/database CREATE, bypass, role membership or journal writes. All operations use exact
/// configured-organization scope, bounded transactions and reset.
/// Source-owned schema/ACL admission must preserve the narrower old profiles and P4a binding guards.
/// No transaction spans provider/HTTP/filesystem work. Return success only after acknowledged COMMIT;
/// storage/integrity/cancellation/ambiguous commit remain errors, with no automatic retry or cache.
/// </summary>
public interface IServerSessionWriter
{
    /// <summary>
    /// One fresh bounded admission-and-INSERT statement checks existing configured organization,
    /// exact enabled binding/version, expected immutable owner and active principal, then validates
    /// database-time initial lifetime before global digest arbitration. Returned wrong binding/owner
    /// or broken references fail integrity before ordinary denial. Initially expired candidates deny
    /// without a digest probe or provisional insert; arithmetic overflow fails before arbitration.
    /// Only an admitted candidate reaches insertion; a committed existing digest, including another
    /// organization or identical retry, yields Conflict without adoption, replay or collided facts.
    /// After every insertion, close its result and use one new exact-key final statement to observe
    /// the complete original version1/unrevoked row, current binding/owner/version and active principal
    /// with one finite database-time sample. Missing/mismatched issued facts or wrong expected owner,
    /// binding or references fail integrity before ordinary current identity/liveness denial.
    /// Only a still-live admitted final observation permits Created, including after a competing
    /// insertion rolls back. Keep the immutable lifetime anchored to the initial statement sample;
    /// do not renew or extend it from the later observation. A creation savepoint established after
    /// trusted transaction preparation encloses the insertion. Current expiry/identity denial undoes
    /// only this uncommitted insertion through rollback-to-savepoint and release, then returns Denied
    /// only after acknowledged outer COMMIT. Undo/cancellation/storage/integrity or failed/ambiguous
    /// acknowledgement remain exceptions, with no automatic retry. No parent commit fence, deletion
    /// authority, upsert, reassignment or replay is added; organization admission is never inferred.
    /// Trusted later issuance supplies a fresh random 256-bit handle hash/independent epoch and bounded
    /// protected bytes. This port neither generates handles nor validates a provider/ticket codec.
    /// An ambiguous create remains an error; reconciliation of an exact known key is explicit.
    /// </summary>
    Task<ServerSessionWriteResult> CreateOnlyAsync(ServerSessionCreateIntent intent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Positive expected-version CAS against one fresh exact live session/binding/owner observation.
    /// Stale conflicts even for no-op. Unchanged preserves version; actual idle extension advances once,
    /// capped to the fixed original absolute deadline; maximum-version mutation refuses. Preserve key,
    /// binding snapshot, epoch, issued/absolute times, policy/codec and exact ticket bytes. No insertion,
    /// revival or blind retry; a caller may separately observe the latest same identity after conflict.
    /// </summary>
    Task<ServerSessionWriteResult> RenewAsync(ServerSessionKey key, long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke this exact row once at database time and increment version once. Existing revoked or
    /// missing row is an idempotent no-op; parent inactivity/expiry does not block revocation. Maximum
    /// version refuses a genuine change; it is never successful logout. Preserve all other facts;
    /// never delete, recreate, revive or revoke another session/account. Concurrent renew/revoke must
    /// recheck the locked/current row after waits. Acknowledgement is distinct from browser delivery.
    /// </summary>
    Task<ServerSessionWriteResult> RevokeAsync(ServerSessionKey key, CancellationToken cancellationToken = default);
}
