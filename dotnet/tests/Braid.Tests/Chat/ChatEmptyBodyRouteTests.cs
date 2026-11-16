using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Ai;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Api.Chat;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Actual chat routes and antiforgery, with synthetic catalog/ledger ports and no provider calls.</summary>
public sealed class ChatEmptyBodyRouteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Chunked_empty_cancellation_preserves_authority_and_terminal_identity(bool workspace, bool completed)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var origin = new Uri($"http://127.0.0.1:{port}");
        var catalog = new WorkspaceToolFixture();
        var actor = WorkspaceToolFixture.Actor;
        var revision = WorkspaceToolFixture.Head;
        var id = Guid.NewGuid();
        var digest = Sha256Digest.FromBytes([1]);
        var conversation = workspace
            ? ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(revision.OrganizationId, actor.PrincipalId, id), null, ChatTurnPolicyTests.Now.AddDays(1))
            : new ChatConversationIntent(new(revision, actor.PrincipalId, id), new(Guid.NewGuid(), Guid.NewGuid(), digest, digest),
                new(Guid.NewGuid(), Guid.NewGuid(), digest, digest), ChatTurnPolicyTests.Now.AddDays(1));
        var profile = AiProfileSpec.Create(AiProfileKey.Create("cancellation-fixture", 1), AiDialect.ChatCompletionsSseV1,
            "fixture-model", new Uri("https://fixture.invalid/v1/chat/completions"), false, AiAuthentication.None, null,
            AiCapability.Text, "mapping-metadata", 1, true, false);
        var ledger = new ChatOrchestrationFixture.Ledger(conversation, actor, profile, new());
        var intent = ChatTurnPolicyTests.Intent(budget: new(100, 65536, 3));
        await ledger.StartAsync(actor, conversation.Key, intent);
        if (completed)
            ledger.TurnRead = value => value is null ? null : ChatTurnPolicyTests.Turn(intent, phase: ChatTurnPhase.Completed);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
        builder.Services.AddBraidApi();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddBraidCatalog(new(true, true, actor.PrincipalId.Value, revision.OrganizationId, origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
        builder.Services.AddSingleton<ICatalogReader>(catalog);
        builder.Services.AddSingleton(new ChatPersistencePorts(new UnusedListings(), ledger, ledger, ledger));
        builder.Services.AddSingleton<IAiCredentialResolver>(new NoCredentials());
        builder.Services.AddBraidChat(new([new("Fixture", "fixture", profile)], TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 128));
        await using var app = builder.Build();
        app.MapBraidCatalog();
        app.MapBraidChat();
        reservation.Stop();
        await app.StartAsync();
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies })
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var context = await client.GetAsync("/api/v1/catalog/context");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        using var contextJson = JsonDocument.Parse(await context.Content.ReadAsByteArrayAsync());
        var token = contextJson.RootElement.GetProperty("requestToken").GetString()!;
        var path = workspace
            ? $"/api/v1/organizations/{revision.OrganizationId:D}/chat/conversations/{id:D}/turns/{intent.TurnId:D}/cancel"
            : $"/api/v1/organizations/{revision.OrganizationId:D}/projects/{revision.ProjectId:D}/mappings/{revision.MappingId:D}/revisions/{revision.RevisionId:D}/chat/conversations/{id:D}/turns/{intent.TurnId:D}/cancel";

        // Invalid authority must not reach the turn store, even with otherwise valid framing.
        using (var missing = await SendAsync(token: null)) Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        using (var foreign = await SendAsync(token, "https://foreign.invalid")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var invalid = await SendAsync("invalid-token")) Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        Assert.Equal(0, ledger.Count("read-turn"));
        using (var nonempty = await SendAsync(token, bytes: [1])) Assert.Equal(HttpStatusCode.BadRequest, nonempty.StatusCode);
        Assert.Equal(0, ledger.Count("read-turn"));

        // Fully authorized but refused framing must close the connection before an appended cancellation runs.
        var authority = $"Host: {origin.Authority}\r\nOrigin: {origin.GetLeftPart(UriPartial.Authority)}\r\nX-XSRF-TOKEN: {token}\r\nCookie: {cookies.GetCookieHeader(new Uri(origin, path))}\r\n";
        var appended = $"POST {path} HTTP/1.1\r\n{authority}Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n0\r\n\r\n";
        foreach (var (headers, body) in new[]
        {
            ("Transfer-Encoding: chunked\r\n", "1\r\nx\r\n0\r\n\r\n"),
            ("Transfer-Encoding: chunked\r\n", "0\r\nX-Undeclared: refused\r\n\r\n"),
            ("Transfer-Encoding: chunked\r\nTrailer: X-Extra\r\n", "0\r\n\r\n"),
            ("Transfer-Encoding: chunked\r\n", "z\r\n"),
            ("Transfer-Encoding: chunked\r\nContent-Length: 0\r\n", "0\r\n\r\n"),
            ("Content-Length: 1\r\n", "x")
        })
        {
            var response = await ExchangeAsync($"POST {path} HTTP/1.1\r\n{authority}{headers}\r\n{body}{appended}");
            Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
            // Exactly one response: the appended request was never parsed, not merely refused.
            Assert.Equal(-1, response.IndexOf("HTTP/1.1 ", 1, StringComparison.Ordinal));
            Assert.Equal(0, ledger.Count("read-turn"));
            Assert.Equal(0, ledger.Count("finish"));
        }

        using var first = await SendAsync(token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(intent.TurnId.ToString("D"), firstJson.RootElement.GetProperty("turnId").GetString());
        Assert.Equal(completed ? "completed" : "cancelled", firstJson.RootElement.GetProperty("phase").GetString());
        var writes = ledger.Count("finish");
        using var replay = await SendAsync(token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal(firstJson.RootElement.GetRawText(), replayJson.RootElement.GetRawText());
        Assert.Equal(completed ? 0 : 1, writes);
        Assert.Equal(writes, ledger.Count("finish"));
        Assert.Equal(0, ledger.Count("reserve"));
        await app.StopAsync();

        async Task<HttpResponseMessage> SendAsync(string? token, string? suppliedOrigin = null, byte[]? bytes = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ChunkedContent(bytes ?? []) };
            request.Headers.Add("Origin", suppliedOrigin ?? origin.GetLeftPart(UriPartial.Authority));
            if (token is not null) request.Headers.Add("X-XSRF-TOKEN", token);
            request.Headers.TransferEncodingChunked = true;
            return await client.SendAsync(request);
        }

        async Task<string> ExchangeAsync(string message)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            await using var stream = raw.GetStream();
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(message), deadline.Token);
            using var received = new MemoryStream();
            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer, deadline.Token) is var count and > 0)
            {
                Assert.True(received.Length + count <= 65536, "The fixture response exceeded its bound.");
                received.Write(buffer, 0, count);
            }
            return System.Text.Encoding.ASCII.GetString(received.ToArray());
        }
    }

    internal sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    private sealed class NoCredentials : IAiCredentialResolver
    {
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct) =>
            throw new InvalidOperationException("Cancellation must never resolve provider credentials.");
    }

    private sealed class UnusedListings : IChatConversationReader
    {
        public Task<ChatConversationPage> ListConversationsAsync(ActorContext actor, RevisionScope revision, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationSummary?> GetConversationAsync(ActorContext actor, ChatConversationKey key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatTurnPage?> ListTurnsAsync(ActorContext actor, ChatConversationKey key, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationPage> ListOrganizationConversationsAsync(ActorContext actor, Guid organizationId, RevisionScope? revision, int limit, Guid? afterConversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationSummary?> FindConversationAsync(ActorContext actor, Guid organizationId, Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
