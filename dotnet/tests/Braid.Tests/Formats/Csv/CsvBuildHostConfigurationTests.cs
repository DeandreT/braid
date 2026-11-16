using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Deployment;
using Braid.MappingBackends;
using Braid.MappingBackends.AzureSessions;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Server;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;

namespace Braid.Tests;

public sealed class CsvBuildHostConfigurationTests
{
    private const string Native = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Toolchain = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData(FerruleRunnerKind.Service, "valid")]
    [InlineData(FerruleRunnerKind.Service, "wrongNative")]
    [InlineData(FerruleRunnerKind.Service, "wrongToolchain")]
    [InlineData(FerruleRunnerKind.AzureContainerAppsSessions, "valid")]
    [InlineData(FerruleRunnerKind.AzureContainerAppsSessions, "wrongNative")]
    [InlineData(FerruleRunnerKind.AzureContainerAppsSessions, "wrongToolchain")]
    public async Task Remote_csv_dispatch_uses_the_real_compile_runner_and_checks_both_peer_pins(FerruleRunnerKind kind, string peer)
    {
        var settings = RemoteSettings(kind);
        var connector = new CompileConnector(peer == "wrongNative" ? "sha256:" + new string('d', 64) : Native,
            peer == "wrongToolchain" ? "sha256:" + new string('e', 64) : Toolchain);
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IFerruleServiceConnector>(nameof(IFerruleServiceConnector), (_, _) =>
        {
            resolutions++;
            return connector;
        });
        await using (var provider = services.BuildServiceProvider())
        {
            await using var runner = new CsvBuildHostRunner(settings, provider);
            Assert.Equal(Native, runner.BuildIdentity);
            Assert.Equal(CompileWorkerIdentity.Combine(Native, Toolchain), runner.CompilationBuildIdentity);
            Assert.Equal(0, resolutions);
            Assert.Equal(0, connector.Opens);
            byte[] frame = [1, 2, 3, 4];
            // CsvBuildHostRunner constructs the actual FerruleServiceRunner here;
            // the owned fake connector replaces only its HTTP channel, not its pin checks.
            var result = await runner.RunAsync(frame, default);
            Assert.Equal(1, resolutions);
            Assert.Equal(1, connector.ReadyCalls);
            if (peer == "valid")
            {
                Assert.Equal(WorkerProcessFailure.None, result.Failure);
                Assert.Equal(frame, result.Output);
                Assert.Equal(frame, Assert.Single(connector.Frames));
                Assert.Equal(Native, Assert.Single(connector.NativeHeaders));
                Assert.Equal(Toolchain, Assert.Single(connector.ToolchainHeaders));
            }
            else
            {
                Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
                Assert.Empty(connector.Frames);
                Assert.Empty(result.Output);
            }
            Assert.Equal(1, connector.ClosedChannels);
            await runner.StopAsync();
            Assert.False(runner.CanExecute);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(frame, default));
            Assert.Equal(1, connector.Opens);
            Assert.Equal(0, connector.Disposals); // Borrowed by the runner; owned by DI.
        }
        Assert.Equal(1, connector.Disposals);
    }

    [Theory]
    [InlineData(FerruleRunnerKind.Service)]
    [InlineData(FerruleRunnerKind.AzureContainerAppsSessions)]
    public async Task Stopping_an_unused_remote_csv_runner_does_not_resolve_a_connector(FerruleRunnerKind kind)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IFerruleServiceConnector>(nameof(IFerruleServiceConnector), (_, _) =>
            throw new InvalidOperationException("An unused runner must not open its connector."));
        await using var provider = services.BuildServiceProvider();
        await using var runner = new CsvBuildHostRunner(RemoteSettings(kind), provider);
        Assert.True(runner.CanExecute);
        await runner.StopAsync();
        await runner.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new byte[] { 1 }, default));
    }

    private static CsvBuildHostSettings RemoteSettings(FerruleRunnerKind kind)
    {
        var values = Settings();
        if (kind == FerruleRunnerKind.AzureContainerAppsSessions)
        {
            values[FerruleRunnerSelection.Setting] = "azureContainerAppsSessions";
            foreach (var name in new[] { FerruleServiceEndpoint.Url, FerruleServiceEndpoint.CertificatePin,
                FerruleServiceEndpoint.ClientCertificateFile, FerruleServiceEndpoint.ClientPrivateKeyFile }) values.Remove(name);
            values[AzureSessionsRunnerSettings.PoolEndpoint] = "https://sessions.invalid";
        }
        return CsvBuildHostSettings.Read(values.GetValueOrDefault, session: true, expectedProfileIdentity: CompileWorkerIdentity.Combine(Native, Toolchain));
    }

    private sealed class CompileConnector(string native, string toolchain) : IFerruleServiceConnector, IDisposable
    {
        internal int Opens;
        internal int ReadyCalls;
        internal int ClosedChannels;
        internal int Disposals;
        internal List<byte[]> Frames { get; } = [];
        internal List<string> NativeHeaders { get; } = [];
        internal List<string> ToolchainHeaders { get; } = [];

        public IFerruleServiceChannel Open()
        {
            Interlocked.Increment(ref Opens);
            return new Channel(this, native, toolchain);
        }

        public void Dispose() => Interlocked.Increment(ref Disposals);

        private sealed class Channel(CompileConnector owner, string native, string toolchain) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.NoContent : HttpStatusCode.OK);
                response.Headers.Add(FerruleServiceProtocol.ProtocolHeader, FerruleServiceProtocol.Version);
                response.Headers.Add(InspectionTransport.ImageHeader, native);
                response.Headers.Add(FerruleServiceProtocol.CompileToolchainHeader, toolchain);
                if (request.Method == HttpMethod.Get)
                {
                    Assert.Equal(FerruleServiceProtocol.ReadyPath, request.RequestUri!.OriginalString);
                    Interlocked.Increment(ref owner.ReadyCalls);
                }
                else
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.StartsWith(FerruleServiceProtocol.OperationsPath + FerruleServiceProtocol.CompileProfile + "/", request.RequestUri!.OriginalString);
                    var frame = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    owner.Frames.Add(frame);
                    owner.NativeHeaders.Add(Assert.Single(request.Headers.GetValues(InspectionTransport.ImageHeader)));
                    owner.ToolchainHeaders.Add(Assert.Single(request.Headers.GetValues(FerruleServiceProtocol.CompileToolchainHeader)));
                    response.Headers.Add(InspectionTransport.ResultHeader, "none");
                    response.Content = new ByteArrayContent(frame);
                    response.Content.Headers.ContentLength = frame.Length;
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
                }
                return response;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.ClosedChannels);
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Enabled_csv_composition_is_lazy_and_preserves_the_combined_compiler_identity()
    {
        var values = Settings();
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, true);
        Assert.Equal(CompileWorkerIdentity.Combine(Native, Toolchain), runtime.Image);
        var services = new ServiceCollection();
        CatalogBuildHostConfiguration.Configure(services, new Environment(), name => values.GetValueOrDefault(name) ??
            (name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal) ? "Host=127.0.0.1;Database=braid;Username=fixture;Password=configuration-only" : null), runtime);
        await using var provider = services.BuildServiceProvider();
        // The deliberately nonexistent PEM paths must not be opened by capabilities, registration or shutdown.
        var options = provider.GetRequiredService<CatalogBuildOptions>();
        Assert.Equal(FerruleBuildCompiler.CsvProfile, options.CsvProfile);
        Assert.Equal(FerrulePreviewBackend.CsvAdapterBuild, options.CsvBackend!.AdapterVersion);
        Assert.Equal(runtime.Image, options.CsvBackend.EngineBuild);
        Assert.Same(provider.GetRequiredKeyedService<IRevisionReportMetadataReader>("build-http"), provider.GetRequiredService<IRevisionReportMetadataReader>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPrivateObjectStore) && !descriptor.IsKeyedService);
        Assert.NotNull(provider.GetRequiredService<ICsvBuildBoundaryReader>());
        var runner = provider.GetRequiredService<CsvBuildHostRunner>();
        Assert.Equal(Native, runner.BuildIdentity);
        Assert.Equal(runtime.Image, runner.CompilationBuildIdentity);
        await runner.StopAsync();
        Assert.False(runner.CanExecute);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new byte[] { 1 }, default));
    }

    [Fact]
    public async Task Native_alias_reuses_the_qualified_keyed_store()
    {
        var values = Settings();
        var services = new ServiceCollection();
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, true);
        CatalogBuildHostConfiguration.Configure(services, new Environment(), name => values.GetValueOrDefault(name) ??
            (name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal) ? "Host=127.0.0.1;Database=braid;Username=fixture;Password=configuration-only" : null), runtime);
        await using var storage = new LazyPrivateObjectStore(new FileSystemStorageOptions(
            "/configuration-only/keyed-native-store", "/configuration-only/application"));
        services.AddKeyedSingleton("build-http", storage);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(storage, provider.GetRequiredService<IPrivateObjectStore>());
        Assert.False(Directory.Exists(storage.Configuration.FileSystem!.RootPath));
    }

    [Fact]
    public async Task Existing_reporting_ports_are_not_replaced_by_build_aliases()
    {
        var values = Settings();
        var services = new ServiceCollection();
        await using var database = new PostgresRevisionReportDatabase("Host=127.0.0.1;Database=braid;Username=fixture;Password=configuration-only");
        var metadata = new PostgresRevisionReportMetadataReader(database);
        await using var storage = new LazyPrivateObjectStore(new FileSystemStorageOptions(
            "/configuration-only/existing-report-store", "/configuration-only/application"));
        services.AddSingleton<IRevisionReportMetadataReader>(metadata);
        services.AddSingleton<IPrivateObjectStore>(storage);
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, true);
        CatalogBuildHostConfiguration.Configure(services, new Environment(), name => values.GetValueOrDefault(name) ??
            (name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal) ? "Host=127.0.0.1;Database=braid;Username=fixture;Password=configuration-only" : null), runtime);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(metadata, provider.GetRequiredService<IRevisionReportMetadataReader>());
        Assert.Same(storage, provider.GetRequiredService<IPrivateObjectStore>());
        Assert.False(Directory.Exists(storage.Configuration.FileSystem!.RootPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Unset_csv_does_not_register_or_advertise_csv(string? flag)
    {
        var values = Settings();
        values.Remove(CsvBuildHostSettings.EnabledSetting);
        if (flag is not null) values[CsvBuildHostSettings.EnabledSetting] = flag;
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, true);
        Assert.Null(runtime.Csv);
        var services = new ServiceCollection();
        CatalogBuildHostConfiguration.Configure(services, new Environment(), name => values.GetValueOrDefault(name) ??
            (name.EndsWith("_CONNECTION_STRING", StringComparison.Ordinal) ? "Host=127.0.0.1;Database=braid;Username=fixture;Password=configuration-only" : null), runtime);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ICsvBuildBoundaryReader));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(CsvBuildHostRunner));
        Assert.DoesNotContain(services, descriptor => !descriptor.IsKeyedService &&
            (descriptor.ServiceType == typeof(IRevisionReportMetadataReader) || descriptor.ServiceType == typeof(IPrivateObjectStore)));
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetRequiredService<CatalogBuildOptions>().CsvProfile);
    }

    [Theory]
    [InlineData("toolchain")]
    [InlineData("worker")]
    [InlineData("flag")]
    [InlineData("mixed")]
    public void Unsupported_or_unpinned_csv_settings_refuse_before_private_delivery(string failure)
    {
        var values = Settings();
        if (failure == "toolchain") values.Remove(FerruleRunnerSelection.CompileToolchainSetting);
        if (failure == "worker")
        {
            values[FerruleRunnerSelection.Setting] = "worker";
            values.Remove(FerruleRunnerSelection.CompileToolchainSetting);
        }
        if (failure == "flag") values[CsvBuildHostSettings.EnabledSetting] = "yes";
        if (failure == "mixed") values["BRAID_FERRULE_SESSIONS_POOL_ENDPOINT"] = "https://sessions.invalid/pool";
        Assert.ThrowsAny<Exception>(() => CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, true));
    }

    [Fact]
    public void Csv_cannot_silently_replace_the_configured_compiler_identity()
    {
        var values = Settings();
        Assert.Throws<ArgumentException>(() => CsvBuildHostSettings.Read(values.GetValueOrDefault, session: true, expectedProfileIdentity: Native));
        var selected = CsvBuildHostSettings.Read(values.GetValueOrDefault, session: true, expectedProfileIdentity: CompileWorkerIdentity.Combine(Native, Toolchain));
        Assert.Equal(Native, selected.NativeIdentity);
        Assert.Equal(Toolchain, selected.Toolchain);
    }

    private static Dictionary<string, string> Settings() => new()
    {
        ["BRAID_BUILDS_ENABLED"] = "true",
        [CsvBuildHostSettings.EnabledSetting] = "true",
        ["BRAID_FERRULE_WORKER_IMAGE"] = Native,
        [FerruleRunnerSelection.Setting] = "service",
        [FerruleRunnerSelection.CompileToolchainSetting] = Toolchain,
        [FerruleServiceEndpoint.Url] = "https://ferrule.invalid",
        [FerruleServiceEndpoint.CertificatePin] = new string('c', 64),
        [FerruleServiceEndpoint.ClientCertificateFile] = Path.GetFullPath("nonexistent-csv-client.pem"),
        [FerruleServiceEndpoint.ClientPrivateKeyFile] = Path.GetFullPath("nonexistent-csv-client.key"),
        ["BRAID_PRIVATE_STORAGE_ROOT"] = "/configuration-only/csv-store"
    };

    private sealed class Environment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Braid.Server";
        public string WebRootPath { get; set; } = Path.GetFullPath("wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetFullPath(".");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
