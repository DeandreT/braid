using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Weft.Api.Catalog;

/// <summary>Explicit server configuration for the Development-only catalog identity boundary.</summary>
public sealed record CatalogApiOptions
{
    public CatalogApiOptions(bool enabled = false, bool developmentIdentityEnabled = false,
        Guid? principalId = null, Guid? organizationId = null, string? browserOrigin = null)
    {
        if (principalId == Guid.Empty)
            throw new ArgumentException("The configured catalog principal cannot be empty.", nameof(principalId));
        if (organizationId == Guid.Empty)
            throw new ArgumentException("The configured catalog organization cannot be empty.", nameof(organizationId));
        if (browserOrigin is not null)
            _ = ParseOrigin(browserOrigin);
        Enabled = enabled;
        DevelopmentIdentityEnabled = developmentIdentityEnabled;
        PrincipalId = principalId;
        OrganizationId = organizationId;
        BrowserOrigin = browserOrigin;
    }

    public bool Enabled { get; }
    public bool DevelopmentIdentityEnabled { get; }
    public Guid? PrincipalId { get; }
    public Guid? OrganizationId { get; }
    public string? BrowserOrigin { get; }
    internal string? Authority => BrowserOrigin?["http://".Length..];

    internal void Validate(IHostEnvironment environment)
    {
        if (DevelopmentIdentityEnabled && !environment.IsDevelopment())
            throw new InvalidOperationException("The catalog Development identity provider cannot run in a hosted environment.");
        if (!Enabled)
            return;
        if (!environment.IsDevelopment() || !DevelopmentIdentityEnabled || PrincipalId is null ||
            OrganizationId is null || BrowserOrigin is null)
            throw new InvalidOperationException("Enabled catalog requires explicit Development identity, organization, and loopback browser origin.");
    }

    private static Uri ParseOrigin(string origin)
    {
        if (origin.Length > 128 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || uri.Port is < 1 or > 65535 ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("The catalog browser origin must be a canonical concrete loopback HTTP origin.", nameof(origin));
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "[" + address + "]" : address.ToString();
        var canonical = "http://" + host + (uri.IsDefaultPort ? "" :
            ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.Equals(origin, canonical, StringComparison.Ordinal))
            throw new ArgumentException("The catalog browser origin must be a canonical concrete loopback HTTP origin.", nameof(origin));
        return uri;
    }
}

public static partial class CatalogEndpoints
{
    public static IServiceCollection AddWeftCatalog(this IServiceCollection services,
        CatalogApiOptions options, IHostEnvironment environment) =>
        AddWeftCatalog(services, options, environment, new CatalogMetadataEditOptions());

    public static IServiceCollection AddWeftCatalog(this IServiceCollection services,
        CatalogApiOptions options, IHostEnvironment environment, CatalogMetadataEditOptions metadataEditOptions) =>
        AddWeftCatalog(services, options, environment, metadataEditOptions, new CatalogRevisionIngestionOptions());

    public static IServiceCollection AddWeftCatalog(this IServiceCollection services,
        CatalogApiOptions options, IHostEnvironment environment, CatalogMetadataEditOptions metadataEditOptions,
        CatalogRevisionIngestionOptions ingestionOptions) =>
        AddWeftCatalog(services, options, environment, metadataEditOptions, ingestionOptions,
            new CatalogProjectCreationOptions());

    public static IServiceCollection AddWeftCatalog(this IServiceCollection services,
        CatalogApiOptions options, IHostEnvironment environment, CatalogMetadataEditOptions metadataEditOptions,
        CatalogRevisionIngestionOptions ingestionOptions, CatalogProjectCreationOptions creationOptions)
        => AddWeftCatalog(services, options, environment, metadataEditOptions, ingestionOptions, creationOptions,
            new CatalogRevisionReportOptions());

    public static IServiceCollection AddWeftCatalog(this IServiceCollection services,
        CatalogApiOptions options, IHostEnvironment environment, CatalogMetadataEditOptions metadataEditOptions,
        CatalogRevisionIngestionOptions ingestionOptions, CatalogProjectCreationOptions creationOptions,
        CatalogRevisionReportOptions revisionReportOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(metadataEditOptions);
        ArgumentNullException.ThrowIfNull(ingestionOptions);
        ArgumentNullException.ThrowIfNull(creationOptions);
        ArgumentNullException.ThrowIfNull(revisionReportOptions);
        options.Validate(environment);
        RequireUnregisteredCatalog(services);
        if (metadataEditOptions.Enabled && !options.Enabled)
            throw new InvalidOperationException("Metadata edits require an enabled catalog.");
        if (ingestionOptions.Enabled && !options.Enabled)
            throw new InvalidOperationException("Revision ingestion requires an enabled catalog.");
        if (creationOptions.Enabled && !options.Enabled)
            throw new InvalidOperationException("Project creation requires an enabled catalog.");
        if (revisionReportOptions.Enabled && !options.Enabled)
            throw new InvalidOperationException("Revision reports require an enabled catalog.");
        services.AddSingleton(options);
        RegisterCatalogFeatures(services, metadataEditOptions, ingestionOptions, creationOptions, revisionReportOptions);
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = CatalogRequestBoundary.RequestTokenHeader;
            antiforgery.Cookie.Name = ".Weft.Catalog.Antiforgery";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.Path = "/api/v1";
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.None;
        });
        services.AddSingleton<CatalogRequestBoundary>();
        return services;
    }
}
