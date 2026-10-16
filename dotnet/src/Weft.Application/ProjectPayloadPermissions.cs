using Weft.Domain;

namespace Weft.Application;

/// <summary>
/// One explicit project run.payload.read grant observation. The complete scope and principal
/// are immutable ownership facts; Active and Version describe only this observed row.
/// A grant is not project authority, a lease, a token or a payload/storage capability.
/// </summary>
public sealed record ProjectPayloadGrant
{
    public ProjectPayloadGrant(ProjectScope scope, PrincipalId principalId, bool active, long version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Scope = scope;
        PrincipalId = principalId;
        Active = active;
        Version = version;
    }

    public ProjectScope Scope { get; }
    public PrincipalId PrincipalId { get; }
    public bool Active { get; }
    public long Version { get; }
    public override string ToString() => nameof(ProjectPayloadGrant);
}

public enum ProjectPayloadGrantOwnerOutcome
{
    Created = 1,
    Updated = 2,
    Unchanged = 3,
    AuthorityUnavailable = 4,
    VersionConflict = 5,
    VersionExhausted = 6
}

/// <summary>
/// Only acknowledged owner success carries grant facts. An absent revoke is Unchanged without
/// a row; authority/version refusal exposes no existing grant. Storage failures are not outcomes.
/// </summary>
public sealed class ProjectPayloadGrantOwnerResult
{
    private ProjectPayloadGrantOwnerResult(ProjectPayloadGrantOwnerOutcome outcome, ProjectPayloadGrant? grant)
    {
        Outcome = outcome;
        Grant = grant;
    }

    public ProjectPayloadGrantOwnerOutcome Outcome { get; }
    public ProjectPayloadGrant? Grant { get; }
    public override string ToString() => nameof(ProjectPayloadGrantOwnerResult);

    public static ProjectPayloadGrantOwnerResult ForGrant(ProjectPayloadGrantOwnerOutcome outcome,
        ProjectPayloadGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var valid = outcome switch
        {
            ProjectPayloadGrantOwnerOutcome.Created => grant.Active && grant.Version == 1,
            ProjectPayloadGrantOwnerOutcome.Updated => grant.Version >= 2,
            ProjectPayloadGrantOwnerOutcome.Unchanged => true,
            _ => false
        };
        if (!valid)
            throw new ArgumentException("Only coherent owner success may carry payload grant facts.", nameof(outcome));
        return new(outcome, grant);
    }

    public static ProjectPayloadGrantOwnerResult WithoutGrant(ProjectPayloadGrantOwnerOutcome outcome)
    {
        if (outcome is not (ProjectPayloadGrantOwnerOutcome.Unchanged or
            ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable or ProjectPayloadGrantOwnerOutcome.VersionConflict or
            ProjectPayloadGrantOwnerOutcome.VersionExhausted))
            throw new ArgumentException("Only an absent no-op or owner refusal may omit payload grant facts.", nameof(outcome));
        return new(outcome, null);
    }
}

