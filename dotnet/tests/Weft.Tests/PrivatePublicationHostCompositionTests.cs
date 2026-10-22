using System.Net;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Weft.Server;
using Weft.Storage.FileSystem;
using Weft.Storage.Hosting;

namespace Weft.Tests;

/// <summary>Host registration and actual owned-file reads only; no database connection or workflow ownership is claimed.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivatePublicationHostCompositionTests
{
    [Theory]
    [InlineData("ingestion")]
    [InlineData("report-read")]
    [InlineData("report-write")]
    [InlineData("both")]
    public async Task EnabledFamiliesShareOneCapableStoreAndReadWithoutResolvingEitherPublicationCredentialFamily(string mode)
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(101);
        var reference = PrivateStorageData.Reference(bytes);
        using (var initialized = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => new PrivateStorageV2PhysicalRegistry()))
            await initialized.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope,
                reference, new MemoryStream(bytes));
        var marker = File.ReadAllBytes(root.MarkerPath);
        var native = mode is "ingestion" or "both";
        var reports = mode != "ingestion";
        var derived = mode is "report-write" or "both";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            Args = [],
            ContentRootPath = root.ApplicationRoot
        });
        var values = new Dictionary<string, string>
        {
            ["WEFT_CATALOG_ENABLED"] = "true",
            ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
            ["WEFT_DEV_PRINCIPAL_ID"] = "33333333-3333-3333-3333-333333333333",
            ["WEFT_DEV_ORGANIZATION_ID"] = "11111111-1111-1111-1111-111111111111",
            ["WEFT_DEV_BROWSER_ORIGIN"] = "http://127.0.0.1:4100",
            ["WEFT_CATALOG_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=weft_catalog_reader;Timeout=1",
            ["WEFT_REVISION_INGESTION_ENABLED"] = native ? "true" : "false",
            ["WEFT_REVISION_REPORTS_ENABLED"] = reports ? "true" : "false",
            ["WEFT_REVISION_REPORT_DERIVATION_ENABLED"] = derived ? "true" : "false",
            // These intentionally cannot be parsed as database connection strings.
            // Store/router construction and stored reads must never evaluate them.
            ["WEFT_POSTGRES_INGESTION_CONNECTION_STRING"] = "unused invalid native credentials",
            ["WEFT_POSTGRES_REPORT_CONNECTION_STRING"] = "unused invalid report reader credentials",
            ["WEFT_POSTGRES_DERIVATION_CONNECTION_STRING"] = "unused invalid report writer credentials",
            ["WEFT_PRIVATE_STORAGE_ROOT"] = root.RootPath
        };
        CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, 4100), values.GetValueOrDefault);
        Assert.Equal(native, builder.Services.Any(descriptor => descriptor.ServiceType == typeof(PostgresIngestionPublicationRegistry)));
        Assert.Equal(derived, builder.Services.Any(descriptor => descriptor.ServiceType == typeof(PostgresRevisionReportPublicationRegistry)));
        await using var app = builder.Build();

        var store = app.Services.GetRequiredService<IPrivateObjectStore>();
        var capable = app.Services.GetRequiredService<IPrivateObjectPublicationStore>();
        Assert.IsType<LazyPrivateObjectStore>(store);
        Assert.Same(store, capable);
        Assert.Same(store, app.Services.GetRequiredService<LazyPrivateObjectStore>());
        _ = app.Services.GetRequiredService<LazyPrivateObjectPublicationRegistry>();
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(marker, File.ReadAllBytes(root.MarkerPath));
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(bytes, await capable.ReadVerifiedAsync(reference.Id.Scope, reference));
    }
}
