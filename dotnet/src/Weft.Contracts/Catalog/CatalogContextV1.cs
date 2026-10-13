using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weft.Contracts.Catalog;

[JsonConverter(typeof(CatalogRoleConverterV1))]
public enum CatalogRoleV1 { Viewer = 1, Runner = 2, Publisher = 3, Administrator = 4 }

[JsonConverter(typeof(CatalogAdmissionConverterV1))]
public enum CatalogAdmissionV1 { Valid = 1, DecodedInvalid = 2 }

[JsonConverter(typeof(CatalogErrorCodeConverterV1))]
public enum CatalogErrorCodeV1
{
    Unavailable = 1,
    InvalidRequest = 2,
    NotFound = 3,
    IdentityRejected = 4,
    PersistenceFailure = 5,
    InternalFailure = 6
}

public sealed record CatalogFeaturesV1
{
    public CatalogFeaturesV1(bool readCatalog, bool createProjects, bool createMappings, bool editMetadata,
        bool uploadRevisions, bool readReports, bool chat, bool preview, bool manageMembership)
    {
        ReadCatalog = readCatalog;
        CreateProjects = createProjects;
        CreateMappings = createMappings;
        EditMetadata = editMetadata;
        UploadRevisions = uploadRevisions;
        ReadReports = readReports;
        Chat = chat;
        Preview = preview;
        ManageMembership = manageMembership;
    }

    public bool ReadCatalog { get; }
    public bool CreateProjects { get; }
    public bool CreateMappings { get; }
    public bool EditMetadata { get; }
    public bool UploadRevisions { get; }
    public bool ReadReports { get; }
    public bool Chat { get; }
    public bool Preview { get; }
    public bool ManageMembership { get; }

    public static CatalogFeaturesV1 ReadOnly { get; } = new(true, false, false, false, false, false, false, false, false);
}

public sealed record CatalogActionsV1
{
    public CatalogActionsV1(bool view, bool createProjects, bool createMappings, bool editMetadata,
        bool uploadRevisions, bool readReports, bool chat, bool preview, bool manageMembership)
    {
        View = view;
        CreateProjects = createProjects;
        CreateMappings = createMappings;
        EditMetadata = editMetadata;
        UploadRevisions = uploadRevisions;
        ReadReports = readReports;
        Chat = chat;
        Preview = preview;
        ManageMembership = manageMembership;
    }

    public bool View { get; }
    public bool CreateProjects { get; }
    public bool CreateMappings { get; }
    public bool EditMetadata { get; }
    public bool UploadRevisions { get; }
    public bool ReadReports { get; }
    public bool Chat { get; }
    public bool Preview { get; }
    public bool ManageMembership { get; }

    public static CatalogActionsV1 ReadOnly { get; } = new(true, false, false, false, false, false, false, false, false);
}

public sealed record CatalogAccessV1
{
    public CatalogAccessV1(CatalogRoleV1 role, CatalogActionsV1 actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), "A catalog role must be known.");
        Role = role;
        Actions = actions;
    }

    public CatalogRoleV1 Role { get; }
    public CatalogActionsV1 Actions { get; }

    public static CatalogAccessV1 ReadOnly(CatalogRoleV1 role) => new(role, CatalogActionsV1.ReadOnly);
}

/// <summary>Successfully admitted context only. The request token is serialized but omitted from diagnostic text.</summary>
public sealed record CatalogContextV1
{
    public CatalogContextV1(string mode, string actorPrincipalId, string allowedOrganizationId,
        string organizationName, CatalogFeaturesV1 features, string requestToken)
    {
        if (mode != "development")
            throw new ArgumentException("The catalog identity mode is unsupported.", nameof(mode));
        ArgumentNullException.ThrowIfNull(features);
        Mode = mode;
        ActorPrincipalId = CatalogValueV1.RequireId(actorPrincipalId, nameof(actorPrincipalId));
        AllowedOrganizationId = CatalogValueV1.RequireId(allowedOrganizationId, nameof(allowedOrganizationId));
        OrganizationName = CatalogValueV1.RequireName(organizationName, nameof(organizationName));
        Features = features;
        RequestToken = CatalogValueV1.RequireText(requestToken, 4096, nameof(requestToken));
    }

    public string Mode { get; }
    public string ActorPrincipalId { get; }
    public string AllowedOrganizationId { get; }
    public string OrganizationName { get; }
    public CatalogFeaturesV1 Features { get; }
    public string RequestToken { get; }

    public override string ToString() => $"CatalogContextV1 {{ Mode = {Mode}, ActorPrincipalId = {ActorPrincipalId}, AllowedOrganizationId = {AllowedOrganizationId} }}";
}

public sealed record CatalogErrorV1
{
    public CatalogErrorV1(CatalogErrorCodeV1 code)
    {
        if (!Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(nameof(code), "A catalog error code must be known.");
        Code = code;
    }

    public CatalogErrorCodeV1 Code { get; }
}

internal sealed class CatalogRoleConverterV1 : JsonStringEnumConverter<CatalogRoleV1>
{
    public CatalogRoleConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class CatalogAdmissionConverterV1 : JsonStringEnumConverter<CatalogAdmissionV1>
{
    public CatalogAdmissionConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

internal sealed class CatalogErrorCodeConverterV1 : JsonStringEnumConverter<CatalogErrorCodeV1>
{
    public CatalogErrorCodeConverterV1() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
