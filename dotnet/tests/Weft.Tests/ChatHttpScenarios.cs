using System.Collections.Concurrent;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Weft.Ai;
using Weft.Api.Chat;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;
using static Weft.Tests.ChatOrchestrationFixture;
using H = Weft.Tests.ChatHttpTestSupport;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL revision, catalog and chat ledger with the reports that conversations pin.</summary>
[SupportedOSPlatform("linux")]
internal sealed record ChatTestEnvironment(PostgresCase Case, ActorContext Actor, RevisionScope Revision, Guid OtherPrincipal,
    IRevisionReportReader Reports, IRevisionReportMetadataReader Metadata, Func<Task> PublishReportsAsync, bool DerivedReports = false)
{
    internal Task<ChatHttpTestSupport> StartAsync(ChatApiOptions options, Guid? principal = null,
        Func<IChatReadOnlyToolPort, IChatReadOnlyToolPort>? tools = null) =>
        H.StartAsync(Case, new ActorContext(new PrincipalId(principal ?? Actor.PrincipalId.Value)), Revision, options, Reports, Metadata, tools);
    public override string ToString() => nameof(ChatTestEnvironment);
}

/// <summary>Real Kestrel HTTP over actual PostgreSQL with the owned AI HTTP fixture as the provider wire.</summary>
[SupportedOSPlatform("linux")]
internal static class ChatHttpScenarios
{
    private static string Answer(string text) => Text(text) + Finish("stop") + Usage(11, 2, 13) + Done;

