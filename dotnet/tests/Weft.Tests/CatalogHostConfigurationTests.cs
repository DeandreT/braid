using System.Net;
using Microsoft.AspNetCore.Builder;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Persistence.Postgres;
using Weft.Server;

namespace Weft.Tests;

public sealed class CatalogHostConfigurationTests
{
    private static readonly string Principal = "33333333-3333-3333-3333-333333333333";
    private static readonly string Organization = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void DefaultHostMapsClosedCatalogWithoutCreatingAReaderPool()
    {
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Loopback, 4100), _ => null);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(ICatalogReader) ||
            descriptor.ServiceType == typeof(PostgresCatalogDatabase) ||
            descriptor.ServiceType == typeof(IMappingMetadataWriter) ||
            descriptor.ServiceType == typeof(PostgresMappingMetadataDatabase));
        var options = Assert.IsType<CatalogApiOptions>(Assert.Single(builder.Services,
            descriptor => descriptor.ServiceType == typeof(CatalogApiOptions)).ImplementationInstance);
        Assert.False(options.Enabled);
        Assert.Null(options.PrincipalId);
        var edits = Assert.IsType<CatalogMetadataEditOptions>(Assert.Single(builder.Services,
            descriptor => descriptor.ServiceType == typeof(CatalogMetadataEditOptions)).ImplementationInstance);
        Assert.False(edits.Enabled);
    }

    [Fact]
    public void ReadOnlyHostIgnoresUnusedEditorConfigurationAndRegistersNoWriter()
    {
        var configuration = EnabledConfiguration();
        configuration["WEFT_POSTGRES_METADATA_CONNECTION_STRING"] = "unused-invalid-editor-configuration";
        var builder = Builder();
        Configure(builder, configuration);
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(ICatalogReader));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IMappingMetadataWriter) ||
            descriptor.ServiceType == typeof(PostgresMappingMetadataDatabase));
    }

    [Fact]
    public void EnabledEditsRequireDedicatedCredentialsAndCannotFallBackToReaderConfiguration()
    {
        var configuration = EnabledConfiguration();
        configuration["WEFT_METADATA_EDITS_ENABLED"] = "true";
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(), configuration));
    }

    [Fact]
    public void EditsCannotEnableAnOtherwiseClosedCatalog()
    {
        var configuration = new Dictionary<string, string>
        {
            ["WEFT_METADATA_EDITS_ENABLED"] = "true",
            ["WEFT_POSTGRES_METADATA_CONNECTION_STRING"] = "unused-editor-configuration"
        };
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(), configuration));
    }

    [Fact]
    public void EnabledHostRegistersSeparateLazyReaderAndEditorPools()
    {
        var configuration = EnabledConfiguration();
        configuration["WEFT_METADATA_EDITS_ENABLED"] = "true";
        configuration["WEFT_POSTGRES_METADATA_CONNECTION_STRING"] =
            "Host=127.0.0.1;Database=weft;Username=weft_catalog_editor;Password=synthetic-never-connected";
        var builder = Builder();
        Configure(builder, configuration);
        Assert.NotNull(Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(PostgresCatalogDatabase)).ImplementationFactory);
        Assert.NotNull(Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(PostgresMappingMetadataDatabase)).ImplementationFactory);
        Assert.NotNull(Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IMappingMetadataWriter)).ImplementationFactory);
    }

    [Theory]
    [InlineData("WEFT_DEV_IDENTITY_ENABLED")]
    [InlineData("WEFT_DEV_PRINCIPAL_ID")]
    [InlineData("WEFT_DEV_ORGANIZATION_ID")]
    [InlineData("WEFT_DEV_BROWSER_ORIGIN")]
    [InlineData("WEFT_CATALOG_CONNECTION_STRING")]
    public void EnabledHostRequiresEveryExplicitConfigurationValue(string omitted)
    {
        var configuration = EnabledConfiguration();
        configuration.Remove(omitted);
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(), configuration));
    }

    [Theory]
    [InlineData("127.0.0.2", 4100)]
    [InlineData("127.0.0.1", 4101)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("0.0.0.0", 4100)]
    public void OriginCannotSelectAnAliasOrDifferentListener(string address, int port)
    {
        var configuration = EnabledConfiguration();
        Assert.Throws<InvalidOperationException>(() => CatalogHostConfiguration.Configure(Builder(),
            new IPEndPoint(IPAddress.Parse(address), port), variable => configuration.GetValueOrDefault(variable)));
    }

    [Theory]
    [InlineData("WEFT_CATALOG_ENABLED", "1")]
    [InlineData("WEFT_CATALOG_ENABLED", "")]
    [InlineData("WEFT_DEV_IDENTITY_ENABLED", "yes")]
    [InlineData("WEFT_METADATA_EDITS_ENABLED", "yes")]
    [InlineData("WEFT_DEV_PRINCIPAL_ID", "00000000-0000-0000-0000-000000000000")]
    [InlineData("WEFT_DEV_PRINCIPAL_ID", "33333333333333333333333333333333")]
    [InlineData("WEFT_DEV_ORGANIZATION_ID", "not-an-id")]
    public void MalformedExplicitOptionsNeverBecomeDefaults(string variable, string value)
    {
        var configuration = EnabledConfiguration();
        configuration[variable] = value;
        Assert.Throws<ArgumentException>(() => Configure(Builder(), configuration));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void HostedHostRefusesExplicitLocalIdentityEvenWhenCatalogIsOff(string environment)
    {
        var configuration = EnabledConfiguration();
        configuration["WEFT_CATALOG_ENABLED"] = "false";
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(environment), configuration));
    }

    [Theory]
    [InlineData("127.0.0.1", "http://127.0.0.1")]
    [InlineData("::1", "http://[::1]")]
    public void CanonicalDefaultPortOriginsRetainTheExactEffectiveListenerPort(string address, string origin)
    {
        var configuration = EnabledConfiguration();
        configuration["WEFT_DEV_BROWSER_ORIGIN"] = origin;
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Parse(address), 80),
            variable => configuration.GetValueOrDefault(variable));
        var options = Assert.IsType<CatalogApiOptions>(Assert.Single(builder.Services,
            descriptor => descriptor.ServiceType == typeof(CatalogApiOptions)).ImplementationInstance);
        Assert.Equal(origin, options.BrowserOrigin);
        Assert.Equal(80, new Uri(options.BrowserOrigin!).Port);
    }

    private static void Configure(WebApplicationBuilder builder, Dictionary<string, string> configuration) =>
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Loopback, 4100),
            variable => configuration.GetValueOrDefault(variable));

    private static WebApplicationBuilder Builder(string environment = "Development") =>
        WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });

    private static Dictionary<string, string> EnabledConfiguration() => new()
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
        ["WEFT_DEV_PRINCIPAL_ID"] = Principal,
        ["WEFT_DEV_ORGANIZATION_ID"] = Organization,
        ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["WEFT_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Database=weft;Username=weft_catalog_reader;Password=synthetic-never-connected"
    };
}
