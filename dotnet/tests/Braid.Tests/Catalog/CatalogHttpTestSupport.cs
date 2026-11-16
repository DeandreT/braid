using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>
/// Actual Kestrel/IP-loopback transport and actual catalog PostgreSQL reads. Setup uses
/// explicitly synthetic C1 admission evidence; this fixture does not qualify native validation.
/// No worker or private object store is registered in this application.
/// </summary>
internal sealed class CatalogHttpTestSupport : IAsyncDisposable
{
    // All helper hosts deliberately share these test-owned, memory-only keys. Cross-host
    // identity binding must fail because of the actor/organization claim, not a different key.
    // Framework token tests must not read or create the user's persisted ASP.NET key files.
    private static readonly IDataProtectionProvider SharedTestKeys = new EphemeralDataProtectionProvider();
    private readonly WebApplication app;
    private readonly PostgresCatalogDatabase? database;
    internal HttpClient Client { get; }
    internal Uri Origin { get; }
    internal CookieContainer Cookies { get; }
    internal int UnsafeBodyReadCount { get; private set; }
    internal int UnsafeMutationCount { get; private set; }
    internal const string UnsafePath = "/api/v1/catalog/test-only-unsafe";

    private CatalogHttpTestSupport(WebApplication app, PostgresCatalogDatabase? database, Uri origin)
    {
        this.app = app;
        this.database = database;
        Origin = origin;
        Cookies = new CookieContainer();
        Client = NewClient(Cookies);
    }

    internal HttpClient NewClient(CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        AllowAutoRedirect = false,
        UseProxy = false
    })
    { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(20) };

    internal static async Task<CatalogHttpTestSupport> StartAsync(PostgresCase? test = null,
        Guid? principal = null, Guid? organization = null, string environment = "Development",
        Action<IServiceCollection>? configureServices = null, long? maximumRequestBodyBytes = null, ICatalogSearch? search = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var origin = new Uri($"http://127.0.0.1:{port}");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            if (maximumRequestBodyBytes is not null) server.Limits.MaxRequestBodySize = maximumRequestBodyBytes;
            server.Listen(IPAddress.Loopback, port);
        });
        builder.Services.AddBraidApi();
        builder.Services.AddSingleton(SharedTestKeys);
        var options = test is null ? new CatalogApiOptions() : new CatalogApiOptions(true, true,
            principal ?? Guid.Parse("33333333-3333-3333-3333-333333333333"),
            organization ?? Guid.Parse("11111111-1111-1111-1111-111111111111"), origin.GetLeftPart(UriPartial.Authority));
        builder.Services.AddBraidCatalog(options, builder.Environment);
        PostgresCatalogDatabase? database = null;
        if (test is not null)
        {
            database = new(test.CatalogConnectionString);
            var reader = new PostgresCatalogReader(database);
            builder.Services.AddSingleton<ICatalogReader>(reader);
            builder.Services.AddSingleton<ICatalogSearch>(search ?? new PostgresCatalogSearch(reader));
        }
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        var support = new CatalogHttpTestSupport(app, database, origin);
        app.MapBraidCatalog();
        app.MapOpenApi();
        // P3d1 has no production unsafe route. This route independently qualifies the
        // reusable request boundary, followed by fresh admission, before application I/O.
        app.MapPost(UnsafePath, async (HttpContext context, CatalogRequestBoundary boundary) =>
        {
            var admission = await boundary.AuthenticateAsync(context, unsafeRequest: true, context.RequestAborted);
            if (!admission.IsAdmitted)
                return Results.Json(new CatalogErrorV1(admission.Error!.Value), statusCode: admission.StatusCode);
            var reader = context.RequestServices.GetRequiredService<ICatalogReader>();
            var authority = await reader.GetOrganizationAdmissionAsync(admission.Actor!, admission.AllowedOrganizationId!.Value,
                context.RequestAborted);
            if (authority is null)
                return Results.Json(new CatalogErrorV1(CatalogErrorCodeV1.NotFound), statusCode: StatusCodes.Status404NotFound);
            using var body = new MemoryStream();
            support.UnsafeBodyReadCount++;
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            support.UnsafeMutationCount++;
            return Results.Json(new { acceptedByteCount = body.Length });
        }).ExcludeFromDescription();
        reservation.Stop();
        try
        {
            await app.StartAsync();
            Assert.Equal(origin.GetLeftPart(UriPartial.Authority), Assert.Single(app.Urls).TrimEnd('/'));
            return support;
        }
        catch
        {
            await support.DisposeAsync();
            throw;
        }
    }

    internal static string ProjectPath(ProjectScope scope) =>
        $"/api/v1/organizations/{scope.OrganizationId:D}/projects/{scope.ProjectId:D}";

    internal static string MappingPath(RevisionScope scope) =>
        $"{ProjectPath(new(scope.OrganizationId, scope.ProjectId))}/mappings/{scope.MappingId:D}";

    internal static string RevisionPath(RevisionScope scope) => $"{MappingPath(scope)}/revisions/{scope.RevisionId:D}";

    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    internal static async Task<IngestionReceipt> CommitSyntheticAsync(PostgresIngestionTestSupport test,
        IngestionIntent? intent = null, bool valid = true)
    {
        var facts = await test.ReserveAsync(intent: intent);
        var claim = await test.ClaimAsync(facts);
        await test.ReadyAsync(facts, claim, valid);
        var result = await test.Store.FinalizeAsync(test.Actor, claim);
        Assert.Equal(IngestionStoreOutcome.Applied, result.Outcome);
        Assert.Equal(IngestionPhase.Committed, result.Receipt!.Phase);
        return result.Receipt;
    }

    internal async Task<HttpResponseMessage> UnsafeAsync(string? requestToken, string? origin,
        HttpClient? client = null, bool duplicateOrigin = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UnsafePath)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("body only available after boundary and fresh authority"))
        };
        if (requestToken is not null)
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", requestToken);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", duplicateOrigin ? new[] { origin, origin } : [origin]);
        return await (client ?? Client).SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await app.DisposeAsync(); }
        finally { if (database is not null) await database.DisposeAsync(); }
    }
}
