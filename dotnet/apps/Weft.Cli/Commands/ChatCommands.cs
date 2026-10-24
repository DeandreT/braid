using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Chat;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static class ChatCommands
{
    private const int MaximumReconnects = 5;

    private sealed record Scope(Option<string> Project, Option<string> Mapping, Option<string> Revision)
    {
        public Symbol[] Symbols => [Project, Mapping, Revision];

        public async Task<ApiRequest> RequestAsync(CliInvocation invocation, ApiOperation operation) =>
            (await invocation.RequestAsync(operation)).Path("projectId", invocation.Id(Project)).Path("mappingId", invocation.Id(Mapping))
                .Path("revisionId", invocation.Id(Revision));
    }

    /// <summary>The result of one asked turn: the streamed answer and the resources it cites.</summary>
    internal sealed record ChatAnswer(string ConversationId, string TurnId, string Outcome, string Text,
        IReadOnlyList<ChatCitationEventV1> Citations, ChatUsageEventV1? Usage, ChatTurnFailedEventV1? Failure);

    public static Command Chat(CliGlobals globals)
    {
        var scope = new Scope(Required("--project", "The project ID"), Required("--mapping", "The mapping ID"),
            Required("--revision", "The revision ID"));

        var availability = new Command("availability", "Show the chat model profiles and limits for a revision").With(scope.Symbols);
        availability.Handle(globals, async invocation =>
        {
            var value = await invocation.Api.ReadAsync<ChatAvailabilityV1>(
                await scope.RequestAsync(invocation, ApiOperations.GetChatAvailability), invocation.Cancellation);
            invocation.Output.Write(value, output =>
            {
                output.Table(["PROFILE", "VERSION", "NAME", "PROVIDER", "MODEL", "TOOLS"], value.Profiles.Select(profile =>
                    (IReadOnlyList<object?>)[profile.ProfileId, profile.Version, profile.DisplayName, profile.ProviderId, profile.ModelId,
                        profile.SupportsTools]));
                output.Line();
                output.Fields(("Question bytes", value.Limits.MaxTextBytes), ("Selectors", value.Limits.MaxSelectors),
                    ("Turn seconds", value.Limits.TurnSeconds), ("Tools enabled", value.ToolsEnabled));
            });
        });

        var question = new Argument<string>("question") { Description = "The question, or - to read it from standard input" };
        var conversation = Optional("--conversation", "Continue this conversation (default: start a new one)");
        var model = Optional("--model", "The model profile as ID or ID:VERSION (default: the first available)");
        var select = new Option<string[]>("--select")
        {
            Description = "Point the question at a report resource: KIND=ID (boundary, field, parameter, rule, control), " +
                "projectionDiagnostic=N, validationDiagnostic=N, projectionReport or validationReport; repeat for several"
        };
        var ask = new Command("ask", "Ask about a revision and stream the answer with its citations")
            .With([.. scope.Symbols, conversation, model, select, question]);
        ask.Handle(globals, async invocation =>
        {
            var text = invocation.Value(question)!;
            if (text == "-")
                text = Encoding.UTF8.GetString(await ReadInputAsync(invocation, "-", 64 * 1024, "question")).TrimEnd('\r', '\n');
            if (text.Length == 0)
                throw CliException.Usage("The question is empty.");
            var selectors = (invocation.Value(select) ?? []).Select(Selector).ToArray();
            var available = await invocation.Api.ReadAsync<ChatAvailabilityV1>(
                await scope.RequestAsync(invocation, ApiOperations.GetChatAvailability), invocation.Cancellation);
            if (Encoding.UTF8.GetByteCount(text) > available.Limits.MaxTextBytes || selectors.Length > available.Limits.MaxSelectors)
                throw CliException.Usage($"The question exceeds the server's limits ({available.Limits.MaxTextBytes} bytes, " +
                    $"{available.Limits.MaxSelectors} selectors).");
            var profile = Profile(available, invocation.Value(model));
            var conversationId = invocation.OptionalId(conversation) ?? (await CreateConversationAsync(invocation, scope, null)).ConversationId;
            var turnId = CatalogCommands.NewId();
            var request = (await scope.RequestAsync(invocation, ApiOperations.CreateChatTurn)).Path("conversationId", conversationId)
                .Body(CatalogCommands.JsonBody(new ChatTurnCreateV1(turnId, text, profile.ProfileId, profile.Version, selectors),
                    ApiOperations.CreateChatTurn));
            var turn = await invocation.Api.ReadAsync<ChatTurnV1>(request, invocation.Cancellation);
            var answer = await FollowAsync(invocation, scope, conversationId, turn.TurnId, 0);
            return Finish(invocation, answer);
        });

        var turnId = Argument("turnId", "The turn ID");
        var conversationRequired = Required("--conversation", "The conversation ID");
        var after = new Option<int>("--after") { Description = "Replay events after this sequence number", DefaultValueFactory = _ => 0 };
        var follow = new Command("follow", "Stream an existing turn's events: replay, then follow it live")
            .With([.. scope.Symbols, conversationRequired, after, turnId]);
        follow.Handle(globals, async invocation =>
        {
            var start = invocation.Value(after);
            if (start < 0)
                throw CliException.Usage("--after must not be negative.");
            var answer = await FollowAsync(invocation, scope, invocation.Id(conversationRequired), invocation.Id(turnId), start);
            return Finish(invocation, answer);
        });

        var cancel = new Command("cancel", "Stop an active turn").With([.. scope.Symbols, conversationRequired, turnId]);
        cancel.Handle(globals, async invocation =>
        {
            var turn = await CancelAsync(invocation, scope, invocation.Id(conversationRequired), invocation.Id(turnId), invocation.Cancellation);
            invocation.Output.Write(turn, output => output.Line($"Turn {turn.TurnId} is {Name(turn.Phase)}."));
        });

        return new Command("chat", "Grounded chat about a revision").With(availability, Conversations(globals, scope), ask, follow, cancel);
    }

    private static Command Conversations(CliGlobals globals, Scope scope)
    {
        var limit = new Option<int?>("--limit") { Description = "At most this many conversations, 1-100 (default 50)" };
        var list = new Command("list", "List your conversations about a revision").With([.. scope.Symbols, limit]);
        list.Handle(globals, async invocation =>
        {
            var request = (await scope.RequestAsync(invocation, ApiOperations.GetChatConversations))
                .Query("limit", RegressionCommands.LimitValue(invocation, limit));
            var page = await invocation.Api.ReadAsync<ChatConversationPageV1>(request, invocation.Cancellation);
            invocation.Output.Write(page, output =>
            {
                output.Table(["CONVERSATION", "TITLE", "TURNS", "LAST TURN", "RETAINED UNTIL"], page.Conversations.Select(item =>
                    (IReadOnlyList<object?>)[item.ConversationId, item.Title, item.TurnCount, item.LastTurnAtUtc, item.RetainUntilUtc]));
                if (page.HasMore)
                    output.Line("More conversations exist; raise --limit.");
            });
        });

        var conversationId = Argument("conversationId", "The conversation ID");
        var get = new Command("get", "Show a conversation and its turns").With([.. scope.Symbols, conversationId]);
        get.Handle(globals, async invocation =>
        {
            var request = (await scope.RequestAsync(invocation, ApiOperations.GetChatConversation)).Path("conversationId", invocation.Id(conversationId));
            var detail = await invocation.Api.ReadAsync<ChatConversationDetailV1>(request, invocation.Cancellation);
            invocation.Output.Write(detail, output =>
            {
                output.Fields(("Conversation", detail.Conversation.ConversationId), ("Title", detail.Conversation.Title),
                    ("Projection", detail.Conversation.ProjectionDerivationId), ("Validation", detail.Conversation.ValidationDerivationId),
                    ("Retained until", detail.Conversation.RetainUntilUtc));
                output.Line();
                output.Table(["TURN", "PHASE", "MODEL", "CREATED", "QUESTION"], detail.Turns.Select(turn =>
                    (IReadOnlyList<object?>)[turn.TurnId, turn.Phase, $"{turn.Profile.ProfileId}:{turn.Profile.Version}", turn.CreatedAtUtc,
                         turn.Text.Length > 80 ? turn.Text[..80] + "…" : turn.Text]));
                if (detail.HasEarlierTurns)
                    output.Line("Earlier turns are omitted; this response contains only the latest turn window, not the complete conversation.");
            });
        });

        var newId = Optional("--conversation-id", "The conversation ID; reuse it to retry safely (default: new)");
        var create = new Command("create", "Start a conversation pinned to the revision's current reports").With([.. scope.Symbols, newId]);
        create.Handle(globals, async invocation =>
        {
            var created = await CreateConversationAsync(invocation, scope, invocation.OptionalId(newId));
            invocation.Output.Write(created, output => output.Line($"Conversation {created.ConversationId}."));
        });

        return new Command("conversations", "Your conversations about a revision").With(list, get, create);
    }

    private static async Task<ChatConversationV1> CreateConversationAsync(CliInvocation invocation, Scope scope, string? id)
    {
        var request = (await scope.RequestAsync(invocation, ApiOperations.CreateChatConversation))
            .Body(CatalogCommands.JsonBody(new ChatConversationCreateV1(id ?? CatalogCommands.NewId()), ApiOperations.CreateChatConversation));
        using var response = await invocation.Api.SendAsync(request, invocation.Cancellation);
        if (!response.IsSuccess && await response.FailureAsync(invocation.Cancellation) is { Category: "reportsUnavailable" })
            throw new CliException(CliExitCode.Conflict, "Chat needs the revision's projection and validation reports. Derive them with: " +
                "weft reports projection ensure, then weft reports validation ensure.", "reportsUnavailable");
        await response.RequireSuccessAsync(invocation.Cancellation);
        return await response.ReadJsonAsync<ChatConversationV1>(invocation.Cancellation);
    }

    /// <summary>
    /// Streams the turn's events from <paramref name="after"/>, reconnecting from the last sequence when the stream ends
    /// before a terminal event. Canceling the command stops the turn on the server.
    /// </summary>
    private static async Task<ChatAnswer> FollowAsync(CliInvocation invocation, Scope scope, string conversationId, string turnId, int after)
    {
        var text = new StringBuilder();
        var citations = new List<ChatCitationEventV1>();
        ChatUsageEventV1? usage = null;
        var sequence = after;
        var human = !invocation.Output.Json;
        try
        {
            for (var reconnect = 0; reconnect <= MaximumReconnects; reconnect++)
            {
                var request = (await scope.RequestAsync(invocation, ApiOperations.StreamChatTurnEvents)).Path("conversationId", conversationId)
                    .Path("turnId", turnId).Query("after", sequence.ToString(CultureInfo.InvariantCulture));
                using var response = await invocation.Api.SendAsync(request, invocation.Cancellation, streaming: true);
                await response.RequireSuccessAsync(invocation.Cancellation);
                if (response.Message.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    throw new CliException(CliExitCode.Transport, "The server's response was not a valid Weft response.", "invalidResponse");
                await using var stream = await response.ReadStreamAsync(invocation.Cancellation);
                var reader = new ServerSentEventReader(stream);
                try
                {
                    while (await reader.ReadAsync(invocation.Cancellation) is { } frame)
                    {
                        var item = Event(frame);
                        if (item.Sequence <= sequence)
                            continue;
                        sequence = item.Sequence;
                        switch (item)
                        {
                            case ChatTextDeltaEventV1 delta:
                                text.Append(delta.Text);
                                if (human)
                                {
                                    await invocation.Output.Writer.WriteAsync(SafeAnswer(delta.Text));
                                    await invocation.Output.Writer.FlushAsync(invocation.Cancellation);
                                }
                                break;
                            case ChatCitationEventV1 citation:
                                citations.Add(citation);
                                break;
                            case ChatUsageEventV1 snapshot:
                                usage = snapshot;
                                break;
                            case ChatTurnCompletedEventV1:
                                return new(conversationId, turnId, "completed", text.ToString(), citations, usage, null);
                            case ChatTurnFailedEventV1 failed:
                                return new(conversationId, turnId, "failed", text.ToString(), citations, usage, failed);
                            case ChatTurnCancelledEventV1:
                                return new(conversationId, turnId, "cancelled", text.ToString(), citations, usage, null);
                        }
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException && !invocation.Cancellation.IsCancellationRequested)
                {
                    // A dropped stream resumes from the last delivered sequence.
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << reconnect)), invocation.Environment.Time, invocation.Cancellation);
            }
        }
        catch (OperationCanceledException) when (invocation.Cancellation.IsCancellationRequested)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await CancelAsync(invocation, scope, conversationId, turnId, stop.Token); }
            catch (Exception error) when (error is CliException or OperationCanceledException) { }
            throw;
        }
        throw new CliException(CliExitCode.Transport,
            $"The answer stream ended before the turn finished. Resume it with: weft chat follow --conversation {conversationId} " +
            $"--after {sequence} {turnId}", "streamInterrupted");
    }

    private static async Task<ChatTurnV1> CancelAsync(CliInvocation invocation, Scope scope, string conversationId, string turnId,
        CancellationToken cancellationToken)
    {
        var request = (await scope.RequestAsync(invocation, ApiOperations.CancelChatTurn)).Path("conversationId", conversationId)
            .Path("turnId", turnId);
        return await invocation.Api.ReadAsync<ChatTurnV1>(request, cancellationToken);
    }

    private static int Finish(CliInvocation invocation, ChatAnswer answer)
    {
        if (invocation.Output.Json)
            invocation.Output.WriteJson(answer);
        else
        {
            var output = invocation.Output;
            output.Line();
            if (answer.Citations.Count > 0)
            {
                output.Line();
                output.Line("Citations:");
                for (var i = 0; i < answer.Citations.Count; i++)
                    output.Line($"  [{i + 1}] {Describe(answer.Citations[i].Target)} (report {answer.Citations[i].ReportDerivationId})");
            }
            if (answer.Failure is { } failure)
                invocation.Environment.Error.WriteLine($"The turn failed ({Name(failure.Reason)}" +
                    (failure.ProviderFailure is { } provider ? ", " + Name(provider) : "") + ").");
            else if (answer.Outcome == "cancelled")
                invocation.Environment.Error.WriteLine("The turn was cancelled.");
            invocation.Environment.Error.WriteLine($"Conversation {answer.ConversationId}, turn {answer.TurnId}.");
        }
        return answer.Outcome == "completed" ? (int)CliExitCode.Success : (int)CliExitCode.OperationFailed;
    }

    private static ChatTurnEventV1 Event(ServerSentEvent frame)
    {
        try
        {
            var item = JsonSerializer.Deserialize<ChatTurnEventV1>(frame.Data, WeftApiClient.Json);
            if (item is null || frame.Id != item.Sequence.ToString(CultureInfo.InvariantCulture))
                throw new JsonException();
            return item;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            throw new CliException(CliExitCode.Transport, "The server's event stream was not a valid Weft response.", "invalidResponse");
        }
    }

    private static ChatProfileV1 Profile(ChatAvailabilityV1 available, string? requested)
    {
        if (requested is null)
            return available.Profiles[0];
        var colon = requested.LastIndexOf(':');
        var id = colon < 0 ? requested : requested[..colon];
        int? version = colon < 0 ? null
            : int.TryParse(requested.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1
                ? number : throw CliException.Usage("--model must be ID or ID:VERSION.");
        return available.Profiles.Where(profile => profile.ProfileId == id && (version is null || profile.Version == version))
            .OrderByDescending(profile => profile.Version).FirstOrDefault()
            ?? throw CliException.Usage($"No available chat model profile matches {requested}. List them with: weft chat availability");
    }

    internal static ChatSelectorV1 Selector(string text)
    {
        var equals = text.IndexOf('=');
        var name = equals < 0 ? text : text[..equals];
        var value = equals < 0 ? null : text[(equals + 1)..];
        if (!Enum.TryParse<ChatSelectorKindV1>(name, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) ||
            name.Any(character => !char.IsAsciiLetter(character)))
            throw CliException.Usage($"Unknown --select kind {name}.");
        try
        {
            return kind switch
            {
                ChatSelectorKindV1.ProjectionReport or ChatSelectorKindV1.ValidationReport when value is null => new(kind, null, null),
                ChatSelectorKindV1.ProjectionDiagnostic or ChatSelectorKindV1.ValidationDiagnostic when
                    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) => new(kind, null, ordinal),
                _ when value is { Length: > 0 } && kind is not (ChatSelectorKindV1.ProjectionReport or ChatSelectorKindV1.ValidationReport or
                    ChatSelectorKindV1.ProjectionDiagnostic or ChatSelectorKindV1.ValidationDiagnostic) => new(kind, value, null),
                _ => throw CliException.Usage($"--select {name} takes {(kind is ChatSelectorKindV1.ProjectionReport or ChatSelectorKindV1.ValidationReport ? "no value" : "a value")}.")
            };
        }
        catch (ArgumentException)
        {
            throw CliException.Usage($"--select {text} is not a valid selector.");
        }
    }

    private static string Describe(ChatSelectorV1 target) => Name(target.Kind) +
        (target.ResourceId is { } id ? " " + CliOutput.Safe(id) : "") +
        (target.DiagnosticOrdinal is { } ordinal ? " #" + ordinal.ToString(CultureInfo.InvariantCulture) : "");

    /// <summary>Model text keeps line breaks and tabs; other control characters are replaced.</summary>
    internal static string SafeAnswer(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            builder.Append(rune.Value is '\n' or '\t' ? rune.ToString() : CliOutput.Safe(rune.ToString()));
        return builder.ToString();
    }

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
