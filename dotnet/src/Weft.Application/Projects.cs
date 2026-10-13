using Weft.Domain;

namespace Weft.Application;

/// <summary>
/// Internal identity supplied by a trusted server identity boundary. The actor
/// carries no role grants; each operation must read current memberships.
/// </summary>
public sealed record ActorContext
{
    public ActorContext(PrincipalId principalId)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        PrincipalId = principalId;
    }

    public PrincipalId PrincipalId { get; }
}

public sealed record ProjectSummary
{
    public ProjectSummary(ProjectScope scope, string name, string description, PrincipalId creatorPrincipalId,
        DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc, long version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(creatorPrincipalId);
        if (updatedAtUtc < createdAtUtc)
            throw new ArgumentException("A project update cannot precede its creation.", nameof(updatedAtUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        Scope = scope;
        Name = name;
        Description = description;
        CreatorPrincipalId = creatorPrincipalId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        Version = version;
    }

    public ProjectScope Scope { get; }
    public string Name { get; }
    public string Description { get; }
    public PrincipalId CreatorPrincipalId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public long Version { get; }
}

/// <summary>
/// One fresh observation of a project membership. A later operation, especially
/// a write, must recheck authority rather than reuse this observed role.
/// </summary>
public sealed record ProjectAccess
{
    public ProjectAccess(ProjectScope scope, PrincipalId principalId, ProjectRole role)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(principalId);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "A project role must be known.");

        Scope = scope;
        PrincipalId = principalId;
        Role = role;
    }

    public ProjectScope Scope { get; }
    public PrincipalId PrincipalId { get; }
    public ProjectRole Role { get; }

    public bool Can(ProjectAction action) => ProjectPermissions.Can(Role, action);
}

public sealed record ProjectPage
{
    public ProjectPage(IEnumerable<ProjectSummary> projects, Guid? nextProjectId = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (nextProjectId == Guid.Empty)
            throw new ArgumentException("A project page cursor cannot be empty.", nameof(nextProjectId));

        var snapshot = projects.ToArray();
        if (snapshot.Any(project => project is null))
            throw new ArgumentException("A project page cannot contain null projects.", nameof(projects));

        Projects = Array.AsReadOnly(snapshot);
        NextProjectId = nextProjectId;
    }

    public IReadOnlyList<ProjectSummary> Projects { get; }
    public Guid? NextProjectId { get; }
}

/// <summary>
/// Reads projects after checking current active principal, organization admission,
/// and explicit project membership. Organization admission alone grants no project
/// access. Database failures remain exceptions rather than absent-access results.
/// </summary>
public interface IProjectReader
{
    /// <summary>
    /// Returns visible projects in PostgreSQL UUID ascending order. The limit is
    /// 1 through 100 inclusive, and a nonempty cursor seeks strictly after that
    /// project ID. The next cursor is the last returned project ID when more
    /// visible projects remain. Absent access returns an empty page.
    /// </summary>
    Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit,
        Guid? afterProjectId = null, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the project is not currently visible to the actor.</summary>
    Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Returns null when the actor has no current access to the project.</summary>
    Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default);
}
