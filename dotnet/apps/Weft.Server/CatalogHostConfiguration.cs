using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;

namespace Weft.Server;

/// <summary>Explicit host composition; disabled catalog never creates a database pool.</summary>
public static class CatalogHostConfiguration
{
    public static void Configure(WebApplicationBuilder builder, IPEndPoint listener,
        Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(listener);
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var options = new CatalogApiOptions(
            Flag("WEFT_CATALOG_ENABLED"), Flag("WEFT_DEV_IDENTITY_ENABLED"),
            Id("WEFT_DEV_PRINCIPAL_ID"), Id("WEFT_DEV_ORGANIZATION_ID"),
            readEnvironment("WEFT_DEV_BROWSER_ORIGIN"));
        var metadataOptions = new CatalogMetadataEditOptions(Flag("WEFT_METADATA_EDITS_ENABLED"));
        var ingestionOptions = new CatalogRevisionIngestionOptions(Flag("WEFT_REVISION_INGESTION_ENABLED"));
        var creationOptions = new CatalogProjectCreationOptions(Flag("WEFT_PROJECT_CREATION_ENABLED"));
        var reportOptions = new CatalogRevisionReportOptions(Flag("WEFT_REVISION_REPORTS_ENABLED"),
            Flag("WEFT_REVISION_REPORT_DERIVATION_ENABLED"));
        var previewOptions = new CatalogPreviewOptions(Flag("WEFT_PREVIEWS_ENABLED"));
        builder.Services.AddWeftCatalog(options, builder.Environment, metadataOptions, ingestionOptions, creationOptions, reportOptions);
        var chat = Flag("WEFT_CHAT_ENABLED");
        if (previewOptions.Enabled && !options.Enabled)
            throw new InvalidOperationException("Previews require an enabled catalog.");
        if (!options.Enabled)
        {
            CatalogRegressionHostConfiguration.Configure(builder.Services, readEnvironment, catalogEnabled: false);
            ChatHostConfiguration.Configure(builder.Services, readEnvironment, chat, false, reportOptions);
            return;
        }
        var origin = new Uri(options.BrowserOrigin!, UriKind.Absolute);
        if (!IPAddress.IsLoopback(listener.Address) || origin.Port != listener.Port ||
            !IPAddress.Parse(origin.Host.Trim('[', ']')).Equals(listener.Address))
            throw new InvalidOperationException("The catalog browser origin must match the concrete configured loopback listener.");
        CatalogBusinessServices.Configure(builder.Services, builder.Environment, metadataOptions, ingestionOptions,
            creationOptions, reportOptions, readEnvironment);
        ChatHostConfiguration.Configure(builder.Services, readEnvironment, chat, true, reportOptions);
        CatalogPreviewHostConfiguration.Configure(builder.Services, previewOptions, readEnvironment);

        bool Flag(string variable) => readEnvironment(variable) switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException("Catalog enablement options must be explicit true or false.")
        };

        Guid? Id(string variable)
        {
            var value = readEnvironment(variable);
            if (value is null)
                return null;
            if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
                throw new ArgumentException("Catalog identity configuration requires canonical nonempty UUIDs.");
            return id;
        }
    }
}
