using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using static Weft.Tests.ChatOrchestrationFixture;
using H = Weft.Tests.ChatHttpTestSupport;

namespace Weft.Tests;

/// <summary>Workspace chat over real HTTP, the actual catalog and chat ledger, and the owned provider wire; no native worker.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ChatWorkspaceHttpTests(PostgresFixture fixture)
{
    private static string Answer(string text) => Text(text) + Finish("stop") + Done;

    private static string Workspace(Guid id, object? focus = null) =>
        JsonSerializer.Serialize(new { conversationId = id.ToString("D"), focus });

    private async Task RunAsync(Func<ChatTestEnvironment, Task> scenario)
    {
        await using var reports = await PostgresRevisionDerivationTestSupport.CreateAsync(fixture);
        var reader = new ChatHttpSyntheticReportTests.SyntheticReports(reports);
        await scenario(new(reports.Case, reports.Actor, reports.Revision, reports.Seed.Admitted, reader, reports.Reader, reader.PublishAsync));
    }

    [PostgresFact]
    public Task Creation_accepts_omitted_focus_and_omitted_revision_without_changing_replay_identity() => RunAsync(async env =>
    {
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(Answer("ok")), ct));
        await using var api = await env.StartAsync(H.Options(provider), workspace: true);
        var token = await api.TokenAsync();
        var unfocused = Guid.NewGuid();
        using (var created = await api.PostAsync(api.WorkspaceConversationsPath,
            JsonSerializer.Serialize(new { conversationId = unfocused.ToString("D") }), token))
            Assert.Equal(JsonValueKind.Null, (await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created)).GetProperty("focus").ValueKind);
        using (var replay = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(unfocused), token))
            Assert.Equal(unfocused.ToString("D"), (await CatalogHttpTestSupport.JsonAsync(replay)).GetProperty("conversationId").GetString());

        var focused = Guid.NewGuid();
        using (var created = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(focused, new
        {
            projectId = env.Revision.ProjectId.ToString("D"),
            mappingId = env.Revision.MappingId.ToString("D")
        }), token))
            Assert.Equal(JsonValueKind.Null, (await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created)).GetProperty("focus").GetProperty("revisionId").ValueKind);
        using (var replay = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(focused, new
        {
            projectId = env.Revision.ProjectId.ToString("D"),
            mappingId = env.Revision.MappingId.ToString("D"),
            revisionId = (string?)null
        }), token))
            Assert.Equal(focused.ToString("D"), (await CatalogHttpTestSupport.JsonAsync(replay)).GetProperty("conversationId").GetString());
        Assert.Equal(0, provider.RequestCount);
    });

    [PostgresFact]
    public Task A_focused_workspace_conversation_answers_through_the_tools_and_streams_verified_workspace_citations() => RunAsync(async env =>
    {
        var mapping = env.Revision.MappingId.ToString("D");
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1 ? Call(0, "call-a", "{\"mappingId\":\"" + mapping + "\"}", "get_mapping") + Finish("tool_calls") + Done
                : Answer("It maps the synthetic source.")), ct));
        await using var api = await env.StartAsync(H.Options(provider), workspace: true);
        using (var availability = await api.Client.GetAsync(api.WorkspacePath))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(availability);
            Assert.Equal("weft.chat.v1", body.GetProperty("schemaVersion").GetString());
            Assert.Equal(env.Revision.OrganizationId.ToString("D"), body.GetProperty("organizationId").GetString());
            var profile = Assert.Single(body.GetProperty("profiles").EnumerateArray());
            Assert.Equal(H.ProfileId, profile.GetProperty("profile").GetProperty("profileId").GetString());
            Assert.False(profile.GetProperty("payloadsAllowed").GetBoolean());
        }
        var token = await api.TokenAsync();
        var focus = new { projectId = env.Revision.ProjectId.ToString("D"), mappingId = mapping, revisionId = (string?)null };
        var conversation = Guid.NewGuid();
        using (var created = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(conversation, focus), token))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);
            Assert.Equal("workspace", body.GetProperty("kind").GetString());
            Assert.Equal(mapping, body.GetProperty("focus").GetProperty("mappingId").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("revision").ValueKind);
        }
        // The identity is the idempotency key: the same request replays, a different focus conflicts.
        using (var replay = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(conversation, focus), token))
            Assert.Equal(conversation.ToString("D"), (await CatalogHttpTestSupport.JsonAsync(replay)).GetProperty("conversationId").GetString());
        using (var changed = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(conversation), token))
            await H.ErrorAsync(changed, HttpStatusCode.Conflict, "idempotencyConflict");

        var turn = Guid.NewGuid();
        using (var start = await api.PostAsync(api.WorkspaceConversationPath(conversation) + "/turns", H.Turn(turn, "What does this mapping do?"), token))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(start, HttpStatusCode.Created);
            // A mapping focus resolves to the head revision when the turn starts and is recorded with it.
            Assert.Equal("resolved", body.GetProperty("focus").GetProperty("state").GetString());
            Assert.Equal(env.Revision.RevisionId.ToString("D"), body.GetProperty("focus").GetProperty("revision").GetProperty("revisionId").GetString());
        }
        var frames = await api.StreamAsync(conversation, turn, path: api.WorkspaceTurnPath(conversation, turn));
        Assert.Equal("turn.started", frames[0].Name);
        Assert.Equal("turn.completed", frames[^1].Name);
        Assert.Equal("It maps the synthetic source.", H.Answer(frames));
        Assert.Contains(frames, frame => frame.Name == "tool.completed" && frame.Data.GetProperty("name").GetString() == "get_mapping");
        var citations = frames.Where(frame => frame.Name == "workspace.citation").Select(frame => frame.Data.GetProperty("citation")).ToArray();
        Assert.Equal(new[] { "mapping", "revision" }, citations.Select(citation => citation.GetProperty("kind").GetString()));
        Assert.All(citations, citation => Assert.Equal(mapping, citation.GetProperty("mappingId").GetString()));
        Assert.DoesNotContain(frames, frame => frame.Name is "citation" or "data.shared");

        // The provider saw the pinned instructions, the focus as a separate data message and the tool's result.
        Assert.Equal(2, provider.RequestCount);
        using (var first = provider.Requests[0].Json())
        {
            var messages = first.RootElement.GetProperty("messages");
            Assert.Contains("workspace assistant", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.Contains("weft.workspace-turn-context.v1", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic report source", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        }
        Assert.True(provider.Requests[1].ContainsUtf8("Synthetic report source"));

        // Replays never dispatch again; resumption is strictly after an acknowledged sequence.
        using (var replay = await api.PostAsync(api.WorkspaceConversationPath(conversation) + "/turns", H.Turn(turn, "What does this mapping do?"), token))
            Assert.Equal("completed", (await CatalogHttpTestSupport.JsonAsync(replay)).GetProperty("phase").GetString());
        using (var changed = await api.PostAsync(api.WorkspaceConversationPath(conversation) + "/turns", H.Turn(turn, "Something else"), token))
            await H.ErrorAsync(changed, HttpStatusCode.Conflict, "idempotencyConflict");
        var resumed = await api.StreamAsync(conversation, turn, after: frames[1].Id, path: api.WorkspaceTurnPath(conversation, turn));
        Assert.Equal(frames.Skip(2).Select(frame => frame.Id), resumed.Select(frame => frame.Id));
        Assert.Equal(2, provider.RequestCount);

        using (var detail = await api.Client.GetAsync(api.WorkspaceConversationPath(conversation)))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(detail);
            Assert.Equal("completed", Assert.Single(body.GetProperty("turns").EnumerateArray()).GetProperty("phase").GetString());
            Assert.Equal(1, body.GetProperty("conversation").GetProperty("turnCount").GetInt32());
        }
    });

    [PostgresFact]
    public Task Focus_does_not_restrict_cross_project_reads_and_each_turn_observes_the_targets_current_membership() => RunAsync(async env =>
    {
        var otherProject = Guid.Parse("00000002-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await env.Case.ExecuteAsync("""
            INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active)
            VALUES ($1,$2,$3,'viewer',true)
            """, env.Revision.OrganizationId, otherProject, env.Actor.PrincipalId.Value);
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal % 2 == 1 ? Call(0, "cross-project-" + exchange.Ordinal,
                "{\"projectId\":\"" + otherProject.ToString("D") + "\"}", "get_project") + Finish("tool_calls") + Done
                : Answer("The tool result describes current access.")), ct));
        await using var api = await env.StartAsync(H.Options(provider), workspace: true);
        var token = await api.TokenAsync();
        var conversation = Guid.NewGuid();
        await CatalogHttpTestSupport.JsonAsync(await api.PostAsync(api.WorkspaceConversationsPath, Workspace(conversation, new
        {
            projectId = env.Revision.ProjectId.ToString("D"),
            mappingId = env.Revision.MappingId.ToString("D"),
            revisionId = env.Revision.RevisionId.ToString("D")
        }), token), HttpStatusCode.Created);
        var first = Guid.NewGuid();
        await CatalogHttpTestSupport.JsonAsync(await api.PostAsync(api.WorkspaceConversationPath(conversation) + "/turns",
            H.Turn(first, "Describe the other project."), token), HttpStatusCode.Created);
        var frames = await api.StreamAsync(conversation, first, path: api.WorkspaceTurnPath(conversation, first));
        Assert.Equal("turn.completed", frames[^1].Name);
        Assert.Contains(frames, frame => frame.Name == "workspace.citation" &&
            frame.Data.GetProperty("citation").GetProperty("projectId").GetString() == otherProject.ToString("D"));
        Assert.True(provider.Requests[1].ContainsUtf8("Hidden project"));

        await env.Case.ExecuteAsync("""
            UPDATE weft.project_memberships SET active=false,version=version+1
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
            """, env.Revision.OrganizationId, otherProject, env.Actor.PrincipalId.Value);
        var second = Guid.NewGuid();
        await CatalogHttpTestSupport.JsonAsync(await api.PostAsync(api.WorkspaceConversationPath(conversation) + "/turns",
            H.Turn(second, "Read the other project again."), token), HttpStatusCode.Created);
        var denied = await api.StreamAsync(conversation, second, path: api.WorkspaceTurnPath(conversation, second));
        Assert.Equal("turn.completed", denied[^1].Name);
        Assert.DoesNotContain(denied, frame => frame.Name == "workspace.citation");
        Assert.True(provider.Requests[3].ContainsUtf8("notFound"));
        Assert.Equal(4, provider.RequestCount);
    });

    [PostgresFact]
    public Task Listings_cover_both_kinds_and_every_route_is_private_scoped_and_protected() => RunAsync(async env =>
    {
        await env.PublishReportsAsync();
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(Answer("ok")), ct));
        await using var api = await env.StartAsync(H.Options(provider), workspace: true);
        var token = await api.TokenAsync();
        var revisionConversation = await api.CreateConversationAsync(token);
        var exact = Guid.NewGuid();
        using (var created = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(exact, new
        {
            projectId = env.Revision.ProjectId.ToString("D"),
            mappingId = env.Revision.MappingId.ToString("D"),
            revisionId = env.Revision.RevisionId.ToString("D")
        }), token))
            await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);
        var unfocused = Guid.NewGuid();
        using (var created = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(unfocused), token))
            await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);

        using (var all = await api.Client.GetAsync(api.WorkspaceConversationsPath))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(all);
            Assert.Equal(new[] { unfocused, exact, revisionConversation }.Select(id => id.ToString("D")),
                body.GetProperty("conversations").EnumerateArray().Select(item => item.GetProperty("conversationId").GetString()));
            Assert.Equal(new[] { "workspace", "workspace", "revision" },
                body.GetProperty("conversations").EnumerateArray().Select(item => item.GetProperty("kind").GetString()));
        }
        using (var focused = await api.Client.GetAsync(api.WorkspaceConversationsPath + "?revisionId=" + env.Revision.RevisionId.ToString("D")))
            Assert.Equal(new[] { exact, revisionConversation }.Select(id => id.ToString("D")),
                (await CatalogHttpTestSupport.JsonAsync(focused)).GetProperty("conversations").EnumerateArray().Select(item => item.GetProperty("conversationId").GetString()));
        using (var paged = await api.Client.GetAsync(api.WorkspaceConversationsPath + "?limit=1&after=" + unfocused.ToString("D")))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(paged);
            Assert.Equal(exact.ToString("D"), Assert.Single(body.GetProperty("conversations").EnumerateArray()).GetProperty("conversationId").GetString());
            Assert.True(body.GetProperty("hasMore").GetBoolean());
        }
        foreach (var query in new[] { "?limit=0", "?limit=01", "?after=not-a-uuid", "?revisionId=" + Guid.Empty.ToString("D"), "?other=1" })
            using (var refused = await api.Client.GetAsync(api.WorkspaceConversationsPath + query))
                await H.ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidRequest");
        using (var unknown = await api.Client.GetAsync(api.WorkspaceConversationsPath + "?revisionId=" + Guid.NewGuid().ToString("D")))
            await H.ErrorAsync(unknown, HttpStatusCode.NotFound, "notFound");

        // A revision conversation is not a workspace conversation, another organization is never addressed,
        // and a focus the caller cannot read is refused before anything is stored.
        using (var wrongKind = await api.Client.GetAsync(api.WorkspaceConversationPath(revisionConversation)))
            await H.ErrorAsync(wrongKind, HttpStatusCode.NotFound, "notFound");
        var foreign = $"/api/v1/organizations/{Guid.NewGuid():D}/chat";
        using (var other = await api.Client.GetAsync(foreign + "/conversations"))
            await H.ErrorAsync(other, HttpStatusCode.NotFound, "notFound");
        using (var hidden = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(Guid.NewGuid(), new
        {
            projectId = Guid.NewGuid().ToString("D"),
            mappingId = Guid.NewGuid().ToString("D")
        }), token))
            await H.ErrorAsync(hidden, HttpStatusCode.NotFound, "notFound");
        foreach (var body in new[] { "{}", "{\"conversationId\":\"" + Guid.NewGuid().ToString("D") + "\",\"focus\":{\"mappingId\":\"" + Guid.NewGuid().ToString("D") + "\"}}",
            "{\"conversationId\":\"" + Guid.NewGuid().ToString("D") + "\",\"organizationId\":\"" + Guid.NewGuid().ToString("D") + "\"}" })
            using (var malformed = await api.PostAsync(api.WorkspaceConversationsPath, body, token))
                await H.ErrorAsync(malformed, HttpStatusCode.BadRequest, "invalidRequest");

        // State-changing routes require the antiforgery token and the request origin.
        using (var tokenless = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(Guid.NewGuid()), null))
            Assert.True(tokenless.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, tokenless.StatusCode.ToString());
        using (var crossOrigin = await api.PostAsync(api.WorkspaceConversationsPath, Workspace(Guid.NewGuid()), token, origin: "https://attacker.example"))
            Assert.True(crossOrigin.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, crossOrigin.StatusCode.ToString());

        var turn = Guid.NewGuid();
        await CatalogHttpTestSupport.JsonAsync(await api.PostAsync(api.WorkspaceConversationPath(unfocused) + "/turns", H.Turn(turn, "Hello"), token),
            HttpStatusCode.Created);
        Assert.Equal("ok", H.Answer(await api.StreamAsync(unfocused, turn, path: api.WorkspaceTurnPath(unfocused, turn))));

        // Another member of the organization sees none of it.
        await using var other2 = await env.StartAsync(H.Options(provider), principal: env.OtherPrincipal, workspace: true);
        var otherToken = await other2.TokenAsync();
        using (var listing = await other2.Client.GetAsync(other2.WorkspaceConversationsPath))
            Assert.Empty((await CatalogHttpTestSupport.JsonAsync(listing)).GetProperty("conversations").EnumerateArray());
        using (var detail = await other2.Client.GetAsync(other2.WorkspaceConversationPath(unfocused)))
            await H.ErrorAsync(detail, HttpStatusCode.NotFound, "notFound");
        using (var events = await other2.Client.GetAsync(other2.WorkspaceTurnPath(unfocused, turn) + "/events"))
            await H.ErrorAsync(events, HttpStatusCode.NotFound, "notFound");
        using (var cancel = await other2.PostAsync(other2.WorkspaceTurnPath(unfocused, turn) + "/cancel", null, otherToken))
            await H.ErrorAsync(cancel, HttpStatusCode.NotFound, "notFound");
        using (var start = await other2.PostAsync(other2.WorkspaceConversationPath(unfocused) + "/turns", H.Turn(Guid.NewGuid(), "Hi"), otherToken))
            await H.ErrorAsync(start, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(1, provider.RequestCount);
    });

    [PostgresFact]
    public Task Cancel_closes_a_live_workspace_turn_and_a_profile_without_tools_cannot_start_one() => RunAsync(async env =>
    {
        var closed = AiHttpFixture.NewSignal();
        await using var provider = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.WriteAsync(AiHttpFixture.Utf8(Text("partial")), ct);
            await exchange.ObserveClientCloseAsync(ct);
            closed.TrySetResult();
        });
        await using var api = await env.StartAsync(H.Options(provider), workspace: true);
        var token = await api.TokenAsync();
        var conversation = Guid.NewGuid();
        await CatalogHttpTestSupport.JsonAsync(await api.PostAsync(api.WorkspaceConversationsPath, Workspace(conversation), token), HttpStatusCode.Created);
        var turn = Guid.NewGuid();
        await CatalogHttpTestSupport.JsonAsync(await api.PostAsync(api.WorkspaceConversationPath(conversation) + "/turns", H.Turn(turn, "Explain"), token),
            HttpStatusCode.Created);
        var frames = await api.StreamAsync(conversation, turn, path: api.WorkspaceTurnPath(conversation, turn), onFrame: async frame =>
        {
            if (frame.Name != "text.delta") return;
            using var cancel = await api.PostAsync(api.WorkspaceTurnPath(conversation, turn) + "/cancel", null, token);
            Assert.Equal("cancelled", (await CatalogHttpTestSupport.JsonAsync(cancel)).GetProperty("phase").GetString());
        });
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("turn.cancelled", frames[^1].Name);
        using (var body = await api.PostAsync(api.WorkspaceTurnPath(conversation, turn) + "/cancel", "{}", token))
            await H.ErrorAsync(body, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(1, provider.RequestCount);

        // Workspace answers come only from the tools: a text-only profile is not offered and cannot start a turn.
        var textOnly = new Weft.Api.Chat.ChatApiOptions([new Weft.Api.Chat.ChatModelProfile("Text model", "fixture", Weft.Ai.AiProfileSpec.Create(
                Weft.Ai.AiProfileKey.Create(H.ProfileId, 1), Weft.Ai.AiDialect.ChatCompletionsSseV1, "fixture-model", provider.Endpoint("/v1/chat/completions"),
                true, Weft.Ai.AiAuthentication.Bearer, "fixture-credential", Weft.Ai.AiCapability.Text, "mapping-metadata", 1, true, false))],
            TimeSpan.FromDays(1), TimeSpan.FromSeconds(60), 128);
        await using var plain = await env.StartAsync(textOnly, workspace: true);
        using (var availability = await plain.Client.GetAsync(plain.WorkspacePath))
            await H.ErrorAsync(availability, HttpStatusCode.Conflict, "profileUnavailable");
        var plainToken = await plain.TokenAsync();
        using (var start = await plain.PostAsync(plain.WorkspaceConversationPath(conversation) + "/turns", H.Turn(Guid.NewGuid(), "Explain"), plainToken))
            await H.ErrorAsync(start, HttpStatusCode.Conflict, "profileUnavailable");
        Assert.Equal(1, provider.RequestCount);
    });
}
