using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Ai;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Application.MappingTools;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Actual Kestrel loopback HTTP/1.1, actual catalog/report/chat PostgreSQL roles and verified Ferrule reports.
/// The provider wire is the owned loopback AI HTTP fixture; no live model is contacted.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class ChatHttpTestSupport : IAsyncDisposable
{
    private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();
    internal const string ProfileId = "chat-fixture";
    internal const string CredentialValue = "fixture-bearer-value";
    private readonly WebApplication app;
    private readonly PostgresCatalogDatabase catalog;
    private readonly PostgresChatDatabase reader;
    private readonly PostgresChatDatabase writer;
    internal Uri Origin { get; }
    internal HttpClient Client { get; }
    internal ActorContext Actor { get; }
    internal RevisionScope Revision { get; }

    private ChatHttpTestSupport(WebApplication app, PostgresCatalogDatabase catalog, PostgresChatDatabase reader,
        PostgresChatDatabase writer, Uri origin, ActorContext actor, RevisionScope revision)
    {
        this.app = app; this.catalog = catalog; this.reader = reader; this.writer = writer;
        Origin = origin; Actor = actor; Revision = revision;
        Client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true, CookieContainer = new() })
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    internal ChatTurnHost Host => app.Services.GetRequiredService<ChatTurnHost>();

    internal static AiProfileSpec Profile(AiHttpFixture provider, bool metadataAllowed = true) =>
        AiProfileSpec.Create(AiProfileKey.Create(ProfileId, 1), AiDialect.ChatCompletionsSseV1, "fixture-model",
            provider.Endpoint("/v1/chat/completions"), true, AiAuthentication.Bearer, "fixture-credential",
            AiCapability.Text | AiCapability.FunctionTools, "mapping-metadata", 1, metadataAllowed, false);

    // Every chat transaction re-admits the complete schema, so a turn's ledger writes cost hundreds of milliseconds each on a
    // loaded host. Fixture turns get the longest configurable deadline, and streams outlive it so an overrun is reported by
    // the turn's own terminal event rather than a client cancellation.
    internal const int TurnSeconds = 180;
    private static readonly TimeSpan StreamBound = TimeSpan.FromSeconds(TurnSeconds + 30);

    internal static ChatApiOptions Options(AiHttpFixture provider, int turnSeconds = TurnSeconds) =>
        new([new ChatModelProfile("Fixture model", "fixture", Profile(provider))], TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(turnSeconds), 128);

    internal static async Task EnsureReportsAsync(RevisionReportTestSupport test)
    {
        foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
            Assert.NotNull(RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, operation)));
    }

    internal static Task<ChatHttpTestSupport> StartAsync(RevisionReportTestSupport test, ChatApiOptions options,
        Guid? principal = null, bool reportsEnabled = true) =>
        StartAsync(test.Case, new ActorContext(new PrincipalId(principal ?? test.Actor.PrincipalId.Value)), test.Revision, options,
            reportsEnabled ? test.Service : null, reportsEnabled ? test.Reader : null);

    /// <summary>
    /// With both report ports, registers the mapping tools and their citation check exactly as the Server host does.
    /// <paramref name="tools"/> may observe or alter the tool port only; citations are always checked by the real service.
    /// </summary>
    internal static async Task<ChatHttpTestSupport> StartAsync(PostgresCase test, ActorContext actor, RevisionScope revision,
        ChatApiOptions options, IRevisionReportReader? reports = null, IRevisionReportMetadataReader? metadata = null,
        Func<IChatReadOnlyToolPort, IChatReadOnlyToolPort>? tools = null, bool workspace = false, string? browserWebRoot = null,
        Uri? publicOrigin = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        var configured = (publicOrigin ?? origin).GetLeftPart(UriPartial.Authority);
        WebApplication? app = null;
        var catalog = new PostgresCatalogDatabase(test.CatalogConnectionString);
        var reader = new PostgresChatDatabase(test.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var writer = new PostgresChatDatabase(test.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", WebRootPath = browserWebRoot });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
                server.Listen(IPAddress.Loopback, origin.Port, listen => listen.Protocols = HttpProtocols.Http1));
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton(Keys);
            builder.Services.AddWeftCatalog(new(true, true, actor.PrincipalId.Value, revision.OrganizationId,
                configured), builder.Environment, new CatalogMetadataEditOptions(),
                new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(), new CatalogRevisionReportOptions(reports is not null, false));
            var catalogReader = new PostgresCatalogReader(catalog);
            builder.Services.AddSingleton<ICatalogReader>(catalogReader);
            builder.Services.AddSingleton<ICatalogResourceLocator>(catalogReader);
            if (reports is not null) builder.Services.AddSingleton(reports);
            if (reports is not null && metadata is not null)
            {
                var service = new MappingToolService(metadata, reports);
                builder.Services.AddSingleton(new ChatToolRegistry(MappingToolRegistry.Definitions, tools?.Invoke(service) ?? service, service));
                // The workspace tools as the Server host composes them, with no optional families or guide.
                if (workspace)
                {
                    builder.Services.AddSingleton(metadata);
                    builder.Services.AddSingleton(provider => new WorkspaceToolService(provider, options,
                        new WorkspaceToolFeatures(false, false, false, browserWebRoot is not null),
                        browserWebRoot is null ? null : new WorkspaceUserGuide(Weft.Api.Help.UserGuide.Embedded)));
                }
            }
            var readerStore = new PostgresChatStore(reader);
            var writerStore = new PostgresChatStore(writer);
            builder.Services.AddSingleton(new ChatPersistencePorts(readerStore, readerStore, writerStore, writerStore));
            builder.Services.AddSingleton<IChatMappingWriteJournal>(writerStore);
            builder.Services.AddSingleton<ChatMappingWriteRecovery>();
            builder.Services.AddSingleton<IAiCredentialResolver>(new Credentials());
            builder.Services.AddWeftChat(options);
            if (browserWebRoot is not null)
                Weft.Api.Auth.AuthenticationEndpoints.AddWeftAuthenticationHttp(builder.Services,
                    new(Weft.Api.Auth.AuthenticationHttpMode.Development, configured), builder.Environment);
            app = builder.Build();
            if (browserWebRoot is not null) { app.UseDefaultFiles(); app.UseStaticFiles(); }
            app.MapWeftCatalog();
            app.MapWeftChat();
            if (browserWebRoot is not null)
            {
                app.MapWeftHealth();
                app.MapWeftHelp();
                Weft.Api.Auth.AuthenticationEndpoints.MapWeftAuthentication(app);
                app.Map("/api/{**path}", () => Microsoft.AspNetCore.Http.Results.NotFound());
                app.MapFallbackToFile("index.html");
            }
            reservation.Stop();
            await app.StartAsync();
            return new(app, catalog, reader, writer, origin, actor, revision);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            await catalog.DisposeAsync(); await reader.DisposeAsync(); await writer.DisposeAsync();
            throw;
        }
        finally { reservation.Stop(); }
    }

    internal string ChatPath => CatalogRevisionReportHttpTestSupport.RevisionPath(Revision) + "/chat";
    internal string WorkspacePath => $"/api/v1/organizations/{Revision.OrganizationId:D}/chat";
    internal string WorkspaceConversationsPath => WorkspacePath + "/conversations";
    internal string WorkspaceConversationPath(Guid conversation) => WorkspaceConversationsPath + $"/{conversation:D}";
    internal string WorkspaceTurnPath(Guid conversation, Guid turn) => WorkspaceConversationPath(conversation) + $"/turns/{turn:D}";
    internal string ConversationsPath => ChatPath + "/conversations";
    internal string ConversationPath(Guid conversation) => ConversationsPath + $"/{conversation:D}";
    internal string TurnsPath(Guid conversation) => ConversationPath(conversation) + "/turns";
    internal string TurnPath(Guid conversation, Guid turn) => TurnsPath(conversation) + $"/{turn:D}";

    internal async Task<string> TokenAsync()
    {
        using var response = await Client.GetAsync("/api/v1/catalog/context");
        return (await CatalogHttpTestSupport.JsonAsync(response)).GetProperty("requestToken").GetString()!;
    }

    internal async Task<HttpResponseMessage> PostAsync(string path, string? json, string? token,
        string? origin = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (json is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        request.Headers.TryAddWithoutValidation("Origin", origin ?? Origin.GetLeftPart(UriPartial.Authority));
        if (token is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
        return await Client.SendAsync(request);
    }

    internal async Task<Guid> CreateConversationAsync(string token)
    {
        var id = Guid.NewGuid();
        using var response = await PostAsync(ConversationsPath, Conversation(id), token);
        var body = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(id.ToString("D"), body.GetProperty("conversationId").GetString());
        return id;
    }

    internal static string Conversation(Guid id) => JsonSerializer.Serialize(new { conversationId = id.ToString("D") });

    internal static string Turn(Guid id, string text, string profile = ProfileId, int version = 1, string selectors = "[]") =>
        "{\"turnId\":\"" + id.ToString("D") + "\",\"text\":" + JsonSerializer.Serialize(text) + ",\"profileId\":" +
        JsonSerializer.Serialize(profile) + ",\"profileVersion\":" + version + ",\"selectors\":" + selectors + "}";

    internal async Task<JsonElement> StartTurnAsync(Guid conversation, Guid turn, string text, string token)
    {
        using var response = await PostAsync(TurnsPath(conversation), Turn(turn, text), token);
        return await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.Created);
    }

    /// <summary>Reads one authenticated SSE response to its end; frames are returned in order.</summary>
    internal async Task<List<SseFrame>> StreamAsync(Guid conversation, Guid turn, int? after = null,
        Func<SseFrame, Task>? onFrame = null, CancellationToken ct = default, string? path = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, (path ?? TurnPath(conversation, turn)) + "/events" + (after is { } n ? $"?after={n}" : ""));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(StreamBound);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await using var stream = await response.Content.ReadAsStreamAsync(bound.Token);
        using var lines = new StreamReader(stream, new UTF8Encoding(false, true));
        var frames = new List<SseFrame>();
        string? id = null, name = null, data = null;
        while (await lines.ReadLineAsync(bound.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    var frame = new SseFrame(int.Parse(id!, System.Globalization.CultureInfo.InvariantCulture), name!, JsonDocument.Parse(data).RootElement.Clone());
                    Assert.Equal(frame.Name, frame.Data.GetProperty("type").GetString());
                    Assert.Equal(frame.Id, frame.Data.GetProperty("sequence").GetInt32());
                    frames.Add(frame);
                    if (onFrame is not null) await onFrame(frame);
                }
                id = name = data = null;
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) { Assert.Null(data); data = line[6..]; }
            else Assert.Fail("Unexpected SSE field.");
        }
        return frames;
    }

    internal static string Answer(IEnumerable<SseFrame> frames) =>
        string.Concat(frames.Where(frame => frame.Name == "text.delta").Select(frame => frame.Data.GetProperty("text").GetString()));

    internal static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await app.DisposeAsync(); }
        finally
        {
            await catalog.DisposeAsync();
            await reader.DisposeAsync();
            await writer.DisposeAsync();
        }
    }

    internal sealed record SseFrame(int Id, string Name, JsonElement Data);

    private sealed class Credentials : IAiCredentialResolver
    {
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct) =>
            ValueTask.FromResult<AiCredentialResult>(reference == "fixture-credential"
                ? CredentialFound.Create(AiBearerSecret.CopyFrom(CredentialValue)) : CredentialUnavailable.Instance);
    }
}