    internal static async Task StreamResumeAndReplay(ChatTestEnvironment env)
    {
        await env.PublishReportsAsync();
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(exchange.Ordinal == 1 ? Text("Hello") + Text(" world") + Finish("stop") + Usage(11, 2, 13) + Done
                : Answer("Second answer")), ct));
        await using var api = await env.StartAsync(H.Options(provider));
        using (var context = await api.Client.GetAsync("/api/v1/catalog/context"))
            Assert.True((await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("features").GetProperty("chat").GetBoolean());
        using (var availability = await api.Client.GetAsync(api.ChatPath))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(availability);
            Assert.Equal("weft.chat.v1", body.GetProperty("schemaVersion").GetString());
            var profile = Assert.Single(body.GetProperty("profiles").EnumerateArray());
            Assert.Equal(H.ProfileId, profile.GetProperty("profileId").GetString());
            Assert.Equal("Fixture model", profile.GetProperty("displayName").GetString());
            Assert.True(body.GetProperty("toolsEnabled").GetBoolean());
            Assert.Equal(60, body.GetProperty("limits").GetProperty("turnSeconds").GetInt32());
            Assert.DoesNotContain("127.0.0.1", body.GetRawText(), StringComparison.Ordinal);
        }
        var token = await api.TokenAsync();
        var conversation = await api.CreateConversationAsync(token);
        using (var replay = await api.PostAsync(api.ConversationsPath, H.Conversation(conversation), token))
            Assert.Equal(conversation.ToString("D"), (await CatalogHttpTestSupport.JsonAsync(replay)).GetProperty("conversationId").GetString());

        var turn = Guid.NewGuid();
        var created = await api.StartTurnAsync(conversation, turn, "Where does total come from?", token);
        Assert.Equal(turn.ToString("D"), created.GetProperty("turnId").GetString());
        Assert.Equal(H.ProfileId, created.GetProperty("profile").GetProperty("profileId").GetString());
        Assert.Equal("fixture-model", created.GetProperty("profile").GetProperty("modelId").GetString());
        var frames = await api.StreamAsync(conversation, turn);
        Assert.Equal("turn.started", frames[0].Name);
        Assert.Equal("turn.completed", frames[^1].Name);
        Assert.Equal("Hello world", H.Answer(frames));
        Assert.Contains(frames, frame => frame.Name == "usage" && frame.Data.GetProperty("totalTokens").GetInt64() == 13);
        Assert.DoesNotContain(frames, frame => frame.Name == "citation");
        Assert.Equal(frames.Select(frame => frame.Id).Order(), frames.Select(frame => frame.Id));
        Assert.Equal(1, provider.RequestCount);
        var request = Assert.Single(provider.Requests);
        Assert.True(request.IsPostAt("/v1/chat/completions"));
        Assert.True(request.HeaderIs("Authorization", "Bearer " + H.CredentialValue));
        Assert.True(request.ContainsUtf8("Where does total come from?"));

        // Resume strictly after an acknowledged sequence; reading never dispatches again.
        var resumed = await api.StreamAsync(conversation, turn, after: frames[1].Id);
        Assert.Equal(frames.Skip(2).Select(frame => frame.Id), resumed.Select(frame => frame.Id));
        Assert.Empty(await api.StreamAsync(conversation, turn, after: frames[^1].Id));
        using (var replay = await api.PostAsync(api.TurnsPath(conversation), H.Turn(turn, "Where does total come from?"), token))
            Assert.Equal("completed", (await CatalogHttpTestSupport.JsonAsync(replay)).GetProperty("phase").GetString());
        using (var changed = await api.PostAsync(api.TurnsPath(conversation), H.Turn(turn, "A different question"), token))
            await H.ErrorAsync(changed, HttpStatusCode.Conflict, "idempotencyConflict");
        Assert.Equal(1, provider.RequestCount);

        // The next turn carries the completed exchange as server-read history.
        var second = Guid.NewGuid();
        await api.StartTurnAsync(conversation, second, "And the second question?", token);
        Assert.Equal("Second answer", H.Answer(await api.StreamAsync(conversation, second)));
        Assert.Equal(2, provider.RequestCount);
        using (var history = provider.Requests[1].Json())
        {
            var messages = history.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(["system", "user", "assistant", "user"], messages.Select(message => message.GetProperty("role").GetString()));
            Assert.Equal("Hello world", messages[2].GetProperty("content").GetString());
        }

        using (var list = await api.Client.GetAsync(api.ConversationsPath))
        {
            var item = Assert.Single((await CatalogHttpTestSupport.JsonAsync(list)).GetProperty("conversations").EnumerateArray());
            Assert.Equal("Where does total come from?", item.GetProperty("title").GetString());
            Assert.Equal(2, item.GetProperty("turnCount").GetInt32());
        }
        using (var detail = await api.Client.GetAsync(api.ConversationPath(conversation)))
        {
            var body = await CatalogHttpTestSupport.JsonAsync(detail);
            var turns = body.GetProperty("turns").EnumerateArray().ToArray();
            Assert.Equal([turn.ToString("D"), second.ToString("D")], turns.Select(item => item.GetProperty("turnId").GetString()));
            Assert.All(turns, item => Assert.Equal("completed", item.GetProperty("phase").GetString()));
            Assert.Equal(frames[^1].Id, turns[0].GetProperty("lastSequence").GetInt32());
            Assert.False(body.GetProperty("hasEarlierTurns").GetBoolean());
        }
        Assert.False(provider.HasUnexpectedServerFailure);
    }

    internal static async Task CancelLiveTurn(ChatTestEnvironment env)
    {
        await env.PublishReportsAsync();
        var closed = AiHttpFixture.NewSignal();
        await using var provider = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.WriteAsync(AiHttpFixture.Utf8(Text("partial")), ct);
            await exchange.ObserveClientCloseAsync(ct);
            closed.TrySetResult();
        });
        await using var api = await env.StartAsync(H.Options(provider));
        var token = await api.TokenAsync();
        var conversation = await api.CreateConversationAsync(token);
        var turn = Guid.NewGuid();
        await api.StartTurnAsync(conversation, turn, "Explain the rule", token);
        var frames = await api.StreamAsync(conversation, turn, onFrame: async frame =>
        {
            if (frame.Name != "text.delta") return;
            using var cancel = await api.PostAsync(api.TurnPath(conversation, turn) + "/cancel", null, token);
            Assert.Equal("cancelled", (await CatalogHttpTestSupport.JsonAsync(cancel)).GetProperty("phase").GetString());
        });
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("partial", H.Answer(frames));
        Assert.Equal("turn.cancelled", frames[^1].Name);
        Assert.Single(frames, frame => frame.Name.StartsWith("turn.", StringComparison.Ordinal) && frame.Name != "turn.started");
        using (var again = await api.PostAsync(api.TurnPath(conversation, turn) + "/cancel", null, token))
            Assert.Equal("cancelled", (await CatalogHttpTestSupport.JsonAsync(again)).GetProperty("phase").GetString());
        using (var body = await api.PostAsync(api.TurnPath(conversation, turn) + "/cancel", "{}", token))
            await H.ErrorAsync(body, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(1, provider.RequestCount);
    }

    internal static async Task ReportPinsPrivacyAndRevocation(ChatTestEnvironment env)
    {
        await using var provider = new AiHttpFixture((exchange, ct) =>
            exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(Answer("ok")), ct));
        await using var api = await env.StartAsync(H.Options(provider));
        var token = await api.TokenAsync();
        // Reports are not yet derived: creation pins nothing and never derives implicitly.
        using (var unavailable = await api.PostAsync(api.ConversationsPath, H.Conversation(Guid.NewGuid()), token))
            await H.ErrorAsync(unavailable, HttpStatusCode.Conflict, "reportsUnavailable");
        await env.PublishReportsAsync();
        var conversation = await api.CreateConversationAsync(token);
        using (var detail = await api.Client.GetAsync(api.ConversationPath(conversation)))
        {
            var pinned = (await CatalogHttpTestSupport.JsonAsync(detail)).GetProperty("conversation");
            foreach (var (name, operation) in new[] { ("projectionDerivationId", RevisionReportOperation.Projection), ("validationDerivationId", RevisionReportOperation.Validation) })
            {
                var current = RevisionReportTestSupport.Available(await env.Reports.GetCurrentAsync(env.Actor, env.Revision, operation));
                Assert.Equal(current.Metadata.DerivationId.ToString("D"), pinned.GetProperty(name).GetString());
            }
        }
        using (var profile = await api.PostAsync(api.TurnsPath(conversation), H.Turn(Guid.NewGuid(), "question", version: 2), token))
            await H.ErrorAsync(profile, HttpStatusCode.Conflict, "profileUnavailable");
        Assert.Equal(0, provider.RequestCount);
        var turn = Guid.NewGuid();
        var created = await api.StartTurnAsync(conversation, turn, "private question", token);
        Assert.Equal("private question", created.GetProperty("text").GetString());
        Assert.Equal("turn.completed", (await api.StreamAsync(conversation, turn))[^1].Name);

        // Another project member reads the same revision but never another creator's conversation.
        await using var other = await env.StartAsync(H.Options(provider), env.OtherPrincipal);
        var otherToken = await other.TokenAsync();
        using (var list = await other.Client.GetAsync(other.ConversationsPath))
            Assert.Empty((await CatalogHttpTestSupport.JsonAsync(list)).GetProperty("conversations").EnumerateArray());
        using (var detail = await other.Client.GetAsync(other.ConversationPath(conversation)))
            await H.ErrorAsync(detail, HttpStatusCode.NotFound, "notFound");
        using (var events = await other.Client.GetAsync(other.TurnPath(conversation, turn) + "/events"))
            await H.ErrorAsync(events, HttpStatusCode.NotFound, "notFound");
        using (var cancel = await other.PostAsync(other.TurnPath(conversation, turn) + "/cancel", null, otherToken))
            await H.ErrorAsync(cancel, HttpStatusCode.NotFound, "notFound");
        using (var foreignTurn = await other.PostAsync(other.TurnsPath(conversation), H.Turn(Guid.NewGuid(), "q"), otherToken))
            await H.ErrorAsync(foreignTurn, HttpStatusCode.NotFound, "notFound");
        // Identities are creator-scoped: the same client ID creates the other member's own conversation.
        using (var reused = await other.PostAsync(other.ConversationsPath, H.Conversation(conversation), otherToken))
            Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
        using (var stillPrivate = await other.Client.GetAsync(other.ConversationPath(conversation)))
            Assert.Equal(0, (await CatalogHttpTestSupport.JsonAsync(stillPrivate)).GetProperty("turns").GetArrayLength());

        await env.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", env.Actor.PrincipalId.Value);
        using (var revoked = await api.Client.GetAsync(api.ConversationsPath))
            await H.ErrorAsync(revoked, HttpStatusCode.NotFound, "notFound");
        using (var revokedEvents = await api.Client.GetAsync(api.TurnPath(conversation, turn) + "/events"))
            await H.ErrorAsync(revokedEvents, HttpStatusCode.NotFound, "notFound");
        using (var revokedTurn = await api.PostAsync(api.TurnsPath(conversation), H.Turn(Guid.NewGuid(), "after revocation"), token))
            await H.ErrorAsync(revokedTurn, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(1, provider.RequestCount);
    }

    internal static async Task MappingToolRound(ChatTestEnvironment env)
    {
        await env.PublishReportsAsync();
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream",
            AiHttpFixture.Utf8(exchange.Ordinal == 1 ? Call(0, "call-summary", "{}", "get_mapping_summary") + Finish("tool_calls") + Done
                : Answer("The mapping has one explained boundary set.")), ct));
        await using var api = await env.StartAsync(H.Options(provider));
        var token = await api.TokenAsync();
        var conversation = await api.CreateConversationAsync(token);
        var turn = Guid.NewGuid();
        await api.StartTurnAsync(conversation, turn, "Summarize this mapping", token);
        var frames = await api.StreamAsync(conversation, turn);
        Assert.Equal("turn.completed", frames[^1].Name);
        var started = Assert.Single(frames, frame => frame.Name == "tool.started");
        var completed = Assert.Single(frames, frame => frame.Name == "tool.completed");
        Assert.Equal("get_mapping_summary", started.Data.GetProperty("name").GetString());
        Assert.Equal(1, completed.Data.GetProperty("ordinal").GetInt32());
        Assert.True(completed.Data.GetProperty("resultBytes").GetInt32() > 0);
        Assert.True(started.Id < completed.Id);
        Assert.Equal("The mapping has one explained boundary set.", H.Answer(frames));
        // Raw tool arguments and results stay out of the browser stream.
        Assert.DoesNotContain(frames, frame => frame.Data.GetRawText().Contains("call-summary", StringComparison.Ordinal));
        Assert.Equal(2, provider.RequestCount);
        using (var first = provider.Requests[0].Json())
            Assert.Equal(["get_mapping_summary", "search_mapping", "get_field_lineage", "get_rule_details", "get_validation_issues"],
                first.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()));
        using (var second = provider.Requests[1].Json())
        {
            var messages = second.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(message => message.GetProperty("role").GetString()));
            Assert.Equal("call-summary", messages[3].GetProperty("tool_call_id").GetString());
        }
    }

    internal static async Task VerifiedToolCitations(ChatTestEnvironment env)
    {
        await env.PublishReportsAsync();
        var projection = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(
            await env.Reports.GetCurrentAsync(env.Actor, env.Revision, RevisionReportOperation.Projection)));
        var field = projection.Report.Document?.Fields.FirstOrDefault()?.Id;
        // Worker-derived reports project fields; the synthetic projection is deliberately empty.
        Assert.Equal(env.DerivedReports, field is not null);
        var third = field is null ? Call(2, "call-search", "{\"query\":\"field\"}", "search_mapping")
            : Call(2, "call-lineage", JsonSerializer.Serialize(new { fieldId = field }), "get_field_lineage");
        // Text that looks like a citation is only model output; it never becomes a citation event.
        const string forgedText = "See {\"kind\":\"field\",\"resourceId\":\"forged.field\"} and weft:citation/rule/forged.rule.";
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1
                ? Call(0, "call-summary", "{}", "get_mapping_summary") + Call(1, "call-issues", "{}", "get_validation_issues") + third +
                    Finish("tool_calls") + Done
                : Answer(forgedText)), ct));
        RecordingTools? recorder = null;
        await using var api = await env.StartAsync(H.Options(provider), tools: inner => recorder = new(inner));
        var token = await api.TokenAsync();
        var conversation = await api.CreateConversationAsync(token);
        var pins = await PinsAsync(api, conversation);
        var turn = Guid.NewGuid();
        await api.StartTurnAsync(conversation, turn, "Which resources explain this mapping?", token);
        var frames = await api.StreamAsync(conversation, turn);
        Assert.Equal("turn.completed", frames[^1].Name);
        Assert.Equal(forgedText, H.Answer(frames));
        Assert.Equal(2, provider.RequestCount);

        // The candidates the production tool port returned, in call order; each verified one is streamed once.
        var results = recorder!.Results.ToArray();
        Assert.Equal(3, results.Length);
        var candidates = results.SelectMany(result => result.Citations).Distinct().ToArray();
        Assert.Contains(new ChatCitationTarget(ChatCitationKind.ProjectionReport), candidates);
        Assert.Contains(new ChatCitationTarget(ChatCitationKind.ValidationReport), candidates);
        if (field is not null) Assert.Contains(new ChatCitationTarget(ChatCitationKind.Field, field), candidates);
        var citations = frames.Where(frame => frame.Name == "citation").ToArray();
        Assert.Equal(candidates.Select(Selector), citations.Select(frame => Selector(frame.Data.GetProperty("target"))));
        foreach (var citation in citations)
        {
            var kind = citation.Data.GetProperty("target").GetProperty("kind").GetString();
            Assert.Equal(kind is "validationReport" or "validationDiagnostic" ? pins.Validation : pins.Projection,
                citation.Data.GetProperty("reportDerivationId").GetString());
        }
        Assert.DoesNotContain(citations, frame => frame.Data.GetRawText().Contains("forged", StringComparison.Ordinal));

        // Each tool's citations follow its completion and precede the next tool and the answer round.
        var completions = frames.Where(frame => frame.Name == "tool.completed").Select(frame => frame.Id).ToArray();
        var starts = frames.Where(frame => frame.Name == "tool.started").Select(frame => frame.Id).ToArray();
        Assert.Equal(3, completions.Length);
        var firstText = frames.First(frame => frame.Name == "text.delta").Id;
        for (var index = 0; index < results.Length; index++)
        {
            var end = index + 1 < starts.Length ? starts[index + 1] : firstText;
            var expected = results[index].Citations.Where(candidate => !results.Take(index).SelectMany(prior => prior.Citations).Contains(candidate))
                .Distinct().Select(Selector);
            Assert.Equal(expected, citations.Where(frame => frame.Id > completions[index] && frame.Id < end)
                .Select(frame => Selector(frame.Data.GetProperty("target"))));
        }
        var round = frames.First(frame => frame.Name == "tool.started").Data.GetProperty("roundId").GetString();
        Assert.All(citations, frame => Assert.Equal(round, frame.Data.GetProperty("roundId").GetString()));

        // Citations are durable ledger events: a resumed stream replays them without another provider request.
        var resumed = await api.StreamAsync(conversation, turn, after: completions[0]);
        Assert.Equal(frames.Where(frame => frame.Id > completions[0]).Select(frame => (frame.Id, frame.Name)),
            resumed.Select(frame => (frame.Id, frame.Name)));
        Assert.Equal(2, provider.RequestCount);
    }

    internal static async Task ForgedCitationCandidatesDropped(ChatTestEnvironment env)
    {
        await env.PublishReportsAsync();
        // A tool port that adds candidates absent from the pinned reports: another mapping's field and rule,
        // out-of-range diagnostic ordinals and a repeated genuine candidate. Only the real check decides.
        ChatCitationTarget[] forged =
        [
            new(ChatCitationKind.Field, "foreign.mapping.field"), new(ChatCitationKind.Rule, "foreign.rule"),
            new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 32_767), new(ChatCitationKind.ProjectionDiagnostic, diagnosticOrdinal: 32_767),
            new(ChatCitationKind.Control, "forged.control"), new(ChatCitationKind.ProjectionReport)
        ];
        await using var provider = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            exchange.Ordinal == 1
                ? Call(0, "call-rule", "{\"ruleId\":\"foreign.rule\"}", "get_rule_details") + Finish("tool_calls") + Done
                : Answer("foreign.rule is not part of this revision.")), ct));
        RecordingTools? recorder = null;
        await using var api = await env.StartAsync(H.Options(provider), tools: inner => recorder = new(inner, forged));
        var token = await api.TokenAsync();
        var conversation = await api.CreateConversationAsync(token);
        var pins = await PinsAsync(api, conversation);
        var turn = Guid.NewGuid();
        await api.StartTurnAsync(conversation, turn, "Explain foreign.rule", token);
        var frames = await api.StreamAsync(conversation, turn);
        Assert.Equal("turn.completed", frames[^1].Name);
        Assert.Equal(2, provider.RequestCount);

        // The unknown rule returns no node candidates; only the genuinely read projection report is cited, once.
        var result = Assert.Single(recorder!.Results);
        Assert.Equal(new[] { new ChatCitationTarget(ChatCitationKind.ProjectionReport) }, result.Citations.ToArray());
        var citation = Assert.Single(frames, frame => frame.Name == "citation");
        Assert.Equal("projectionReport", citation.Data.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal(pins.Projection, citation.Data.GetProperty("reportDerivationId").GetString());
        Assert.True(citation.Id > Assert.Single(frames, frame => frame.Name == "tool.completed").Id);
        foreach (var text in new[] { "foreign", "forged", "32767" })
            Assert.DoesNotContain(frames, frame => frame.Data.GetRawText().Contains(text, StringComparison.Ordinal) && frame.Name != "text.delta");
    }

    private static async Task<(string Projection, string Validation)> PinsAsync(ChatHttpTestSupport api, Guid conversation)
    {
        using var detail = await api.Client.GetAsync(api.ConversationPath(conversation));
        var pinned = (await CatalogHttpTestSupport.JsonAsync(detail)).GetProperty("conversation");
        return (pinned.GetProperty("projectionDerivationId").GetString()!, pinned.GetProperty("validationDerivationId").GetString()!);
    }

    private static string Selector(ChatCitationTarget target) =>
        JsonNamingPolicy.CamelCase.ConvertName(target.Kind.ToString()) + "|" + target.ResourceId + "|" + target.DiagnosticOrdinal;

    private static string Selector(JsonElement target) =>
        target.GetProperty("kind").GetString() + "|" + (target.TryGetProperty("resourceId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null) +
        "|" + (target.TryGetProperty("diagnosticOrdinal", out var ordinal) && ordinal.ValueKind == JsonValueKind.Number ? ordinal.GetInt32() : null);

    /// <summary>Observes the production tool port's results; optionally appends forged candidates to them.</summary>
    private sealed class RecordingTools(IChatReadOnlyToolPort inner, IReadOnlyList<ChatCitationTarget>? forged = null) : IChatReadOnlyToolPort
    {
        internal ConcurrentQueue<ChatReadOnlyToolResult> Results { get; } = new();
        public Task<bool> ValidateAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct) =>
            inner.ValidateAsync(context, intent, ct);
        public async Task<ChatReadOnlyToolResult> ExecuteAsync(ChatReadOnlyToolContext context, AiToolIntent intent, CancellationToken ct)
        {
            var result = await inner.ExecuteAsync(context, intent, ct);
            Results.Enqueue(result);
            return forged is null ? result : new(result.Result, result.RetrievedNodes, result.Citations.Concat(forged));
        }
    }

    internal static async Task AbandonedTurnInterrupted(ChatTestEnvironment env)
    {
        await env.PublishReportsAsync();
        await using var provider = new AiHttpFixture((exchange, ct) =>
            exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(Answer("fresh")), ct));
        var options = H.Options(provider);
        Guid conversation, abandoned = Guid.NewGuid();
        await using (var first = await env.StartAsync(options))
        {
            var token = await first.TokenAsync();
            conversation = await first.CreateConversationAsync(token);
            // A prior process's admitted start that never reached a provider round or terminal.
            await using var writerDatabase = new PostgresChatDatabase(env.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
            var key = new ChatConversationKey(env.Revision, env.Actor.PrincipalId, conversation);
            var envelope = new ChatOriginalEnvelope(abandoned, "left behind", [], new(600_000_000, 4, 1_048_576, 4_194_304, 131_072, 8, 128),
                ChatAiBridge.PinGroundingPolicy("weft.mapping-explanation", 1, "unused", [], 256, 32_768, 3));
            var deadline = first.Host.Now.AddSeconds(50);
            var started = await new PostgresChatStore(writerDatabase).StartAsync(env.Actor, key,
                new ChatTurnIntent(abandoned, envelope, options.Profiles[0].Pin, new(4096, 4_194_304, 4), deadline));
            Assert.Equal(ChatStoreOutcome.Applied, started.Outcome);
        }
        await using var restarted = await env.StartAsync(options);
        var freshToken = await restarted.TokenAsync();
        using (var blocked = await restarted.PostAsync(restarted.TurnsPath(conversation), H.Turn(Guid.NewGuid(), "next"), freshToken))
            await H.ErrorAsync(blocked, HttpStatusCode.Conflict, "turnActive");
        var frames = await restarted.StreamAsync(conversation, abandoned);
        Assert.Equal(["turn.started", "turn.failed"], frames.Select(frame => frame.Name));
        Assert.Equal("interrupted", frames[^1].Data.GetProperty("reason").GetString());
        Assert.Equal(0, provider.RequestCount);
        var next = Guid.NewGuid();
        await restarted.StartTurnAsync(conversation, next, "next", freshToken);
        Assert.Equal("fresh", H.Answer(await restarted.StreamAsync(conversation, next)));
        Assert.Equal(1, provider.RequestCount);
    }
}