/// <summary>
/// Pure classification of one current store observation, never a lookup, cache, mutation or
/// authorization for later I/O. Callers must obtain authority and grant together at their actual
/// boundary; combining an earlier ProjectAccess with a later grant read violates this contract.
/// Existing ProjectAccess validates known roles; this policy adds no role/action matrix.
/// </summary>
public static class ProjectPayloadPermissionPolicy
{
    /// <summary>
    /// Returns only the exact active grant intersected with current active principal, existing
    /// organization, active organization admission and active exact project membership. A null
    /// access represents any missing current authority; absence and revoked grant are also null.
    /// A returned foreign tuple is an integrity failure, never a successful denial or permission.
    /// </summary>
    public static ProjectPayloadGrant? ObserveCurrent(ActorContext actor, ProjectScope scope,
        ProjectAccess? currentAccess, ProjectPayloadGrant? grant)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        RequireTuple(scope, actor.PrincipalId, currentAccess, grant);
        return currentAccess is not null && grant is { Active: true } ? grant : null;
    }

    /// <summary>
    /// Classifies already locked current owner facts only. Active requests require the same current
    /// project authority as ObserveCurrent, even for no-op/regrant; revoke does not require active
    /// parents. Expected zero means absent. Existing rows require exact version before same-state
    /// replay; only actual state change advances once, and maximum version refuses mutation.
    /// Persistence must independently enforce ownership, fresh authority, locks, CAS and commit.
    /// </summary>
    public static ProjectPayloadGrantOwnerOutcome ClassifySet(ProjectScope scope, PrincipalId principalId,
        bool active, long expectedVersion, ProjectAccess? currentAccess, ProjectPayloadGrant? existing)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 0);
        RequireTuple(scope, principalId, currentAccess, existing);
        if (active && currentAccess is null)
            return ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable;
        if (existing is null)
        {
            if (expectedVersion != 0)
                return ProjectPayloadGrantOwnerOutcome.VersionConflict;
            return active ? ProjectPayloadGrantOwnerOutcome.Created : ProjectPayloadGrantOwnerOutcome.Unchanged;
        }
        if (existing.Version != expectedVersion)
            return ProjectPayloadGrantOwnerOutcome.VersionConflict;
        if (existing.Active == active)
            return ProjectPayloadGrantOwnerOutcome.Unchanged;
        return existing.Version == long.MaxValue ? ProjectPayloadGrantOwnerOutcome.VersionExhausted
            : ProjectPayloadGrantOwnerOutcome.Updated;
    }

    private static void RequireTuple(ProjectScope scope, PrincipalId principalId,
        ProjectAccess? currentAccess, ProjectPayloadGrant? grant)
    {
        if (currentAccess is not null && (currentAccess.Scope != scope || currentAccess.PrincipalId != principalId) ||
            grant is not null && (grant.Scope != scope || grant.PrincipalId != principalId))
            throw new InvalidOperationException("The current project payload observation has inconsistent ownership.");
    }
}

/// <summary>
/// Dedicated restricted-store observation only; never provision or infer a grant. Each call uses
/// one fresh statement in an organization-bound read-only transaction to intersect existing exact
/// project authority with this principal's active explicit grant. Use existing ProjectAccess/known
/// roles and authority predicates, with no role promotion, cache or extra role matrix. Null hides all
/// missing/inactive authority and absent/revoked grant distinctions. A positive result contains only
/// its exact scope, principal and active grant version; it must be rechecked at future sensitive I/O.
/// Invalid source/schema facts, storage failure and cancellation remain exceptions, never null.
/// No payload, run/sample, native/report object, external binding, session or storage is read here.
/// </summary>
public interface IProjectPayloadPermissionReader
{
    Task<ProjectPayloadGrant?> ObserveAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit separate migration-owner operation, never registered in the web application or called
/// by identity resolution/login, claims, Development provisioning or project-role changes. Require
/// the exact current migration/schema contract and lock current authority/grant facts in established
/// parent order. Active requests require existing project, active principal, active organization
/// admission and active exact project membership after any wait. Organizations have no active flag.
/// Revoke remains possible without active authority and never creates/reactivates parent facts.
/// </summary>
public interface IProjectPayloadGrantOwner
{
    /// <summary>
    /// Scope/principal identify one immutable tuple. Expected version must be nonnegative: zero
    /// means absent; absent revoke is Unchanged/no row, and absent grant creates active version one.
    /// Existing rows require the exact current version even for no-op. Actual changes increment once;
    /// explicit later owner regrant is permitted, maximum-version mutation is VersionExhausted, and
    /// unchanged maximum-version replay remains valid. No deletion, implicit grant or automatic retry.
    /// Return success only after acknowledged COMMIT; SQL/storage/integrity failure, cancellation and
    /// ambiguous commit remain exceptions. No method result authorizes subsequent payload I/O.
    /// </summary>
    Task<ProjectPayloadGrantOwnerResult> SetAsync(ProjectScope scope, PrincipalId principalId, bool active,
        long expectedVersion, CancellationToken cancellationToken = default);
}
