using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Persistence.Postgres;

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
        builder.Services.AddWeftCatalog(options, builder.Environment);
        if (!options.Enabled)
            return;

        var origin = new Uri(options.BrowserOrigin!, UriKind.Absolute);
        if (!IPAddress.IsLoopback(listener.Address) || origin.Port != listener.Port ||
            !IPAddress.Parse(origin.Host.Trim('[', ']')).Equals(listener.Address))
            throw new InvalidOperationException("The catalog browser origin must match the concrete configured loopback listener.");
        var connectionString = readEnvironment("WEFT_CATALOG_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Enabled catalog requires its dedicated reader connection configuration.");
        builder.Services.AddSingleton(_ => new PostgresCatalogDatabase(connectionString));
        builder.Services.AddSingleton<ICatalogReader>(services =>
            new PostgresCatalogReader(services.GetRequiredService<PostgresCatalogDatabase>()));

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
