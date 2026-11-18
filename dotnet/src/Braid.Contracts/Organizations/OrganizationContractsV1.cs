using System.Text.Json.Serialization;
using Braid.Contracts.Auth;
using Braid.Contracts.Catalog;

namespace Braid.Contracts.Organizations;

/// <summary>The authenticated caller's identity: mode and principal, with the Session identity context.</summary>
public sealed record IdentityV1
{
    public const string Schema = "braid.identity.v1";

    [JsonConstructor]
    public IdentityV1(string mode, string principalId, string? identityContext = null)
    {
        if (mode is not ("development" or "session" or "bearer"))
            throw new ArgumentException("The identity mode is unsupported.", nameof(mode));
        Mode = mode;
        PrincipalId = CatalogValueV1.RequireId(principalId, nameof(principalId));
        if (mode == "session" ? !AuthValueV1.IsIdentityContext(identityContext) : identityContext is not null)
            throw new ArgumentException("The identity context is invalid.", nameof(identityContext));
        IdentityContext = identityContext;
    }

    public string SchemaVersion => Schema;
    public string Mode { get; }
    public string PrincipalId { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityContext { get; }
}

/// <summary>One currently active admission's public routing metadata; the name is untrusted display text.</summary>
public sealed record OrganizationV1
{
    [JsonConstructor]
    public OrganizationV1(string organizationId, string name)
    {
        OrganizationId = CatalogValueV1.RequireId(organizationId, nameof(organizationId));
        Name = CatalogValueV1.RequireName(name, nameof(name));
    }

    public string OrganizationId { get; }
    public string Name { get; }
}

/// <summary>A canonical-order page of active admissions; the opaque cursor binds caller and position.</summary>
public sealed record OrganizationPageV1
{
    public const string Schema = "braid.organizations.v1";

    [JsonConstructor]
    public OrganizationPageV1(IReadOnlyList<OrganizationV1> organizations, string? nextCursor = null)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        if (organizations.Count > 100)
            throw new ArgumentException("An organization page holds at most one hundred entries.", nameof(organizations));
        Organizations = organizations;
        NextCursor = nextCursor;
    }

    public string SchemaVersion => Schema;
    public IReadOnlyList<OrganizationV1> Organizations { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; }
}

/// <summary>The qualified business context for one route-selected organization and admission incarnation.</summary>
public sealed record OrganizationContextV1
{
    public const string Schema = "braid.organization-context.v1";

    [JsonConstructor]
    public OrganizationContextV1(string admissionVersion, CatalogContextV1 catalog)
    {
        if (admissionVersion is null || admissionVersion.Length > 19 ||
            !long.TryParse(admissionVersion, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var version) || version < 1 ||
            admissionVersion != version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("The admission incarnation is not a positive canonical decimal.", nameof(admissionVersion));
        ArgumentNullException.ThrowIfNull(catalog);
        AdmissionVersion = admissionVersion;
        Catalog = catalog;
    }

    public string SchemaVersion => Schema;
    public string AdmissionVersion { get; }
    public CatalogContextV1 Catalog { get; }
}
