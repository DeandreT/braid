using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Domain;
using Weft.Server;
using Weft.Storage.Blob;
using Weft.Storage.Hosting;

namespace Weft.Tests;

/// <summary>Host and operator-tool selection of the Blob store against the owned emulator.</summary>
[SupportedOSPlatform("linux")]
[Collection(AzuriteCollection.Name)]
public sealed class BlobPrivateStorageHostTests(AzuriteFixture azurite)
{
    [AzuriteTheory]
    [InlineData("ingestion")]
    [InlineData("report-read")]
    [InlineData("both")]
    public async Task TheServerSelectsTheBlobStoreExplicitlyAndReadsWithoutResolvingAPublicationCredential(string mode)
    {
        var container = await azurite.CreateContainerAsync();
        var bytes = PrivateStorageData.Pattern(101);
        var reference = PrivateStorageData.Reference(bytes);
        await using (var initialized = await BlobPrivateObjectStore.InitializeAsync(azurite.Options(container), azurite.Client(container),
            () => new PrivateStorageV2PhysicalRegistry(), null))
            await initialized.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        var values = Catalog(mode);
        foreach (var (name, value) in Storage(container.Name)) values[name] = value;
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, 4100), values.GetValueOrDefault);
        await using var app = builder.Build();
        var configuration = app.Services.GetRequiredService<PrivateStorageConfiguration>();
        Assert.Equal(PrivateStorageKind.AzureBlob, configuration.Kind);
        Assert.Equal(container.Name, configuration.Blob!.ContainerName);
        var store = app.Services.GetRequiredService<IPrivateObjectStore>();
        Assert.IsType<LazyPrivateObjectStore>(store);
        Assert.Same(store, app.Services.GetRequiredService<IPrivateObjectPublicationStore>());
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));
        Assert.Equal(0, source.ReadCalls);
    }

    [AzuriteFact]
    public async Task AnUninitializedContainerFailsClosedAtFirstUseAndIsNeverInitializedByTheServer()
    {
        var container = await azurite.CreateContainerAsync();
        var values = Catalog("both");
        foreach (var (name, value) in Storage(container.Name)) values[name] = value;
        var builder = Builder();
        CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, 4100), values.GetValueOrDefault);
        await using var app = builder.Build();
        var reference = PrivateStorageData.Reference("never initialized"u8.ToArray());
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () =>
            app.Services.GetRequiredService<IPrivateObjectStore>().ReadVerifiedAsync(reference.Id.Scope, reference));
        await foreach (var _ in container.GetBlobsAsync(new GetBlobsOptions())) Assert.Fail("The server must not write to an uninitialized container.");
    }

    [Theory]
    [InlineData("WEFT_PRIVATE_STORAGE_KIND", "azure", typeof(ArgumentException))]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", null, typeof(InvalidOperationException))]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL", "connection-string", typeof(ArgumentException))]
    [InlineData("WEFT_PRIVATE_STORAGE_ROOT", "/var/lib/weft/private", typeof(ArgumentException))]
    [InlineData("WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT", "http://10.1.2.3:10000/devstoreaccount1", typeof(ArgumentException))]
    public void IngestionRefusesMisconfiguredBlobStorageAtStartup(string name, string? value, Type expected)
    {
        var values = Catalog("ingestion");
        foreach (var (setting, text) in Storage("weft-private")) values[setting] = text;
        if (value is null) values.Remove(name);
        else values[name] = value;
        var error = Record.Exception(() => CatalogHostConfiguration.Configure(Builder(), new(IPAddress.Loopback, 4100), values.GetValueOrDefault));
        Assert.IsAssignableFrom(expected, error);
    }

    [Fact]
    public void BuildRuntimeReadsTheSameExplicitSelection()
    {
        var values = new Dictionary<string, string>
        {
            ["WEFT_BUILDS_ENABLED"] = "true",
            ["WEFT_FERRULE_WORKER_IMAGE"] = "sha256:" + new string('a', 64)
        };
        foreach (var (name, value) in Storage("weft-private")) values[name] = value;
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, catalogEnabled: true);
        Assert.Equal(PrivateStorageKind.AzureBlob, runtime.Storage!.Kind);
        values.Remove("WEFT_PRIVATE_STORAGE_BLOB_CONTAINER");
        Assert.Throws<ArgumentException>(() => CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, catalogEnabled: true));
        values.Remove("WEFT_PRIVATE_STORAGE_KIND");
        Assert.Throws<ArgumentException>(() => CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, catalogEnabled: true));
    }

    [AzuriteFact]
    public async Task TheStorageToolInitializesOnlyAnExplicitEmptyPrivateContainerOnce()
    {
        var container = await azurite.CreateContainerAsync();
        var environment = Storage(container.Name);
        var result = await RunStorageToolAsync(environment, "initialize-blob");
        Assert.Equal((0, "InitializedBlob\n", ""), (result.ExitCode, result.Output, result.Error));
        await using (await BlobPrivateObjectStore.OpenAsync(azurite.Options(container), azurite.Client(container), null, null)) { }
        var repeat = await RunStorageToolAsync(environment, "initialize-blob");
        Assert.Equal((1, "", "UnsafeStorage\n"), (repeat.ExitCode, repeat.Output, repeat.Error));

        var missing = Storage("weft-" + Guid.NewGuid().ToString("N"));
        var absent = await RunStorageToolAsync(missing, "initialize-blob");
        Assert.Equal((1, "UnsafeStorage\n"), (absent.ExitCode, absent.Error));
        foreach (var (changed, arguments) in new (Dictionary<string, string>, string[])[]
        {
            (Without(environment, "WEFT_PRIVATE_STORAGE_KIND"), ["initialize-blob"]),
            (Without(environment, "WEFT_PRIVATE_STORAGE_BLOB_CONTAINER"), ["initialize-blob"]),
            (With(environment, "WEFT_PRIVATE_STORAGE_ROOT", "/var/lib/weft/private"), ["initialize-blob"]),
            (environment, ["initialize-blob", "extra"])
        })
        {
            var refused = await RunStorageToolAsync(changed, arguments);
            Assert.Equal(2, refused.ExitCode);
            Assert.Equal("", refused.Output);
            Assert.DoesNotContain(container.Name, refused.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(azurite.ServiceUri.Authority, refused.Error, StringComparison.Ordinal);
        }
    }

    private Dictionary<string, string> Storage(string container) => new()
    {
        ["WEFT_PRIVATE_STORAGE_KIND"] = "azure-blob",
        // Composition-only cases need a well-formed loopback endpoint, not a running emulator.
        ["WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT"] = (AzuriteFixture.IsConfigured ? azurite.ServiceUri
            : new Uri("http://127.0.0.1:10000/devstoreaccount1")).AbsoluteUri,
        ["WEFT_PRIVATE_STORAGE_BLOB_CONTAINER"] = container,
        ["WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL"] = "emulator"
    };

    private static Dictionary<string, string> Without(Dictionary<string, string> values, string name)
    {
        var copy = new Dictionary<string, string>(values);
        copy.Remove(name);
        return copy;
    }

    private static Dictionary<string, string> With(Dictionary<string, string> values, string name, string value) =>
        new(values) { [name] = value };

    private static Dictionary<string, string> Catalog(string mode) => new()
    {
        ["WEFT_CATALOG_ENABLED"] = "true",
        ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
        ["WEFT_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
        ["WEFT_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
        ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
        ["WEFT_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=weft_catalog_reader;Timeout=1",
        ["WEFT_REVISION_INGESTION_ENABLED"] = mode is "ingestion" or "both" ? "true" : "false",
        ["WEFT_REVISION_REPORTS_ENABLED"] = mode != "ingestion" ? "true" : "false",
        ["WEFT_REVISION_REPORT_DERIVATION_ENABLED"] = mode == "both" ? "true" : "false",
        // These cannot be parsed as connection strings; stored reads must never evaluate them.
        ["WEFT_POSTGRES_INGESTION_CONNECTION_STRING"] = "unused invalid native credentials",
        ["WEFT_POSTGRES_REPORT_CONNECTION_STRING"] = "unused invalid report reader credentials",
        ["WEFT_POSTGRES_DERIVATION_CONNECTION_STRING"] = "unused invalid report writer credentials"
    };

    private static WebApplicationBuilder Builder() => WebApplication.CreateBuilder(new WebApplicationOptions
    { EnvironmentName = "Development", Args = [], ContentRootPath = Path.GetTempPath() });

    private static async Task<(int ExitCode, string Output, string Error)> RunStorageToolAsync(
        IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "StorageTool", "Weft.StorageTool.dll");
        Assert.True(File.Exists(dll), "The owner storage tool and dependencies must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("WEFT_PRIVATE_STORAGE_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned storage tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
