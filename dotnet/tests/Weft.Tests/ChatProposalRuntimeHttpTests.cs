using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Ai;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Real loopback routes and antiforgery; recording persistence/ingestion, no model or database.</summary>
public sealed class ChatProposalRuntimeHttpTests
{
    [Fact]
    public async Task Terminal_turn_review_is_private_bounded_and_exposes_complete_operations_separately()
    {
        await using var f = await Fixture.StartAsync();
        using var list = await f.Client.GetAsync(f.ListPath);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("no-store", list.Headers.CacheControl!.ToString());
        var listed = await Json(list);
        var review = Assert.Single(listed.GetProperty("proposals").EnumerateArray());
        Assert.Equal("awaitingApproval", review.GetProperty("status").GetString());
        Assert.True(review.GetProperty("changesTruncated").GetBoolean());
        Assert.Equal(64, review.GetProperty("changes").GetArrayLength());
        Assert.DoesNotContain("canonicalScript", review.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-native", review.GetRawText(), StringComparison.Ordinal);
        using var detail = await f.Client.GetAsync(f.Path + "/operations?expectedFingerprint=" + f.Store.Proposal.Fingerprint.Value);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var operations = await Json(detail);
        Assert.Equal(Encoding.UTF8.GetString(f.Store.Proposal.Content.Operations.CopyUtf8()), operations.GetProperty("canonicalScript").GetString());
        Assert.DoesNotContain("private-native", operations.GetRawText(), StringComparison.Ordinal);
        using var mismatch = await f.Client.GetAsync(f.Path + "/operations?expectedFingerprint=" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        using var missing = await f.Client.GetAsync(f.Path + "/operations");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        f.Store.Readable = false;
        using var revoked = await f.Client.GetAsync(f.Path);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Equal(0, f.Store.Approvals); Assert.Equal(0, f.Store.Appends); Assert.Equal(0, f.Store.Resumes);
    }

    [Fact]
    public async Task Exact_approval_requires_csrf_origin_and_closed_body_and_replays_prior_commit_after_head_moves()
    {
        await using var f = await Fixture.StartAsync();
        using (var missing = await f.Post("approve", token: false)) Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        using (var foreign = await f.Post("approve", origin: "https://foreign.invalid")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        foreach (var body in new[] { "{}", "{\"approved\":true}", f.Body.TrimEnd('}') + ",\"edit\":{}}",
            f.Body.TrimEnd('}') + ",\"expectedFingerprint\":\"" + f.Store.Proposal.Fingerprint.Value + "\"}",
            "{\"expectedFingerprint\":\"ABC\"}", "{\"expectedFingerprint\":\"\\ud800\"}", "{\"expectedFingerprint\":null}", "[]" })
        {
            using var invalid = await f.Post("approve", body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var mismatch = await f.Post("approve", "{\"expectedFingerprint\":\"" + new string('0', 64) + "\"}"))
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        using (var oversized = await f.Post("approve", "{\"expectedFingerprint\":\"" + new string('0', 257) + "\"}"))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(0, f.Store.Approvals); Assert.Equal(0, f.Store.Appends);
        using var first = await f.Post("approve");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = await Json(first);
        Assert.Equal("saved", saved.GetProperty("status").GetString());
        Assert.Equal(1, f.Store.Appends);
        f.Store.HeadChanged = true;
        using var repeated = await f.Post("approve");
        Assert.Equal(saved.GetRawText(), (await Json(repeated)).GetRawText());
        Assert.Equal(1, f.Store.Appends);
    }

    [Fact]
    public async Task Recovery_never_approves_first_reserves_or_resupplies_source_even_after_lost_prepare_ack()
    {
        await using var f = await Fixture.StartAsync();
        using (var unapproved = await f.Post("recover"))
            Assert.Equal("awaitingApproval", (await Json(unapproved)).GetProperty("status").GetString());
        Assert.Equal(0, f.Store.Approvals); Assert.Equal(0, f.Store.Appends); Assert.Equal(0, f.Store.Resumes);
        f.Store.LosePrepareAck = true;
        using (var lost = await f.Post("approve")) Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.Equal(0, f.Store.Appends);
        using (var approved = await f.Post("recover")) Assert.Equal("approved", (await Json(approved)).GetProperty("status").GetString());
        Assert.Equal(0, f.Store.Appends); Assert.Equal(0, f.Store.Resumes);
        f.Store.LosePrepareAck = false; f.Store.Commit = false;
        using (var retry = await f.Post("approve")) Assert.Equal("pending", (await Json(retry)).GetProperty("status").GetString());
        using (var recover = await f.Post("recover")) Assert.Equal("pending", (await Json(recover)).GetProperty("status").GetString());
        Assert.Equal(1, f.Store.Appends); Assert.Equal(1, f.Store.Resumes);
    }

    [Fact]
    public async Task Foreign_scope_permission_loss_and_unavailable_persistence_fail_closed()
    {
        await using var f = await Fixture.StartAsync();
        using (var foreign = await f.Client.GetAsync(f.Path.Replace(WorkspaceToolFixture.Organization.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)))
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var foreignTurn = await f.Client.GetAsync(f.Path.Replace(f.Store.Proposal.TurnId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)))
            Assert.Equal(HttpStatusCode.NotFound, foreignTurn.StatusCode);
        f.Store.Editable = false;
        using (var forbidden = await f.Post("approve")) Assert.Equal("notPermitted", (await Json(forbidden)).GetProperty("status").GetString());
        Assert.Equal(0, f.Store.Appends);
        f.Store.Editable = true; f.Store.HeadChanged = true;
        using (var stale = await f.Post("approve")) Assert.Equal("outOfDate", (await Json(stale)).GetProperty("status").GetString());
        Assert.Equal(0, f.Store.Appends);
        await using var missing = await Fixture.StartAsync(persistence: false);
        using var unavailable = await missing.Post("approve");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(0, missing.Store.Appends);
        await using var other = await Fixture.StartAsync(foreignCreator: true);
        using var privateProposal = await other.Client.GetAsync(other.Path);
        Assert.Equal(HttpStatusCode.NotFound, privateProposal.StatusCode);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return json.RootElement.Clone();
    }

    private sealed class Fixture(WebApplication app, HttpClient client, ChatProposalRuntimeStore store, string token) : IAsyncDisposable
    {
        internal HttpClient Client => client;
        internal ChatProposalRuntimeStore Store => store;
        internal string ListPath => $"/api/v1/organizations/{store.Proposal.Conversation.OrganizationId:D}/chat/conversations/{store.Proposal.Conversation.ConversationId:D}/turns/{store.Proposal.TurnId:D}/mapping-edit-proposals";
        internal string Path => ListPath + $"/{store.Proposal.ProposalId:D}";
        internal string Body => JsonSerializer.Serialize(new { expectedFingerprint = store.Proposal.Fingerprint.Value });
        internal async Task<HttpResponseMessage> Post(string action, string? body = null, bool token = true, string? origin = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path + "/" + action) { Content = new StringContent(body ?? Body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Origin", origin ?? client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (token) request.Headers.Add("X-XSRF-TOKEN", this.token);
            return await client.SendAsync(request);
        }
        private readonly string token = token;
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }

        internal static async Task<Fixture> StartAsync(bool persistence = true, bool foreignCreator = false)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var origin = new Uri($"http://127.0.0.1:{port}");
            var actor = WorkspaceToolFixture.Actor;
            var conversation = new WorkspaceToolFixture().Conversation();
            var turn = ChatTurnPolicyTests.Intent();
            var content = new MappingEditProposal(NativeArtifact.Admit(WorkspaceToolFixture.Head, "private-native"u8, 100),
                MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)), Enumerable.Range(0, 70).Select(i => new MappingEditChange("field" + i, "before", "after")));
            var store = new ChatProposalRuntimeStore { State = new(new(conversation.Key, turn.TurnId, Guid.NewGuid(), "call-1", 2, content), ChatMappingEditStatus.AwaitingApproval) };
            var profile = AiProfileSpec.Create(AiProfileKey.Create("approval-fixture", 1), AiDialect.ChatCompletionsSseV1,
                "fixture-model", new Uri("https://fixture.invalid/v1/chat/completions"), false, AiAuthentication.None, null,
                AiCapability.Text, "mapping-metadata", 1, true, false);
            var ledger = new ChatOrchestrationFixture.Ledger(conversation, actor, profile, new());
            await ledger.StartAsync(actor, conversation.Key, turn);
            ledger.TurnRead = value => value is null ? null : ChatTurnPolicyTests.Turn(turn, phase: ChatTurnPhase.Completed);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
            builder.Services.AddWeftApi();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddWeftCatalog(new(true, true, foreignCreator ? Guid.NewGuid() : actor.PrincipalId.Value,
                WorkspaceToolFixture.Organization, origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
            builder.Services.AddSingleton(new CatalogMappingEditOptions(true));
            builder.Services.AddSingleton<ICatalogReader>(new WorkspaceToolFixture());
            builder.Services.AddSingleton(new ChatPersistencePorts(new UnusedListings(), ledger, ledger, ledger));
            builder.Services.AddSingleton<IAiCredentialResolver>(new NoCredentials());
            builder.Services.AddWeftChat(new([new("Fixture", "fixture", profile)], TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 128));
            if (persistence)
            {
                builder.Services.AddSingleton<IChatMappingEditProposalStore>(store);
                builder.Services.AddSingleton<IProjectReader>(store);
                builder.Services.AddSingleton<IRevisionIngestionService>(store);
                builder.Services.AddSingleton(store.Service());
            }
            var app = builder.Build(); app.MapWeftCatalog(); app.MapWeftChat();
            reservation.Stop(); await app.StartAsync();
            var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = origin };
            using var response = await client.GetAsync("/api/v1/catalog/context");
            var token = foreignCreator ? "" : (await Json(response)).GetProperty("requestToken").GetString()!;
            return new(app, client, store, token);
        }
    }

    private sealed class NoCredentials : IAiCredentialResolver
    {
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct) => throw new InvalidOperationException("Approval must never call a provider.");
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
