using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// The verified identity an application credential already established: the current enabled binding's
/// identifier and version plus the resolved global principal. Supplied to the restricted discovery
/// function; it never comes from request input.
/// </summary>
public sealed record OrganizationDiscoveryIdentity
{
    public OrganizationDiscoveryIdentity(PrincipalId principalId, Guid bindingId, long bindingVersion)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        if (bindingId == Guid.Empty || bindingVersion < 1)
            throw new ArgumentException("Organization discovery requires a current enabled binding.");
        PrincipalId = principalId;
        BindingId = bindingId;
        BindingVersion = bindingVersion;
    }

    public PrincipalId PrincipalId { get; }
    public Guid BindingId { get; }
    public long BindingVersion { get; }
}

/// <summary>One currently active admission's public routing metadata; names are untrusted display text.</summary>
public sealed record OrganizationDiscoveryItem(Guid OrganizationId, string Name);

/// <summary>A canonical-order page; revocation may remove later entries before a next read.</summary>
public sealed record OrganizationDiscoveryPage(IReadOnlyList<OrganizationDiscoveryItem> Items, Guid? After)
{
    public OrganizationDiscoveryPage(IEnumerable<OrganizationDiscoveryItem> items, Guid? after)
        : this(items.ToArray(), after) { }
}

/// <summary>Restricted discovery of one verified principal's currently active admissions.</summary>
public interface IOrganizationDiscovery
{
    /// <summary>At most <paramref name="limit"/> admissions after <paramref name="after"/>, or a store failure.</summary>
    Task<OrganizationDiscoveryPage?> DiscoverAsync(OrganizationDiscoveryIdentity identity, int limit, Guid? after,
        CancellationToken cancellationToken = default);
}
