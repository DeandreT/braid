using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Deployment;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReleaseOperationsHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Http_actions_require_antiforgery_exact_conditions_and_replay_the_original_operation()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var host = await Host.Start(data);
        using var context = await host.Client.GetAsync("/api/v1/catalog/context");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        using var json = JsonDocument.Parse(await context.Content.ReadAsByteArrayAsync());
        var token = json.RootElement.GetProperty("requestToken").GetString()!;
        using var targets = await host.Client.GetAsync(host.Path);
        Assert.Equal(HttpStatusCode.OK, targets.StatusCode);
        var text = await targets.Content.ReadAsStringAsync();
        Assert.Contains("target_id", text); Assert.DoesNotContain("proof_body", text);
        var body = JsonSerializer.Serialize(new
        {
            action = "activate",
            environmentId = data.Target.Reference.Environment.EnvironmentId.ToString("D"),
            environmentVersion = "1",
            qualificationId = data.First.Id.ToString("D"),
            sourceReleaseId = (string?)null
        });
        var id = Guid.NewGuid();
        using var csrf = await host.Post(id, body, null, "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        using var missing = await host.Post(id, body, token, null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var weak = await host.Post(id, body, token, "W/\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        using var invalidNull = await host.Post(id, body.Replace("\"sourceReleaseId\":null", "\"sourceReleaseId\":\"none\""), token, "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, invalidNull.StatusCode);
        using var first = await host.Post(id, body, token, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replay = await host.Post(id, body, token, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var stale = await host.Post(Guid.NewGuid(), body, token, "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_releases"));
        using var current = await host.Client.GetAsync(host.Path + "/" + data.Target.Reference.TargetId + "/targets");
        Assert.Equal("\"2\"", current.Headers.ETag!.Tag);
        using var history = await host.Client.GetAsync(host.Path + "/" + data.Target.Reference.TargetId + "/history?limit=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var page = JsonDocument.Parse(await history.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page.RootElement.GetProperty("nextOffset").GetInt32());
        using var summary = await host.Client.GetAsync(host.Path + "/" + data.Target.Reference.TargetId + "/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var parsedSummary = JsonSerializer.Deserialize<Braid.Contracts.Operations.ReleaseOperationsSummaryV1>(
            await summary.Content.ReadAsStringAsync(), Braid.Contracts.Projections.ProjectionJson.CreateOptions());
        Assert.NotNull(parsedSummary);
        Assert.Equal(0, parsedSummary.Count);
    }

    [PostgresFact]
    public async Task Http_recovery_is_refused_after_project_membership_revocation()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var host = await Host.Start(data);
        using var context = await host.Client.GetAsync("/api/v1/catalog/context");
        using var json = JsonDocument.Parse(await context.Content.ReadAsByteArrayAsync());
        var token = json.RootElement.GetProperty("requestToken").GetString()!;
        var operation = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            action = "disable",
            environmentId = data.Target.Reference.Environment.EnvironmentId.ToString("D"),
            environmentVersion = "1",
            qualificationId = (string?)null,
            sourceReleaseId = (string?)null
        });
        using var applied = await host.Post(operation, body, token, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        await data.Control.Runs.Cases.RoleAsync("viewer");
        using var denied = await host.Post(operation, body, token, "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));
    }

    [PostgresFact]
    public async Task Http_read_checks_current_project_role_and_exposes_no_cross_scope_history()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var host = await Host.Start(data);
        using var absent = await host.Client.GetAsync(host.Path.Replace(data.Target.Reference.Mapping.OrganizationId.ToString("D"), Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        await data.Control.Runs.Cases.RoleAsync("viewer");
        using var denied = await host.Client.GetAsync(host.Path);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Contains("no-store", denied.Headers.CacheControl!.ToString());
    }

    [PostgresFact]
    public async Task Pilot_route_HTTP_pins_reference_bytes_replays_create_and_fences_stale_pause()
    {
        await using var invocation = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, documents: true);
        var data = invocation.Releases;
        await using var host = await Host.Start(data);
        using var ctx = await host.Client.GetAsync("/api/v1/catalog/context");
        using var json = JsonDocument.Parse(await ctx.Content.ReadAsByteArrayAsync());
        var token = json.RootElement.GetProperty("requestToken").GetString()!;
        var trigger = Guid.NewGuid(); var operation = Guid.NewGuid();
        var form = new Braid.Contracts.Operations.PilotRouteFormV1(data.Target.Reference.Environment.EnvironmentId.ToString(), "1", Guid.NewGuid().ToString(), "inbox", "1",
            Guid.NewGuid().ToString(), "partner", "1", invocation.Caller.TenantId.ToString(), invocation.Caller.ObjectId.ToString(), invocation.Application.ToString(), "* * * * *", "2026-11-18T00:00:00Z", "2025b", "terms", "{\"days\":15}");
        var empty = new Braid.Contracts.Operations.PilotRouteFormV1(form.EnvironmentId, "1", null,null,null,null,null,null,null,null,null,null,null,null,null,null);
        async Task<HttpStatusCode> Apply(string action, Guid id, long expected, Braid.Contracts.Operations.PilotRouteFormV1 body, string? csrf)
        {
            using var response = await host.Post(id, JsonSerializer.Serialize(body, JsonSerializerOptions.Web), csrf, $"\"{expected}\"", $"pilot/{trigger}/{action}");
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Apply("configure", operation, 0, form, null));
        Assert.Equal(HttpStatusCode.OK, await Apply("configure", operation, 0, form, token));
        Assert.Equal(HttpStatusCode.OK, await Apply("configure", operation, 0, form, token));
        Assert.Equal(HttpStatusCode.Conflict, await Apply("configure", operation, 0, form with { ReferenceJson = "{}" }, token));
        Assert.Equal(HttpStatusCode.OK, await Apply("enable", Guid.NewGuid(), 1, empty, token));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await Apply("pause", Guid.NewGuid(), 1, empty, token));
        Assert.Equal(HttpStatusCode.OK, await Apply("pause", Guid.NewGuid(), 2, empty, token));
        await using var admin = new PostgresTriggerAdministrationStore(data.Case.RegressionConnectionString("braid_release_writer"));
        var first = (await admin.ReadAsync(data.Actor,data.Target.Reference,trigger))!.Configuration;
        var original = Braid.Deployment.Triggers.PilotRouteInputs.Request(first,"input"u8);
        Assert.Equal("{\"days\":15}", Encoding.UTF8.GetString(Assert.Single(original.NamedInputs).Bytes));
        Assert.Empty(original.Parameters);
        Assert.Equal(HttpStatusCode.OK, await Apply("configure", Guid.NewGuid(), 3, form with {ReferenceJson="{}"}, token));
        var second = (await admin.ReadAsync(data.Actor,data.Target.Reference,trigger))!.Configuration;
        Assert.NotEqual(original.Digest, Braid.Deployment.Triggers.PilotRouteInputs.Request(second,"input"u8).Digest);
        Assert.Equal(original.Digest, Braid.Deployment.Triggers.PilotRouteInputs.Request(Braid.Deployment.Triggers.TriggerConfiguration.Parse(first.CopyCanonicalBytes()),"input"u8).Digest);
    }

    [PostgresTheory]
    [InlineData("configureQueue", "blob")]
    [InlineData("configureBlob", "serviceBusTopic")]
    public async Task General_routes_require_current_admin_and_preserve_source_and_original_versions(string action, string kind)
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var host = await Host.Start(data);
        using var ctx = await host.Client.GetAsync("/api/v1/catalog/context");
        ctx.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await ctx.Content.ReadAsByteArrayAsync());
        var token = json.RootElement.GetProperty("requestToken").GetString()!;
        var trigger = Guid.NewGuid(); var op = Guid.NewGuid(); var schedule = action == "configureBlob";
        var form = new Braid.Contracts.Operations.PilotRouteFormV1(data.Target.Reference.Environment.EnvironmentId.ToString(), "1", Guid.NewGuid().ToString(), "local-in", "1",
            Guid.NewGuid().ToString(), "local-out", "1", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), schedule ? "* * * * *" : null,
            schedule ? "2026-11-18T00:00:00Z" : null, schedule ? "2025b" : null, null, null, kind, "application/json", "batch");
        async Task<HttpStatusCode> Post(Guid id, Braid.Contracts.Operations.PilotRouteFormV1 f, int generation, string? csrf)
        {
            using var response = await host.Post(id, JsonSerializer.Serialize(f, JsonSerializerOptions.Web), csrf, $"\"{generation}\"", $"pilot/{trigger}/{action}");
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Post(op, form, 0, null));
        await data.Control.Runs.Cases.RoleAsync("publisher");
        Assert.Equal(HttpStatusCode.NotFound, await Post(op, form, 0, token));
        await data.Control.Runs.Cases.RoleAsync("administrator");
        Assert.Equal(HttpStatusCode.OK, await Post(op, form, 0, token));
        Assert.Equal(HttpStatusCode.OK, await Post(op, form, 0, token));
        Assert.Equal(HttpStatusCode.Conflict, await Post(op, form with { DestinationVersion="2" }, 0, token));
        Assert.Equal(HttpStatusCode.NotFound, await Post(Guid.NewGuid(), form with { SourceBinding=Guid.NewGuid().ToString() }, 1, token));
        Assert.Equal(HttpStatusCode.NotFound, await Post(Guid.NewGuid(), form with { Source="another-in" }, 1, token));
        Assert.Equal(HttpStatusCode.OK, await Post(Guid.NewGuid(), form with { DestinationVersion="2" }, 1, token));
        Assert.Equal("1", await data.Case.ScalarAsync<string>("SELECT convert_from(definition,'UTF8')::jsonb->'routes'->0->>'connectorVersion' FROM braid.trigger_configurations WHERE trigger_id=$1 AND version=1",trigger));
        Assert.Equal("2", await data.Case.ScalarAsync<string>("SELECT convert_from(definition,'UTF8')::jsonb->'routes'->0->>'connectorVersion' FROM braid.trigger_configurations WHERE trigger_id=$1 AND version=2",trigger));
    }

    private sealed class Host(WebApplication app, PostgresCatalogDatabase catalog, HttpClient client, string path, Guid target) : IAsyncDisposable
    {
        internal HttpClient Client => client;
        internal string Path => path;
        internal static async Task<Host> Start(LocalReleasePersistenceTests.Context data)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var origin = new Uri("http://127.0.0.1:" + port);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
            builder.Services.AddBraidApi(); builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddBraidCatalog(new(true, true, data.Actor.PrincipalId.Value, data.Target.Reference.Mapping.OrganizationId, origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
            builder.Services.AddSingleton(new ReleaseOperationsOptions(true));
            var catalog = new PostgresCatalogDatabase(data.Case.CatalogConnectionString);
            builder.Services.AddSingleton<ICatalogReader>(new PostgresCatalogReader(catalog));
            builder.Services.AddSingleton<IReleaseOperationsStore>(data.Store);
            builder.Services.AddSingleton<Braid.Deployment.Triggers.ITriggerAdministrationStore>(_ => new PostgresTriggerAdministrationStore(data.Case.RegressionConnectionString("braid_release_writer")));
            builder.Services.AddSingleton(new ReleaseOperationsService(data.Store, data.Store, data.Qualifications));
            var app = builder.Build(); app.MapBraidCatalog(); app.MapBraidReleaseOperations(); listener.Stop();
            var client = new HttpClient(new HttpClientHandler { CookieContainer = new(), UseCookies = true, UseProxy = false }) { BaseAddress = origin };
            try
            {
                await app.StartAsync();
                var mapping = data.Target.Reference.Mapping;
                return new(app, catalog, client, $"/api/v1/organizations/{mapping.OrganizationId:D}/projects/{mapping.ProjectId:D}/mappings/{mapping.MappingId:D}/operations", data.Target.Reference.TargetId);
            }
            catch { client.Dispose(); await app.DisposeAsync(); await catalog.DisposeAsync(); throw; }
        }
        internal async Task<HttpResponseMessage> Post(Guid operation, string body, string? token, string? etag, string section = "actions")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path + $"/{target:D}/{section}/{operation:D}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (token is not null) request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); await catalog.DisposeAsync(); }
    }
}
