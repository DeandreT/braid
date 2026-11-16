using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Contracts.Auth;

namespace Braid.Contracts.Catalog;

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
    InternalFailure = 6,
    PreconditionRequired = 7,
    PreconditionFailed = 8,
    UnsupportedMediaType = 9,
    BodyLimit = 10,
    VersionExhausted = 11,
    IngestionAdmissionBusy = 12,
    PermissionDenied = 13,
    IdempotencyConflict = 14,
    ProjectCreationAdmissionBusy = 15,
    RevisionReportAdmissionBusy = 16,
    RevisionReportClaimLost = 17,
    RevisionReportSelectionChanged = 18,
    RevisionReportGenerationExhausted = 19,
    RevisionReportUnavailable = 20,
    RevisionReportResourceLimit = 21,
    RateLimited = 22
}

public sealed record CatalogFeaturesV1
{
    public CatalogFeaturesV1(bool readCatalog, bool createProjects, bool createMappings, bool editMetadata,
        bool uploadRevisions, bool readReports, bool chat, bool preview, bool manageMembership)
        : this(readCatalog, createProjects, createMappings, editMetadata, uploadRevisions, readReports,
            chat, preview, manageMembership, false)
    {
    }

    [JsonConstructor]
    public CatalogFeaturesV1(bool readCatalog, bool createProjects, bool createMappings, bool editMetadata,
        bool uploadRevisions, bool readReports, bool chat, bool preview, bool manageMembership, bool deriveReports, bool edit = false,
        bool? csvPreview = null, bool xmlPreview = false)
    {
        if (deriveReports && !readReports)
            throw new ArgumentException("Report derivation requires report reads.", nameof(deriveReports));
        if (csvPreview == true && !preview)
            throw new ArgumentException("CSV previews require preview routes.", nameof(csvPreview));
        if (xmlPreview == true && !preview) throw new ArgumentException("XML requires preview routes.", nameof(xmlPreview));
        ReadCatalog = readCatalog;
        CreateProjects = createProjects;
        CreateMappings = createMappings;
        EditMetadata = editMetadata;
        UploadRevisions = uploadRevisions;
        ReadReports = readReports;
        Chat = chat;
        Preview = preview;
        ManageMembership = manageMembership;
        DeriveReports = deriveReports;
        Edit = edit;
        CsvPreview = csvPreview == true ? true : null;
        XmlPreview = xmlPreview == true;
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
    public bool DeriveReports { get; }
    public bool Edit { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CsvPreview { get; }
    public bool XmlPreview { get; }

    public static CatalogFeaturesV1 ReadOnly { get; } = new(true, false, false, false, false, false, false, false, false);
}

public sealed record CatalogActionsV1
{
    public CatalogActionsV1(bool view, bool createProjects, bool createMappings, bool editMetadata,
        bool uploadRevisions, bool readReports, bool chat, bool preview, bool manageMembership)
        : this(view, createProjects, createMappings, editMetadata, uploadRevisions, readReports,
            chat, preview, manageMembership, false)
    {
    }

    [JsonConstructor]
    public CatalogActionsV1(bool view, bool createProjects, bool createMappings, bool editMetadata,
        bool uploadRevisions, bool readReports, bool chat, bool preview, bool manageMembership, bool deriveReports, bool edit = false)
    {
        if (deriveReports && !readReports)
            throw new ArgumentException("Report derivation requires report reads.", nameof(deriveReports));
        View = view;
        CreateProjects = createProjects;
        CreateMappings = createMappings;
        EditMetadata = editMetadata;
        UploadRevisions = uploadRevisions;
        ReadReports = readReports;
        Chat = chat;
        Preview = preview;
        ManageMembership = manageMembership;
        DeriveReports = deriveReports;
        Edit = edit;
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
    public bool DeriveReports { get; }
    public bool Edit { get; }

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
        : this(mode, actorPrincipalId, allowedOrganizationId, organizationName, features,
            CatalogOrganizationActionsV1.None, requestToken)
    {
    }

    public CatalogContextV1(string mode, string actorPrincipalId, string allowedOrganizationId,
        string organizationName, CatalogFeaturesV1 features, CatalogOrganizationActionsV1 organizationActions,
        string requestToken)
        : this(mode, actorPrincipalId, allowedOrganizationId, organizationName, features, organizationActions,
            requestToken, null)
    {
    }

    /// <summary>
    /// Bearer mode is the context of a signed-in user's bearer request: it has neither an antiforgery request
    /// token nor a session identity context, because the credential is not ambient and no session exists.
    /// Both are optional here so that a bearer context, which omits them, deserializes; the other modes still
    /// require the request token.
    /// </summary>
    [JsonConstructor]
    public CatalogContextV1(string mode, string actorPrincipalId, string allowedOrganizationId,
        string organizationName, CatalogFeaturesV1 features, CatalogOrganizationActionsV1 organizationActions,
        string? requestToken = null, string? identityContext = null)
    {
        if (mode is not ("development" or "session" or "bearer"))
            throw new ArgumentException("The catalog identity mode is unsupported.", nameof(mode));
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(organizationActions);
        if (organizationActions.CreateProjects && !features.CreateProjects)
            throw new ArgumentException("An organization action requires its corresponding catalog feature.", nameof(organizationActions));
        Mode = mode;
        ActorPrincipalId = CatalogValueV1.RequireId(actorPrincipalId, nameof(actorPrincipalId));
        AllowedOrganizationId = CatalogValueV1.RequireId(allowedOrganizationId, nameof(allowedOrganizationId));
        OrganizationName = CatalogValueV1.RequireName(organizationName, nameof(organizationName));
        Features = features;
        OrganizationActions = organizationActions;
        RequestToken = mode != "bearer" ? CatalogValueV1.RequireText(requestToken!, 4096, nameof(requestToken))
            : requestToken is null ? null : throw new ArgumentException("A bearer context has no request token.", nameof(requestToken));
        if (mode == "session" ? !AuthValueV1.IsIdentityContext(identityContext) : identityContext is not null)
            throw new ArgumentException("The catalog identity context is invalid.", nameof(identityContext));
        IdentityContext = identityContext;
    }

    public string Mode { get; }
    public string ActorPrincipalId { get; }
    public string AllowedOrganizationId { get; }
    public string OrganizationName { get; }
    public CatalogFeaturesV1 Features { get; }
    public CatalogOrganizationActionsV1 OrganizationActions { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestToken { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityContext { get; }

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
