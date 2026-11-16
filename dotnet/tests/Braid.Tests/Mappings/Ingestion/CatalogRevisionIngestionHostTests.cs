using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Ingestion;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Server;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogRevisionIngestionHostTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MfdEnablementUsesOnlyTheSeparateRealRunnerWithoutEagerDatabaseAccess()
    {
        using var root = new PrivateStorageTestRoot();
        var builder = Builder(root.ApplicationRoot);
        var values = Enabled(root);
        values["BRAID_MFD_INTERCHANGE_ENABLED"] = "true";
        values["BRAID_REVISION_REPORTS_ENABLED"] = "true";
        values["BRAID_POSTGRES_REPORT_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=braid_report_reader;Timeout=1";
        Configure(builder, values);
        var launcher = new NoLaunchWorkerProcessLauncher();
        InspectionWorkerHostConfiguration.Configure(builder.Services, new("/usr/bin/docker", "sha256:" + new string('a', 64)), launcher);
        await using var services = builder.Services.BuildServiceProvider();
        Assert.True(services.GetRequiredService<CatalogInterchangeOptions>().Enabled);
        Assert.True(services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(InterchangeImportService)));
        Assert.True(services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(InterchangeExportService)));
        Assert.NotNull(services.GetRequiredService<IInterchangeBackendRegistry>().GetCurrent("ferrule"));
        Assert.NotSame(services.GetRequiredService<IInspectionWorkerRunner>(),
            services.GetRequiredKeyedService<IInspectionWorkerRunner>("interchange"));
        Assert.Equal(0, launcher.Calls);
    }

    [Fact]
    public void DefaultsStayClosedAndUnusedIngestionConfigurationDoesNotResolveDependencies()
    {
        var builder = Builder();
        var values = new Dictionary<string, string>
        {
            ["BRAID_POSTGRES_INGESTION_CONNECTION_STRING"] = "unused invalid credentials",
            ["BRAID_PRIVATE_STORAGE_ROOT"] = "unused relative root",
            ["BRAID_NATIVE_BYTE_LIMIT"] = "unused malformed limit"
        };
        Configure(builder, values);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IRevisionIngestionService) ||
            descriptor.ServiceType == typeof(IPrivateObjectStore) || descriptor.ServiceType == typeof(PostgresIngestionDatabase));
        var options = Assert.IsType<CatalogRevisionIngestionOptions>(Assert.Single(builder.Services,
            descriptor => descriptor.ServiceType == typeof(CatalogRevisionIngestionOptions)).ImplementationInstance);
        Assert.False(options.Enabled);
        Assert.Equal(2, options.MaximumConcurrentOperations);
    }

    [Fact]
    public void IngestionCannotEnableClosedCatalogOrFallBackToReaderOrEditorCredentials()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(), new() { ["BRAID_REVISION_INGESTION_ENABLED"] = "true" }));
        using var root = new PrivateStorageTestRoot();
        var values = Enabled(root);
        values.Remove("BRAID_POSTGRES_INGESTION_CONNECTION_STRING");
        values["BRAID_POSTGRES_METADATA_CONNECTION_STRING"] = "synthetic unused editor credentials";
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(root.ApplicationRoot), values));
        values = Enabled(root);
        values.Remove("BRAID_PRIVATE_STORAGE_ROOT");
        Assert.Throws<InvalidOperationException>(() => Configure(Builder(root.ApplicationRoot), values));
    }

    [Theory]
    [InlineData("BRAID_REVISION_INGESTION_ENABLED", "1")]
    [InlineData("BRAID_REVISION_INGESTION_ENABLED", "True")]
    [InlineData("BRAID_REVISION_INGESTION_ENABLED", "")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", "0")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", "01")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", "+1")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", " 1")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", "1.0")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", "4194305")]
    [InlineData("BRAID_NATIVE_BYTE_LIMIT", "999999999999999999999")]
    [InlineData("BRAID_PRIVATE_STORAGE_ROOT", "relative/root")]
    [InlineData("BRAID_PRIVATE_STORAGE_ROOT", "/tmp/alias/../store")]
    public void MalformedExplicitIngestionConfigurationNeverBecomesAFallback(string variable, string value)
    {
        using var root = new PrivateStorageTestRoot();
        var values = Enabled(root);
        values[variable] = value;
        Assert.ThrowsAny<ArgumentException>(() => Configure(Builder(root.ApplicationRoot), values));
        Assert.False(Directory.Exists(root.RootPath));
    }

    [Fact]
    public async Task ResolutionUsesConcreteProjectReaderAndActualConfiguredRunnerProfileWithoutLaunchingAWorker()
    {
        using var root = new PrivateStorageTestRoot();
        var builder = Builder(root.ApplicationRoot);
        Configure(builder, Enabled(root));
        var launcher = new NoLaunchWorkerProcessLauncher();
        // Synthetic inert image ID tests composition only, never native qualification.
        var runner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", "sha256:" + new string('a', 64)), launcher);
        builder.Services.AddSingleton<IInspectionWorkerRunner>(runner);
        builder.Services.Replace(ServiceDescriptor.Singleton<ICatalogReader>(new NonProjectCatalogReader()));
        await using var app = builder.Build();
        var services = app.Services;
        Assert.IsType<PostgresCatalogReader>(services.GetRequiredService<IProjectReader>());
        Assert.IsType<NonProjectCatalogReader>(services.GetRequiredService<ICatalogReader>());
        Assert.IsType<RevisionIngestionService>(services.GetRequiredService<IRevisionIngestionService>());
        var registration = services.GetRequiredService<IIngestionBackendRegistry>().GetCurrent("ferrule")!;
        Assert.Equal(runner.BuildIdentity, registration.Profile.BackendBuild);
        Assert.IsType<FerruleIngestionAdmissionBackend>(registration.Backend);
        Assert.Equal(0, launcher.Calls);
        Assert.False(Directory.Exists(root.RootPath));
    }

    [Fact]
    public async Task CurrentAdmissionReductionPreservesTheActualHardStorageReferenceReadLimit()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(257);
        var reference = PrivateStorageData.Reference(bytes);
        using (var initialized = root.Initialize())
        using (var source = new MemoryStream(bytes))
            Assert.Equal(PrivateObjectPublishResult.Created, await initialized.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));
        var activation = await FileSystemV2PrivateObjectStore.ActivateAsync(root.Options, offlineAcknowledged: true);
        Assert.Equal(PrivateStorageV2ActivationOutcome.Activated, activation.Outcome);
        var values = Enabled(root);
        values["BRAID_NATIVE_BYTE_LIMIT"] = "1";
        var builder = Builder(root.ApplicationRoot);
        Configure(builder, values);
        await using var app = builder.Build();
        var services = app.Services;
        Assert.Equal(1, services.GetRequiredService<RevisionIngestionOptions>().NativeByteLimit);
        var registration = services.GetRequiredService<IngestionPrivateStoreRegistration>();
        var storage = services.GetRequiredService<PrivateStorageConfiguration>();
        Assert.Equal(FileSystemStorageOptions.NativeHardMaximum, storage.NativeByteLimit);
        Assert.Equal(storage.NativeByteLimit, registration.NativeByteLimit);
        Assert.Equal(bytes, await registration.Store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Null(services.GetRequiredService<IIngestionBackendRegistry>().GetCurrent("ferrule"));
    }

    [PostgresFact]
    public async Task ActualOwnedReceiptAndTerminalReplayResolveWithNoWorkerAndAnUnopenablePhysicalRoot()
    {
        // Explicit synthetic C1 admission fixture qualifies host composition against
        // real PostgreSQL, not the native worker or a valid native mapping document.
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var original = await test.CommitCreateAsync();
        using var root = new PrivateStorageTestRoot();
        var values = Enabled(root);
        values["BRAID_CATALOG_CONNECTION_STRING"] = test.Case.CatalogConnectionString;
        values["BRAID_POSTGRES_INGESTION_CONNECTION_STRING"] = test.Case.IngestionConnectionString;
        values["BRAID_DEV_PRINCIPAL_ID"] = test.Seed.Alice.ToString("D");
        values["BRAID_DEV_ORGANIZATION_ID"] = test.Seed.OrganizationA.ToString("D");
        values["BRAID_PRIVATE_STORAGE_ROOT"] = "/proc/braid-host-must-not-open-" + Guid.NewGuid().ToString("N");
        values["BRAID_NATIVE_BYTE_LIMIT"] = "1";
        var builder = Builder(root.ApplicationRoot);
        Configure(builder, values);
        builder.Services.Replace(ServiceDescriptor.Singleton<ICatalogReader>(new NonProjectCatalogReader()));
        await using var app = builder.Build();
        var services = app.Services;
        var service = services.GetRequiredService<IRevisionIngestionService>();
        Assert.Null(services.GetRequiredService<IIngestionBackendRegistry>().GetCurrent("ferrule"));
        var receipt = await service.GetOwnReceiptAsync(test.Actor, test.Project, original.Facts.OperationId);
        Assert.Equal(RevisionIngestionReceipt.FromMetadata(original), receipt);
        var intent = original.Facts.Intent;
        using var source = new MemoryStream(PostgresIngestionTestSupport.SyntheticBody);
        var replay = await service.CreateAsync(test.Actor, test.Project, original.Facts.IdempotencyKey,
            new(intent.CreateMetadata!, intent.BackendId, intent.OriginalFilename), source);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, replay.Outcome);
        Assert.Equal(receipt, replay.Receipt);
        Assert.True(source.CanRead);
    }

    private static WebApplicationBuilder Builder(string? root = null) => WebApplication.CreateBuilder(new WebApplicationOptions
    { EnvironmentName = "Development", Args = [], ContentRootPath = root });

    private static void Configure(WebApplicationBuilder builder, Dictionary<string, string> values) =>
        CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, 4100), key => values.GetValueOrDefault(key));

    private static Dictionary<string, string> Enabled(PrivateStorageTestRoot root) => new()
    {
        ["BRAID_CATALOG_ENABLED"] = "true",
        ["BRAID_DEV_IDENTITY_ENABLED"] = "true",
        ["BRAID_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
        ["BRAID_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
        ["BRAID_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["BRAID_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=braid_catalog_reader;Timeout=1",
        ["BRAID_REVISION_INGESTION_ENABLED"] = "true",
        ["BRAID_POSTGRES_INGESTION_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=braid_ingest;Timeout=1",
        ["BRAID_PRIVATE_STORAGE_ROOT"] = root.RootPath
    };

    private sealed class NoLaunchWorkerProcessLauncher : IWorkerProcessLauncher
    {
        internal int Calls { get; private set; }
        public IWorkerProcess Start(ProcessStartInfo startInfo)
        { Calls++; throw new InvalidOperationException("Worker invocation is outside this composition test."); }
    }

    private sealed class NonProjectCatalogReader : ICatalogReader
    {
        public Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit, CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit, CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit, CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
