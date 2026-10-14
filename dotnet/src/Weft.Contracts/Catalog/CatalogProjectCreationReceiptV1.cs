namespace Weft.Contracts.Catalog;

/// <summary>Immutable initial creation facts; these do not describe current project access.</summary>
public sealed record CatalogProjectCreationReceiptV1
{
    public CatalogProjectCreationReceiptV1(CatalogProjectScopeV1 project, string name, string description,
        string creatorPrincipalId, DateTimeOffset createdAtUtc, string initialProjectVersion,
        CatalogRoleV1 initialMembershipRole, bool initialMembershipActive, string initialMembershipVersion)
    {
        ArgumentNullException.ThrowIfNull(project);
        Name = CatalogValueV1.RequireName(name, nameof(name));
        if (name != name.Trim())
            throw new ArgumentException("A creation receipt requires the accepted project name.", nameof(name));
        Description = CatalogValueV1.RequireDescription(description);
        CreatorPrincipalId = CatalogValueV1.RequireId(creatorPrincipalId, nameof(creatorPrincipalId));
        if (initialProjectVersion != "1")
            throw new ArgumentException("The initial project version must be one.", nameof(initialProjectVersion));
        if (initialMembershipRole != CatalogRoleV1.Administrator)
            throw new ArgumentOutOfRangeException(nameof(initialMembershipRole), "The initial membership role must be administrator.");
        if (!initialMembershipActive)
            throw new ArgumentException("The initial membership must be active.", nameof(initialMembershipActive));
        if (initialMembershipVersion != "1")
            throw new ArgumentException("The initial membership version must be one.", nameof(initialMembershipVersion));
        Project = project;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        InitialProjectVersion = initialProjectVersion;
        InitialMembershipRole = initialMembershipRole;
        InitialMembershipActive = initialMembershipActive;
        InitialMembershipVersion = initialMembershipVersion;
    }

    public CatalogProjectScopeV1 Project { get; }
    public string Name { get; }
    public string Description { get; }
    public string CreatorPrincipalId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string InitialProjectVersion { get; }
    public CatalogRoleV1 InitialMembershipRole { get; }
    public bool InitialMembershipActive { get; }
    public string InitialMembershipVersion { get; }
}
