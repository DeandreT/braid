using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Persistence.Postgres;
using Braid.Server;

namespace Braid.Tests;

/// <summary>Pure composition tests; no database is opened and no message host starts.</summary>
public sealed class CatalogPreviewHostConfigurationTests
{
    private const string Flag = "BRAID_PREVIEWS_ENABLED";
    private const string WriterCredential = "BRAID_POSTGRES_PREVIEW_WRITER_CONNECTION_STRING";
    private const string ReaderCredential = "BRAID_POSTGRES_PREVIEW_READER_CONNECTION_STRING";
    private const string Secret = "synthetic-never-connected-preview-secret";
    private const string Connection = "Host=127.0.0.1;Database=braid;Username=braid_preview_writer;Password=" + Secret;
    private static readonly string[] PreviewDependencies = [WriterCredential, ReaderCredential];

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledPreviewsReadNoPreviewCredentialsAndRegisterNoRunService(string? flag)
    {
        var configuration = CatalogConfiguration();
        if (flag is not null) configuration[Flag] = flag;
        var read = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, read);
        Assert.DoesNotContain(read, PreviewDependencies.Contains);
        Assert.False(Assert.IsType<CatalogPreviewOptions>(Assert.Single(builder.Services,
            item => item.ServiceType == typeof(CatalogPreviewOptions)).ImplementationInstance).Enabled);
        Assert.DoesNotContain(builder.Services, item => item.ServiceType == typeof(PreviewRunService)
            || item.ServiceType == typeof(PostgresPreviewDatabase));
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData(" true")]
    public void PreviewFlagIsStrictAndRefusesBeforeCredentials(string value)
    {
        var configuration = CatalogConfiguration();
        configuration[Flag] = value;
        configuration[WriterCredential] = Connection;
        configuration[ReaderCredential] = Connection;
        var read = new List<string>();
        Assert.Throws<ArgumentException>(() => Configure(Builder(), configuration, read));
        Assert.DoesNotContain(read, PreviewDependencies.Contains);
    }

    [Fact]
    public void EnabledPreviewsRequireTheCatalogAndBothDedicatedCredentials()
    {
        var read = new List<string>();
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(),
            new Dictionary<string, string> { [Flag] = "true", [WriterCredential] = Connection, [ReaderCredential] = Connection }, read));
        Assert.DoesNotContain(read, PreviewDependencies.Contains);

        foreach (var missing in PreviewDependencies)
        {
            var configuration = CatalogConfiguration();
            configuration[Flag] = "true";
            foreach (var credential in PreviewDependencies.Where(credential => credential != missing))
                configuration[credential] = Connection;
            var error = Assert.Throws<InvalidOperationException>(() => Configure(Builder(), configuration));
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnabledPreviewsRegisterTheSubmitterServiceAndSeparateProfilesWithoutConnecting()
    {
        var configuration = CatalogConfiguration();
        configuration[Flag] = "true";
        configuration[WriterCredential] = Connection;
        configuration[ReaderCredential] = Connection;
        var builder = Builder();
        Configure(builder, configuration);
        Assert.True(Assert.IsType<CatalogPreviewOptions>(Assert.Single(builder.Services,
            item => item.ServiceType == typeof(CatalogPreviewOptions)).ImplementationInstance).Enabled);
        Assert.Single(builder.Services, item => item.ServiceType == typeof(PreviewRunService));
        Assert.Equal(new object[] { PreviewDatabaseProfile.Reader, PreviewDatabaseProfile.Writer },
            builder.Services.Where(item => item.ServiceType == typeof(PostgresPreviewDatabase))
                .Select(item => item.ServiceKey!).OrderBy(key => key).ToArray());
        Assert.DoesNotContain(builder.Services, item => item.ServiceType == typeof(PreviewRunExecutor));
    }

    [Fact]
    public void Csv_preview_configuration_requires_sources_before_preview_credentials()
    {
        var configuration = CatalogConfiguration();
        configuration[Flag] = "true";
        configuration[CatalogPreviewOptions.CsvEnabledSetting] = "true";
        var reads = new List<string>();
        var absent = Builder();
        var error = Assert.Throws<InvalidOperationException>(() => Configure(absent, configuration, reads));
        Assert.Contains("IPrivateObjectStore and IRevisionReportMetadataReader", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(reads, PreviewDependencies.Contains);

        configuration[WriterCredential] = Connection;
        configuration[ReaderCredential] = Connection;
        var present = Builder();
        var resolved = 0;
        CsvSourceTestServices.Register(present.Services, () => resolved++);
        Configure(present, configuration);
        var options = (CatalogPreviewOptions)present.Services.Single(item => item.ServiceType == typeof(CatalogPreviewOptions)).ImplementationInstance!;
        Assert.True(options.Enabled && options.CsvEnabled);
        Assert.Equal(0, resolved);
    }

    private static WebApplicationBuilder Builder() => WebApplication.CreateBuilder(new WebApplicationOptions
    { EnvironmentName = "Development", Args = [] });

    private static void Configure(WebApplicationBuilder builder, Dictionary<string, string> configuration, List<string>? read = null) =>
        CatalogHostConfiguration.Configure(builder, new IPEndPoint(IPAddress.Loopback, 4100),
            variable => { read?.Add(variable); return configuration.GetValueOrDefault(variable); });

    private static Dictionary<string, string> CatalogConfiguration() => new()
    {
        ["BRAID_CATALOG_ENABLED"] = "true",
        ["BRAID_DEV_IDENTITY_ENABLED"] = "true",
        ["BRAID_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
        ["BRAID_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
        ["BRAID_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["BRAID_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Database=braid;Username=braid_catalog_reader;Password=" + Secret
    };
}
