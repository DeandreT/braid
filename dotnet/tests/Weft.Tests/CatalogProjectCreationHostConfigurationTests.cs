using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Persistence.Postgres;
using Weft.Server;

namespace Weft.Tests;

/// <summary>Configuration and lazy object composition only; no PostgreSQL connection is opened.</summary>
public sealed class CatalogProjectCreationHostConfigurationTests
{
    private const string CreationFlag = "WEFT_PROJECT_CREATION_ENABLED";
    private const string CreationCredential = "WEFT_POSTGRES_PROJECT_CREATION_CONNECTION_STRING";
    private const string SyntheticSecret = "synthetic-never-connected-creator-secret";
    private const string CreatorConnection =
        "Host=127.0.0.1;Database=weft;Username=weft_project_creator;Password=" + SyntheticSecret;

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledCatalogDoesNotReadUnusedCreatorCredentialsOrRegisterCreationPorts(string? flag)
    {
        var configuration = new Dictionary<string, string>();
        if (flag is not null)
            configuration[CreationFlag] = flag;
        var read = new List<string>();
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, Listener(), variable =>
        {
            read.Add(variable);
            if (variable == CreationCredential)
                throw new InvalidOperationException("Unused creator credentials were read.");
            return configuration.GetValueOrDefault(variable);
        });
        Assert.DoesNotContain(CreationCredential, read);
        AssertNoCreationPorts(builder.Services);
        var options = Options(builder.Services);
        Assert.False(options.Enabled);
        Assert.Equal(2, options.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task ReadOnlyCatalogIgnoresUnusedCreatorSettingsWhenResolvingItsReader()
    {
        var configuration = EnabledCatalog();
        configuration[CreationCredential] = "malformed-unused-creator-setting";
        var read = new List<string>();
        var builder = Builder();
        Configure(builder, configuration, read);
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
        Assert.DoesNotContain(CreationCredential, read);
        AssertNoCreationPorts(builder.Services);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TRUE")]
    [InlineData("False")]
    [InlineData("1")]
    [InlineData(" true")]
    [InlineData("false ")]
    public void ExplicitCreationFlagNeverSilentlyBecomesADefault(string value)
    {
        var configuration = EnabledCatalog();
        configuration[CreationFlag] = value;
        configuration[CreationCredential] = CreatorConnection;
        var error = Assert.Throws<ArgumentException>(() => Configure(Builder(), configuration));
        Assert.DoesNotContain(SyntheticSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void EnabledCreationRequiresItsOwnCredentialWithoutReaderEditorOrIngestionFallback(string? credential)
    {
        var configuration = EnabledCatalog();
        configuration[CreationFlag] = "true";
        configuration["WEFT_POSTGRES_METADATA_CONNECTION_STRING"] = "unused-editor-credential";
        configuration["WEFT_POSTGRES_INGESTION_CONNECTION_STRING"] = "unused-ingestion-credential";
        if (credential is not null)
            configuration[CreationCredential] = credential;
        var builder = Builder();
        var error = Assert.Throws<InvalidOperationException>(() => Configure(builder, configuration));
        Assert.Contains("dedicated creator", error.Message, StringComparison.Ordinal);
        AssertNoCreationPorts(builder.Services);
    }

    [Fact]
    public void CreationCannotEnableAClosedCatalogOrReadItsCredential()
    {
        var builder = Builder();
        var read = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            CatalogHostConfiguration.Configure(builder, Listener(), variable =>
            {
                read.Add(variable);
                if (variable == CreationCredential)
                    throw new InvalidOperationException("Creator credential resolution preceded catalog admission.");
                return variable == CreationFlag ? "true" : null;
            }));
        Assert.DoesNotContain(CreationCredential, read);
        AssertNoCreationPorts(builder.Services);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void EnabledCreationRetainsDevelopmentOnlyIdentityRequirements(string environment)
    {
        var configuration = EnabledCreation();
        var read = new List<string>();
        var builder = Builder(environment);
        Assert.Throws<InvalidOperationException>(() => Configure(builder, configuration, read));
        Assert.DoesNotContain(CreationCredential, read);
        AssertNoCreationPorts(builder.Services);
    }

    [Theory]
    [InlineData("WEFT_DEV_IDENTITY_ENABLED")]
    [InlineData("WEFT_DEV_PRINCIPAL_ID")]
    [InlineData("WEFT_DEV_ORGANIZATION_ID")]
    [InlineData("WEFT_DEV_BROWSER_ORIGIN")]
    public void EnabledCreationRequiresEveryTrustedIdentityFactBeforeCreatorResolution(string missing)
    {
        var configuration = EnabledCreation();
        configuration.Remove(missing);
        var read = new List<string>();
        var builder = Builder();
        Assert.Throws<InvalidOperationException>(() => Configure(builder, configuration, read));
        Assert.DoesNotContain(CreationCredential, read);
        AssertNoCreationPorts(builder.Services);
    }

    [Fact]
    public void EnabledCreationRequiresTheExactConfiguredListenerBeforeCreatorResolution()
    {
        var configuration = EnabledCreation();
        var read = new List<string>();
        var builder = Builder();
        Assert.Throws<InvalidOperationException>(() => CatalogHostConfiguration.Configure(builder,
            new IPEndPoint(IPAddress.Loopback, 4101), variable =>
            {
                read.Add(variable);
                return configuration.GetValueOrDefault(variable);
            }));
        Assert.DoesNotContain(CreationCredential, read);
        AssertNoCreationPorts(builder.Services);
    }

    [Fact]
    public async Task EnabledCreationUsesOneLazyCreatorForBothPortsWithoutWorkerStoreOrOtherMutations()
    {
        var builder = Builder();
        var read = new List<string>();
        Configure(builder, EnabledCreation(), read);
        Assert.True(Options(builder.Services).Enabled);
        foreach (var type in new[] { typeof(PostgresProjectCreationDatabase), typeof(PostgresProjectCreator),
                     typeof(IOrganizationProjectCreationReader), typeof(IProjectCreator) })
        {
            var descriptor = Assert.Single(builder.Services, item => item.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.NotNull(descriptor.ImplementationFactory);
            Assert.Null(descriptor.ImplementationInstance);
        }
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IRevisionIngestionService) ||
            descriptor.ServiceType == typeof(IPrivateObjectStore) ||
            descriptor.ServiceType == typeof(IMappingMetadataWriter));
        Assert.DoesNotContain("WEFT_PRIVATE_STORAGE_ROOT", read);
        Assert.DoesNotContain("WEFT_NATIVE_BYTE_LIMIT", read);
        Assert.DoesNotContain("WEFT_POSTGRES_INGESTION_CONNECTION_STRING", read);
        await using var provider = builder.Services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IOrganizationProjectCreationReader>();
        var creator = provider.GetRequiredService<IProjectCreator>();
        Assert.IsType<PostgresProjectCreator>(creator);
        Assert.Same(reader, creator);
        Assert.Same(creator, provider.GetRequiredService<PostgresProjectCreator>());
        Assert.Same(creator, provider.GetRequiredService<IProjectCreator>());
        Assert.Same(provider.GetRequiredService<PostgresProjectCreationDatabase>(),
            provider.GetRequiredService<PostgresProjectCreationDatabase>());
    }

    [Fact]
    public async Task ReaderAndEditorResolutionNeverParsesEnabledButUnusedCreatorConfiguration()
    {
        var configuration = EnabledCreation();
        configuration[CreationCredential] = "UnsupportedSyntheticSecretKey=" + SyntheticSecret;
        configuration["WEFT_METADATA_EDITS_ENABLED"] = "true";
        configuration["WEFT_POSTGRES_METADATA_CONNECTION_STRING"] =
            "Host=127.0.0.1;Database=weft;Username=weft_catalog_editor;Password=synthetic-never-connected";
        var builder = Builder();
        Configure(builder, configuration);
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
        Assert.IsType<PostgresMappingMetadataWriter>(provider.GetRequiredService<IMappingMetadataWriter>());
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IProjectCreator>());
        Assert.Equal("The project creation connection configuration is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SyntheticSecret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("UnsupportedSyntheticSecretKey", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("No Reset On Close=true")]
    [InlineData("Multiplexing=true")]
    [InlineData("Enlist=true")]
    public async Task UnsafeCreatorPoolSettingsFailOnlyAtLazyResolutionWithoutCredentialDetails(string unsafeSetting)
    {
        var configuration = EnabledCreation();
        configuration[CreationCredential] = CreatorConnection + ";" + unsafeSetting;
        var builder = Builder();
        Configure(builder, configuration);
        await using var provider = builder.Services.BuildServiceProvider();
        Assert.IsType<PostgresCatalogReader>(provider.GetRequiredService<ICatalogReader>());
        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IOrganizationProjectCreationReader>());
        Assert.Equal("The project creation connection configuration is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SyntheticSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryLegacyApiRegistrationOverloadKeepsProjectCreationOff(int overload)
    {
        var services = new ServiceCollection();
        var environment = Builder().Environment;
        var catalog = new CatalogApiOptions();
        if (overload == 0)
            services.AddWeftCatalog(catalog, environment);
        else if (overload == 1)
            services.AddWeftCatalog(catalog, environment, new CatalogMetadataEditOptions());
        else
            services.AddWeftCatalog(catalog, environment, new CatalogMetadataEditOptions(),
                new CatalogRevisionIngestionOptions());
        Assert.False(Options(services).Enabled);
        AssertNoCreationPorts(services);
    }

    [Fact]
    public void NewApiOverloadRejectsMissingCreationOptionsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddWeftCatalog(new CatalogApiOptions(),
            Builder().Environment, new CatalogMetadataEditOptions(), new CatalogRevisionIngestionOptions(), null!));
        Assert.Empty(services);
    }

    private static CatalogProjectCreationOptions Options(IServiceCollection services) =>
        Assert.IsType<CatalogProjectCreationOptions>(Assert.Single(services,
            descriptor => descriptor.ServiceType == typeof(CatalogProjectCreationOptions)).ImplementationInstance);

    private static void AssertNoCreationPorts(IServiceCollection services) =>
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(PostgresProjectCreationDatabase) ||
            descriptor.ServiceType == typeof(PostgresProjectCreator) || descriptor.ServiceType == typeof(IProjectCreator) ||
            descriptor.ServiceType == typeof(IOrganizationProjectCreationReader));

    private static void Configure(WebApplicationBuilder builder, Dictionary<string, string> configuration,
        List<string>? read = null) => CatalogHostConfiguration.Configure(builder, Listener(), variable =>
        {
            read?.Add(variable);
            return configuration.GetValueOrDefault(variable);
        });

    private static IPEndPoint Listener() => new(IPAddress.Loopback, 4100);

    private static WebApplicationBuilder Builder(string environment = "Development") =>
        WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });

    private static Dictionary<string, string> EnabledCatalog() => new()
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
        ["WEFT_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
        ["WEFT_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
        ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["WEFT_CATALOG_CONNECTION_STRING"] =
            "Host=127.0.0.1;Database=weft;Username=weft_catalog_reader;Password=synthetic-never-connected"
    };

    private static Dictionary<string, string> EnabledCreation()
    {
        var configuration = EnabledCatalog();
        configuration[CreationFlag] = "true";
        configuration[CreationCredential] = CreatorConnection;
        return configuration;
    }
}
