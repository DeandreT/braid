using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;

namespace Braid.Server;

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
            Flag("BRAID_CATALOG_ENABLED"), Flag("BRAID_DEV_IDENTITY_ENABLED"),
            Id("BRAID_DEV_PRINCIPAL_ID"), Id("BRAID_DEV_ORGANIZATION_ID"),
            readEnvironment("BRAID_DEV_BROWSER_ORIGIN"));
        var metadataOptions = new CatalogMetadataEditOptions(Flag("BRAID_METADATA_EDITS_ENABLED"));
        var ingestionOptions = new CatalogRevisionIngestionOptions(Flag("BRAID_REVISION_INGESTION_ENABLED"));
        var creationOptions = new CatalogProjectCreationOptions(Flag("BRAID_PROJECT_CREATION_ENABLED"));
        var reportOptions = new CatalogRevisionReportOptions(Flag("BRAID_REVISION_REPORTS_ENABLED"),
            Flag("BRAID_REVISION_REPORT_DERIVATION_ENABLED"));
        var previewOptions = new CatalogPreviewOptions(Flag("BRAID_PREVIEWS_ENABLED"), csvEnabled: Flag(CatalogPreviewOptions.CsvEnabledSetting),
            xmlEnabled: Flag(Braid.Application.XmlPreviewAdmission.EnabledSetting), nativeFormats: Braid.Application.NativeDocumentFormat.EnabledFormats(readEnvironment, false));
        CatalogMappingEditingHostConfiguration.Configure(builder.Services, readEnvironment, options.Enabled, ingestionOptions.Enabled,
            reportOptions.Enabled, reportOptions.DerivationEnabled);
        builder.Services.AddBraidCatalog(options, builder.Environment, metadataOptions, ingestionOptions, creationOptions, reportOptions);
        var chat = Flag("BRAID_CHAT_ENABLED");
        if (previewOptions.Enabled && !options.Enabled)
            throw new InvalidOperationException("Previews require an enabled catalog.");
        if (!options.Enabled)
        {
            CatalogRegressionHostConfiguration.Configure(builder.Services, readEnvironment, catalogEnabled: false);
            _ = CatalogBuildHostConfiguration.ReadRuntime(readEnvironment, catalogEnabled: false);
            _ = ReleaseOperationsHostConfiguration.ReadEnabled(readEnvironment, catalogEnabled: false);
            LocalSandboxHostConfiguration.Configure(builder.Services, builder.Environment, readEnvironment, catalogEnabled: false);
            ChatHostConfiguration.Configure(builder.Services, readEnvironment, chat, false, reportOptions);
            return;
        }
        var origin = new Uri(options.BrowserOrigin!, UriKind.Absolute);
        if (!IPAddress.IsLoopback(listener.Address) || origin.Port != listener.Port ||
            !IPAddress.Parse(origin.Host.Trim('[', ']')).Equals(listener.Address))
            throw new InvalidOperationException("The catalog browser origin must match the concrete configured loopback listener.");
        CatalogBusinessServices.Configure(builder.Services, builder.Environment, metadataOptions, ingestionOptions,
            creationOptions, reportOptions, readEnvironment);
        LocalSandboxHostConfiguration.Configure(builder.Services, builder.Environment, readEnvironment, catalogEnabled: true);
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
