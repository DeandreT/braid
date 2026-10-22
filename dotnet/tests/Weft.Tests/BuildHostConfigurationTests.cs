using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Weft.Application;
using Weft.Deployment;
using Weft.Server;
using Weft.Storage.Hosting;

namespace Weft.Tests;

public sealed class BuildHostConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Disabled_build_api_reads_no_runtime_storage_or_credential_selection(string? flag)
    {
        var requested = new List<string>();
        string? Read(string name)
        {
            requested.Add(name);
            if (name != "WEFT_BUILDS_ENABLED") throw new InvalidOperationException("Unexpected disabled dependency read.");
            return flag;
        }
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(Read, false);
        var services = new ServiceCollection();
        CatalogBuildHostConfiguration.Configure(services, new Environment(), Read, runtime);
        Assert.Empty(services); Assert.Equal(["WEFT_BUILDS_ENABLED"], requested);
    }

    [Fact]
    public void Catalog_and_runtime_scalars_are_checked_without_opening_credential_deliveries()
    {
        Assert.Throws<ArgumentException>(() => CatalogBuildHostConfiguration.ReadRuntime(_ => "true", false));
        var requested = new List<string>();
        Assert.Throws<ArgumentException>(() => CatalogBuildHostConfiguration.ReadRuntime(name =>
        {
            requested.Add(name);
            return name == "WEFT_BUILDS_ENABLED" ? "true" : "not-an-immutable-image";
        }, true));
        Assert.DoesNotContain(requested, name => name.Contains("CONNECTION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Historical_recovery_composition_does_not_resolve_active_admission_private_storage_or_report_workers()
    {
        var services = new ServiceCollection();
        var runtime = new BuildHostRuntime(true, "sha256:" + new string('a', 64), PrivateStorageSettings.ForFileSystem("/private/build-reports"));
        CatalogBuildHostConfiguration.Configure(services, new Environment(), name => name == "WEFT_CATALOG_AUTH_MODE" ? "development" :
            "Host=127.0.0.1;Database=weft;Username=fixture;Password=configuration-only", runtime);
        services.AddKeyedSingleton<BuildControlService>("build-http", (_, _) => throw new InvalidOperationException("Unexpected active admission resolution."));
        services.AddKeyedSingleton<IRevisionReportReader>("build-http", (_, _) => throw new InvalidOperationException("Unexpected current report resolution."));
        services.AddKeyedSingleton<LazyPrivateObjectStore>("build-http", (_, _) => throw new InvalidOperationException("Unexpected private storage resolution."));
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(IBuildRunStore));
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(IBuildRunQueue));
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(IBuildCompiler));
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(IBuildPublicationCatalog));
        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<BuildApprovalRecoveryService>());
        Assert.NotNull(provider.GetRequiredService<BuildSubmissionService>());
        Assert.NotNull(provider.GetRequiredService<BuildAdministrationService>());
    }

    private sealed class Environment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Weft.Server";
        public string WebRootPath { get; set; } = "/application/wwwroot";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "/application";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
