using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Regression;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class CatalogCsvContextTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Mapper_requires_both_preview_flags(bool preview, bool csv)
    {
        var context = CatalogPublicMapper.Context(new(WorkspaceToolFixture.Organization, WorkspaceToolFixture.Actor.PrincipalId, "Workspace"), "token",
            previewEnabled: preview, csvPreviewEnabled: csv);
        Assert.Equal(preview, context.Features.Preview);
        Assert.Equal(preview && csv ? true : (bool?)null, context.Features.CsvPreview);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task Context_reports_preview_csv_independently_without_resolving_private_services(bool previews, bool csvPreviews, bool csvRegressions)
    {
        var fixture = new WorkspaceToolFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddWeftApi();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddWeftCatalog(new(true, true, WorkspaceToolFixture.Actor.PrincipalId.Value,
            WorkspaceToolFixture.Organization, "http://127.0.0.1:4100"), builder.Environment);
        builder.Services.AddSingleton<ICatalogReader>(fixture);
        builder.Services.AddSingleton<IPrivateObjectStore>(_ => throw new InvalidOperationException("Context opened private storage."));
        builder.Services.AddSingleton<IRevisionReportMetadataReader>(_ => throw new InvalidOperationException("Context resolved native source credentials."));
        builder.Services.AddSingleton<IInspectionWorkerRunner>(_ => throw new InvalidOperationException("Context created a worker."));
        builder.Services.AddSingleton<IMappingPreviewBackend>(_ => throw new InvalidOperationException("Context created a preview backend."));
        builder.Services.AddWeftPreviews(new(previews, csvEnabled: csvPreviews));
        var runtime = new RegressionRuntimeIdentity("ferrule", "engine", "build", "adapter", PreviewPolicy.Profile);
        var csv = new RegressionRuntimeIdentity("ferrule", "engine", "build", "csv-adapter", CsvDocumentPolicy.Profile);
        builder.Services.AddWeftRegressionCases(new(true, runtime, csvRuntime: csvRegressions ? csv : null));
        await using var app = builder.Build();
        // TestServer has no socket peer. Model the configured loopback transport;
        // the production boundary still performs its normal host and identity checks.
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.MapWeftCatalog();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("http://127.0.0.1:4100");
        using var response = await client.GetAsync("/api/v1/catalog/context");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var features = json.RootElement.GetProperty("features");
        Assert.Equal(previews, features.GetProperty("preview").GetBoolean());
        Assert.Equal(previews && csvPreviews, features.TryGetProperty("csvPreview", out var value));
        if (previews && csvPreviews) Assert.Equal(JsonValueKind.True, value.ValueKind);
        Assert.Equal(new[] { "admission" }, fixture.CatalogReads);
    }
}
