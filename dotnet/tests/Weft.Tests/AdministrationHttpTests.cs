using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Administration;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Administration;
using Weft.Domain;

namespace Weft.Tests;

public sealed class AdministrationHttpTests
{
    private static readonly Guid Org = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Principal = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static string Path => $"/api/v1/organizations/{Org}/administration";

    [Fact]
    public async Task Read_carries_the_strong_scoped_etag_and_no_store()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"admin-{Org}-7\"", response.Headers.ETag!.Tag);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("W/\"7\"", 400)]
    [InlineData("\"7\"", 400)]
    public async Task Mutations_require_the_exact_strong_condition_before_dispatch(string? condition, int status)
    {
        await using var host = await Host.StartAsync();
        using var response = await host.PutAsync(Path + $"/members/{Principal}", "{\"active\":true,\"role\":\"administrator\"}", condition);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    [Theory]
    [InlineData("{\"active\":true,\"active\":false,\"role\":\"member\"}")]
    [InlineData("{\"active\":true,\"role\":\"owner\"}")]
    [InlineData("{\"active\":true,\"role\":\"member\",\"principalId\":\"override\"}")]
    [InlineData("{\"role\":\"member\"}")]
    public async Task Duplicate_unknown_and_missing_fields_and_unknown_roles_are_refused(string body)
    {
        await using var host = await Host.StartAsync();
        using var response = await host.PutAsync(Path + $"/members/{Principal}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    [Fact]
    public async Task Dispatch_binds_the_actual_actor_scope_and_condition_and_does_not_retry()
    {
        await using var host = await Host.StartAsync();
        host.Store.Outcome = "preconditionFailed";
        using var response = await host.PutAsync(Path + $"/members/{Principal}", "{\"active\":true,\"role\":\"administrator\"}");
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(1, host.Store.Writes);
        Assert.Equal(Principal, host.Store.Actor);
        Assert.Equal(Org, host.Store.Org);
        Assert.Equal(7, host.Store.Version);
    }

    [Fact]
    public async Task Foreign_scope_and_missing_csrf_never_reach_the_administration_store()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.PutAsync($"/api/v1/organizations/{Guid.NewGuid()}/administration/members/{Principal}", "{\"active\":true,\"role\":\"member\"}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var noCsrf = await host.Client.PutAsync(Path + $"/members/{Principal}", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    [Theory]
    [InlineData("principals")]
    [InlineData("bindings")]
    [InlineData("invitations")]
    [InlineData("invocation-grants")]
    public async Task Http2_data_without_length_or_transfer_encoding_is_refused_before_dispatch(string control)
    {
        await using var host = await Host.StartAsync(http2: true);
        var path = control == "invocation-grants" ? $"/api/v1/organizations/{Org}/projects/{Guid.NewGuid()}/administration" : Path;
        var suffix = control is "principals" or "bindings" ? "disable" : "revoke";
        using var response = await host.PostAsync($"{path}/{control}/{Guid.NewGuid()}/{suffix}", new UnknownLengthContent());
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("HTTP/2", host.Store.Protocol);
        Assert.Null(host.Store.ContentLength);
        Assert.False(host.Store.TransferEncoding);
        Assert.True(host.Store.CanHaveBody);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proven_bodyless_controls_keep_the_existing_csrf_and_dispatch_behavior(bool http2)
    {
        await using var host = await Host.StartAsync(http2);
        using var response = await host.PostAsync(Path + $"/invitations/{Guid.NewGuid()}/revoke");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(host.Store.CanHaveBody);
        Assert.Equal(1, host.Store.Writes);
        Assert.Equal(Principal, host.Store.Actor);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("unexpected administration body"u8.ToArray()).AsTask();
    }

    private sealed class Store : IAdministrationStore
    {
        internal int Writes; internal string Outcome = "updated"; internal Guid Actor, Org; internal long Version;
        internal string? Protocol; internal long? ContentLength; internal bool TransferEncoding; internal bool? CanHaveBody;
        public Task<OrganizationAdministrationV1?> ReadOrganizationAsync(ActorContext actor, Guid org, CancellationToken token) =>
            Task.FromResult<OrganizationAdministrationV1?>(new(7, [], [], []));
        public Task<ProjectAdministrationV1?> ReadProjectAsync(ActorContext actor, ProjectScope project, CancellationToken token) =>
            Task.FromResult<ProjectAdministrationV1?>(new(7, [], []));
        public Task<string> WriteAsync(ActorContext actor, Guid organizationId, Guid? projectId, long expectedVersion,
            AdministrationOperation operation, Guid targetId, JsonElement data, CancellationToken token)
        { Writes++; Actor = actor.PrincipalId.Value; Org = organizationId; Version = expectedVersion; return Task.FromResult(Outcome); }
    }
    private sealed class Host(WebApplication app, HttpClient client, Store store, string origin) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;
        internal Store Store { get; } = store;
        private string? requestToken;
        internal static async Task<Host> StartAsync(bool http2 = false)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var origin = $"http://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(s => s.Listen(IPAddress.Loopback, port,
                listen => listen.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddWeftCatalog(new(true, true, Principal, Org, origin), builder.Environment);
            var store = new Store(); builder.Services.AddSingleton<IAdministrationStore>(store);
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    store.Protocol = context.Request.Protocol; store.ContentLength = context.Request.ContentLength;
                    store.TransferEncoding = context.Request.Headers.ContainsKey("Transfer-Encoding");
                    store.CanHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody;
                }
                await next(context);
            });
            app.MapWeftAdministration();
            app.MapGet("/token", async (Microsoft.AspNetCore.Http.HttpContext context, CatalogRequestBoundary boundary) =>
            {
                var admission = await boundary.AuthenticateAsync(context, false);
                return await boundary.IssueRequestTokenAsync(context, admission.Actor!, default);
            });
            reservation.Stop(); await app.StartAsync();
            return new(app, new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new(origin), DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact }, store, origin);
        }
        internal async Task<HttpResponseMessage> PutAsync(string path, string body, string? condition = "default")
        {
            requestToken ??= await Client.GetStringAsync("/token");
            using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("Origin", origin); request.Headers.Add("X-XSRF-TOKEN", requestToken);
            if (condition is not null) request.Headers.TryAddWithoutValidation("If-Match", condition == "default" ? $"\"admin-{AdministrationHttpTests.Org}-7\"" : condition);
            return await Client.SendAsync(request);
        }
        internal async Task<HttpResponseMessage> PostAsync(string path, HttpContent? content = null)
        {
            requestToken ??= await Client.GetStringAsync("/token");
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = content, Version = Client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Headers.Add("Origin", origin); request.Headers.Add("X-XSRF-TOKEN", requestToken);
            request.Headers.Add("If-Match", $"\"admin-{AdministrationHttpTests.Org}-7\"");
            return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }
}
