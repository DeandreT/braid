using System.CommandLine;
using System.Globalization;
using System.Text;
using Weft.Cli.Http;
using Weft.Contracts.Chat;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

/// <summary>Organization-scoped read-only chat; revision commands retain their original paths and pins.</summary>
internal static class WorkspaceChatCommands
{
    private static readonly ChatCommands.Scope StreamScope = new(new("--project"), new("--mapping"), new("--revision"), Workspace: true);

    public static Command Workspace(CliGlobals globals)
    {
        var availability = new Command("availability", "Show workspace model profiles, data policy and limits");
        availability.Handle(globals, async invocation =>
        {
            var value = await Available(invocation);
            invocation.Output.Write(value, output =>
            {
                output.Table(["PROFILE", "VERSION", "NAME", "MODEL", "PAYLOADS ALLOWED"], value.Profiles.Select(item =>
                    (IReadOnlyList<object?>)[item.Profile.ProfileId, item.Profile.Version, item.Profile.DisplayName, item.Profile.ModelId, item.PayloadsAllowed]));
                output.Line("Payload data also requires a current grant on its project. Focus never restricts authorized tools.");
            });
        });
        var question = new Argument<string>("question") { Description = "The question, or - to read standard input" };
        var conversation = Optional("--conversation", "Continue a workspace conversation (default: create one)");
        var model = Optional("--model", "Model profile ID or ID:VERSION");
        var requestId = Optional("--turn-id", "Reuse this turn ID with --conversation to recover an unacknowledged send");
        var focus = new Focus();
        var ask = new Command("ask", "Ask read-only questions across your authorized workspace").With([question, conversation, model, requestId, .. focus.Symbols]);
        ask.Handle(globals, async invocation =>
        {
            var selectedFocus = focus.Value(invocation);
            var id = invocation.OptionalId(conversation);
            var turnId = invocation.OptionalId(requestId);
            if (turnId is not null && id is null) throw CliException.Usage("--turn-id recovery requires --conversation.");
            if (id is not null && selectedFocus is not null) throw CliException.Usage("An existing conversation keeps its original focus; omit focus options when continuing it.");
            var text = invocation.Value(question)!;
            if (text == "-") text = Encoding.UTF8.GetString(await ReadInputAsync(invocation, "-", 64 * 1024, "question")).TrimEnd('\r', '\n');
            var available = await Available(invocation);
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > available.Limits.MaxTextBytes)
                throw CliException.Usage("The question is empty, invalid or exceeds the server's text limit.");
            var profile = ChooseProfile(available, invocation.Value(model));
            id ??= (await Create(invocation, CatalogCommands.NewId(), selectedFocus)).ConversationId;
            turnId ??= CatalogCommands.NewId();
            invocation.Environment.Error.WriteLine($"Conversation {id}, turn {turnId}. An unacknowledged send can be retried with these IDs and the same question/profile.");
            var request = (await invocation.RequestAsync(ApiOperations.CreateWorkspaceChatTurn)).Path("conversationId", id)
                .Body(CatalogCommands.JsonBody(new ChatTurnCreateV1(turnId, text, profile.ProfileId, profile.Version, []), ApiOperations.CreateWorkspaceChatTurn));
            var turn = await invocation.Api.ReadAsync<ChatTurnV1>(request, invocation.Cancellation);
            return await Follow(invocation, id, turn.TurnId, 0);
        });
        var conversationRequired = Required("--conversation", "The workspace conversation ID");
        var turn = Argument("turnId", "The turn ID");
        var after = new Option<int>("--after") { Description = "Read after this sequence, 0-4096", DefaultValueFactory = _ => 0 };
        var follow = new Command("follow", "Replay and follow a workspace answer without sending a question").With(conversationRequired, turn, after);
        follow.Handle(globals, invocation =>
        {
            var sequence = invocation.Value(after);
            if (sequence is < 0 or > 4096) throw CliException.Usage("--after must be between 0 and 4096.");
            return Follow(invocation, invocation.Id(conversationRequired), invocation.Id(turn), sequence);
        });
        var cancel = new Command("cancel", "Cancel an active workspace answer").With(conversationRequired, turn);
        cancel.Handle(globals, async invocation =>
        {
            var value = await ChatCommands.CancelAsync(invocation, StreamScope, invocation.Id(conversationRequired), invocation.Id(turn), invocation.Cancellation);
            invocation.Output.Write(value, output => output.Line($"Turn {value.TurnId} is {value.Phase}."));
        });
        return new Command("workspace", "Private workspace conversations; focus is context, not an access limit")
            .With(availability, Conversations(globals), ask, follow, cancel);
    }

    private static Command Conversations(CliGlobals globals)
    {
        var limit = new Option<int?>("--limit") { Description = "Page size, 1-100" };
        var after = Optional("--after", "Continue after this conversation ID");
        var revision = Optional("--revision", "Filter to an exact revision's focused and legacy conversations");
        var list = new Command("list", "List workspace and legacy conversations, newest first").With(limit, after, revision);
        list.Handle(globals, async invocation =>
        {
            var request = (await invocation.RequestAsync(ApiOperations.GetWorkspaceChatConversations)).Query("limit", RegressionCommands.LimitValue(invocation, limit))
                .Query("after", invocation.OptionalId(after)).Query("revisionId", invocation.OptionalId(revision));
            var page = await invocation.Api.ReadAsync<ChatOrganizationConversationPageV1>(request, invocation.Cancellation);
            invocation.Output.Write(page, output =>
            {
                output.Table(["CONVERSATION", "KIND", "TITLE", "TURNS", "REVISION"], page.Conversations.Select(item =>
                    (IReadOnlyList<object?>)[item.ConversationId, item.Kind, item.Title, item.TurnCount, item.Revision?.RevisionId ?? item.Focus?.RevisionId]));
                if (page.HasMore && page.Conversations.LastOrDefault() is { } last)
                    output.Line($"Continue with --after {last.ConversationId}.");
                output.Line("Legacy revision conversations continue through weft chat ask/follow with their original revision options.");
            });
        });
        var id = Argument("conversationId", "Workspace conversation ID");
        var get = new Command("get", "Read a workspace conversation and its recorded turn focus").With(id);
        get.Handle(globals, async invocation =>
        {
            var value = await invocation.Api.ReadAsync<ChatWorkspaceConversationDetailV1>((await invocation.RequestAsync(ApiOperations.GetWorkspaceChatConversation))
                .Path("conversationId", invocation.Id(id)), invocation.Cancellation);
            invocation.Output.Write(value, output =>
            {
                output.Fields(("Conversation", value.Conversation.ConversationId), ("Focus mapping", value.Conversation.Focus?.MappingId),
                    ("Exact revision", value.Conversation.Focus?.RevisionId), ("Retained until", value.Conversation.RetainUntilUtc));
                output.Table(["TURN", "PHASE", "FOCUS", "REVISION"], value.Turns.Select(item =>
                    (IReadOnlyList<object?>)[item.TurnId, item.Phase, item.Focus?.State, item.Focus?.Revision?.RevisionId]));
                if (value.HasEarlierTurns) output.Line("Earlier turns are outside this response window.");
            });
        });
        var focus = new Focus();
        var explicitId = Optional("--conversation-id", "Use this ID to recover an unacknowledged creation safely");
        var create = new Command("create", "Start an optional mapping/exact-revision focused workspace conversation").With([explicitId, .. focus.Symbols]);
        create.Handle(globals, async invocation =>
        {
            var value = await Create(invocation, invocation.OptionalId(explicitId) ?? CatalogCommands.NewId(), focus.Value(invocation));
            invocation.Output.Write(value, output => output.Line($"Conversation {value.ConversationId}."));
        });
        return new Command("conversations", "Workspace conversation history").With(list, get, create);
    }

    private sealed class Focus
    {
        private readonly Option<string?> project = Optional("--focus-project", "The focus project ID");
        private readonly Option<string?> mapping = Optional("--focus-mapping", "The mapping whose head is resolved at each turn");
        private readonly Option<string?> revision = Optional("--focus-revision", "Pin focus to this exact revision instead of following the head");
        public Symbol[] Symbols => [project, mapping, revision];
        public ChatFocusV1? Value(CliInvocation invocation)
        {
            var p = invocation.OptionalId(project); var m = invocation.OptionalId(mapping); var r = invocation.OptionalId(revision);
            if (p is null && m is null && r is null) return null;
            if (p is null || m is null) throw CliException.Usage("Focus requires --focus-project and --focus-mapping together; --focus-revision is optional.");
            return new(p, m, r);
        }
    }
    private static async Task<ChatWorkspaceAvailabilityV1> Available(CliInvocation invocation) =>
        await invocation.Api.ReadAsync<ChatWorkspaceAvailabilityV1>(await invocation.RequestAsync(ApiOperations.GetWorkspaceChatAvailability), invocation.Cancellation);
    private static async Task<ChatOrganizationConversationV1> Create(CliInvocation invocation, string id, ChatFocusV1? focus) =>
        await invocation.Api.ReadAsync<ChatOrganizationConversationV1>((await invocation.RequestAsync(ApiOperations.CreateWorkspaceChatConversation))
            .Body(CatalogCommands.JsonBody(new ChatWorkspaceConversationCreateV1(id, focus), ApiOperations.CreateWorkspaceChatConversation)), invocation.Cancellation);

    private static ChatProfileV1 ChooseProfile(ChatWorkspaceAvailabilityV1 available, string? requested)
    {
        if (requested is null) return available.Profiles[0].Profile;
        var colon = requested?.LastIndexOf(':') ?? -1;
        var id = colon < 0 ? requested : requested![..colon];
        int? version = colon < 0 ? null : int.TryParse(requested.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : throw CliException.Usage("--model must be ID or ID:VERSION.");
        return available.Profiles.Select(item => item.Profile).Where(profile => (id is null || profile.ProfileId == id) && (version is null || profile.Version == version))
            .OrderByDescending(profile => profile.Version).FirstOrDefault() ?? throw CliException.Usage("The requested workspace model profile is unavailable.");
    }
    private static async Task<int> Follow(CliInvocation invocation, string conversation, string turn, int after)
    {
        var citations = new List<ChatWorkspaceCitationEventV1>();
        var disclosures = new List<ChatDataSharedEventV1>();
        var organization = await invocation.OrganizationAsync();
        var answer = await ChatCommands.FollowAsync(invocation, StreamScope, conversation, turn, after, item =>
        {
            if (item is ChatWorkspaceCitationEventV1 citation)
            {
                if (citation.Citation.OrganizationId != organization)
                    throw new CliException(CliExitCode.Transport, "The citation does not belong to this workspace.", "invalidResponse");
                citations.Add(citation);
            }
            if (item is ChatDataSharedEventV1 shared) disclosures.Add(shared);
        });
        if (invocation.Output.Json)
        {
            invocation.Output.WriteJson(new
            {
                answer.ConversationId,
                answer.TurnId,
                answer.Outcome,
                answer.Text,
                Citations = citations,
                SharedData = disclosures,
                answer.Usage,
                answer.Failure
            });
            return answer.Outcome == "completed" ? (int)CliExitCode.Success : (int)CliExitCode.OperationFailed;
        }
        var result = ChatCommands.Finish(invocation, answer);
        foreach (var item in citations)
            invocation.Output.Line($"Citation: {item.Citation.Kind} · {item.Citation.SubjectId ?? item.Citation.SectionId ?? item.Citation.MappingId ?? item.Citation.ProjectId}");
        if (disclosures.Count > 0)
            invocation.Output.Line("Data shared with the selected model. This answer may contain sample-derived content and follows conversation retention.");
        return result;
    }
}
