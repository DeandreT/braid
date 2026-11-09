using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends.Workers;
using Weft.Server;

namespace Weft.Tests;

/// <summary>Ordinary build host composition and real loopback HTTP; no substituted build or report replies.</summary>
[SupportedOSPlatform("linux")]
internal sealed class BuildApiLiveHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly string token;
    private readonly ActorContext actor;
    private BuildApiLiveHost(WebApplication app, HttpClient client, string token, ActorContext actor)
    { this.app = app; this.client = client; this.token = token; this.actor = actor; }

    internal static async Task<BuildApiLiveHost> StartAsync(PostgresClaimAwareStorageIntegrationSupport test, string image,
        int reportLimit = RevisionReportPolicy.MaximumStoredObjectByteLimit, IReadOnlyDictionary<string, string>? runnerSettings = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var origin = new Uri("http://127.0.0.1:" + port);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = test.Root.ApplicationRoot });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
        builder.Services.AddWeftApi(); builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var values = new Dictionary<string, string>
        {
            ["WEFT_CATALOG_ENABLED"] = "true",
            ["WEFT_DEV_IDENTITY_ENABLED"] = "true",
            ["WEFT_DEV_PRINCIPAL_ID"] = test.Actor.PrincipalId.Value.ToString("D"),
            ["WEFT_DEV_ORGANIZATION_ID"] = test.Project.OrganizationId.ToString("D"),
            ["WEFT_DEV_BROWSER_ORIGIN"] = origin.GetLeftPart(UriPartial.Authority),
            ["WEFT_CATALOG_CONNECTION_STRING"] = test.Case.CatalogConnectionString,
            ["WEFT_BUILDS_ENABLED"] = "true",
            ["WEFT_FERRULE_WORKER_IMAGE"] = image,
            ["WEFT_PRIVATE_STORAGE_ROOT"] = test.Root.RootPath,
            ["WEFT_DERIVED_READ_BYTE_LIMIT"] = reportLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["WEFT_BUILD_METADATA_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_build_metadata_reader"),
            ["WEFT_BUILD_CONTROL_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_build_writer"),
            ["WEFT_BUILD_ACCESS_CONNECTION_STRING"] = test.Case.RuntimeConnectionString,
            ["WEFT_BUILD_REPORT_CONNECTION_STRING"] = test.Case.ReportConnectionString,
            ["WEFT_BUILD_CASE_READER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_case_reader"),
            ["WEFT_BUILD_EVIDENCE_READER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_run_reader"),
            ["WEFT_BUILD_RUN_READER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_build_run_reader"),
            ["WEFT_BUILD_RUN_WRITER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_build_run_writer")
        };
        if (runnerSettings is not null)
            foreach (var (key, value) in runnerSettings) values[key] = value;
        CatalogHostConfiguration.Configure(builder, new(IPAddress.Loopback, port), values.GetValueOrDefault);
        var app = builder.Build();
        var lookup = app.Services.GetRequiredService<IServiceProviderIsService>();
        foreach (var type in new[] { typeof(IBuildCompiler), typeof(IBuildRunQueue), typeof(IBuildRunStore), typeof(IBuildPublicationCatalog), typeof(IInspectionWorkerRunner) })
            Assert.False(lookup.IsService(type));
        app.MapWeftCatalog(); app.MapWeftBuilds(); app.MapWeftReleaseOperations(); app.MapWeftLocalSandbox(); reservation.Stop();
        var client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new(), UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(45) };
        try
        {
            await app.StartAsync();
            using var context = await client.GetAsync("/api/v1/catalog/context");
            Assert.Equal(HttpStatusCode.OK, context.StatusCode);
            using var document = JsonDocument.Parse(await context.Content.ReadAsByteArrayAsync());
            return new(app, client, document.RootElement.GetProperty("requestToken").GetString()!, test.Actor);
        }
        catch { client.Dispose(); await app.DisposeAsync(); throw; }
    }

    internal async Task ApproveAsync(BuildIntent expected)
    {
        var retained = await app.Services.GetRequiredService<IBuildMetadataReader>().ReadEnvironmentAsync(actor, expected.Environment);
        Assert.NotNull(retained); Assert.True(retained.Environment.Active); Assert.Equal(expected.Profile, retained.Environment.Profile);
        var source = await app.Services.GetRequiredKeyedService<IRevisionReportMetadataReader>("build-http").GetSourceAsync(actor, expected.Revision);
        Assert.NotNull(source); Assert.Equal(expected.Backend.BackendId, source.Source.SourceBackendId);
        var evidence = await app.Services.GetRequiredKeyedService<Weft.Application.Regression.RegressionRunReadService>("build-http")
            .ReadAsync(actor, expected.Revision.OrganizationId, expected.RegressionEvidenceId);
        Assert.NotNull(evidence); Assert.Equal(Weft.Application.Regression.RegressionComparisonOutcome.Passed, evidence.Result?.Comparison?.Outcome);
        Assert.Equal(expected.Backend.EngineBuild, evidence.Runtime?.BackendBuild);
        var report = await app.Services.GetRequiredKeyedService<IRevisionReportReader>("build-http")
            .GetCurrentAsync(actor, expected.Revision, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportReadOutcome.Observed, report.Outcome);
        var verified = Assert.IsType<VerifiedProjectionReport>(report.Report);
        Assert.Equal(expected.Backend.EngineBuild, verified.Metadata.Key.Profile.BackendBuild);
        Assert.Equal(Weft.Contracts.Operations.OperationStatusV1.Succeeded, verified.Report.Status);
        Assert.True(verified.Report.Provenance?.Confirmed);
        Assert.NotNull(verified.Report.Document);
        Assert.Equal(Weft.Contracts.Projections.CoverageStatus.Complete, verified.Report.Document.Coverage.Status);
        Assert.Empty(verified.Report.Document.Coverage.Gaps);
        Assert.All(verified.Report.Document.Boundaries, boundary => Assert.Equal("json", boundary.Format));
        var path = Mapping(expected.Revision) + "/build-approvals/" + expected.ApprovalId.ToString("D");
        var body = JsonSerializer.Serialize(new
        {
            buildId = expected.BuildId.ToString("D"),
            revisionId = expected.Revision.RevisionId.ToString("D"),
            environmentId = expected.Environment.EnvironmentId.ToString("D"),
            environmentVersion = expected.Environment.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            regressionEvidenceId = expected.RegressionEvidenceId.ToString("D")
        });
        using var response = await Put(path, body, CatalogBuildRequest.ApprovalMediaType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var approval = document.RootElement.GetProperty("approval");
        Assert.Equal(expected.NativeDigest.Value, approval.GetProperty("nativeDigest").GetString());
        Assert.Equal(expected.Backend.EngineBuild, approval.GetProperty("backend").GetProperty("engineBuild").GetString());
        Assert.Equal(expected.Boundaries.Count, approval.GetProperty("boundaries").GetArrayLength());
        Assert.Equal(expected.Revision.RevisionId.ToString("D"), approval.GetProperty("revision").GetProperty("revisionId").GetString());
    }
    internal async Task SubmitAsync(BuildIntent expected, bool replay = false)
    {
        using var response = await Put(Mapping(expected.Revision) + "/build-runs/" + expected.BuildId.ToString("D"),
            JsonSerializer.Serialize(new { approvalId = expected.ApprovalId.ToString("D") }), CatalogBuildRequest.RunMediaType);
        Assert.Equal(replay ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(replay ? "replayed" : "created", document.RootElement.GetProperty("outcome").GetString());
    }
    private async Task<HttpResponseMessage> Put(string path, string body, string media)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = new(media);
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
        return await client.SendAsync(request);
    }
    private static string Mapping(RevisionScope scope) => $"/api/v1/organizations/{scope.OrganizationId:D}/projects/{scope.ProjectId:D}/mappings/{scope.MappingId:D}";
    internal async Task<HttpResponseMessage> SandboxAsync(RevisionScope scope, HttpMethod method, string suffix = "", object? body = null,
        string? etag = null, bool antiforgery = true)
    {
        using var request = new HttpRequestMessage(method, Mapping(scope) + suffix);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (antiforgery) request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }
    public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
}
