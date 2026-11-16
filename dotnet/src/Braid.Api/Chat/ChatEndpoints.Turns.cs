using System.Text;
using Microsoft.AspNetCore.Http;
using Braid.Application;
using Braid.Contracts.Chat;
using Braid.Domain;

namespace Braid.Api.Chat;

public static partial class ChatEndpoints
{
    private static readonly TimeSpan OutcomeWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(10);

    private static Task<IResult> CreateTurn(string organizationId, string projectId, string mappingId, string revisionId,
        string conversationId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var path = PathScope(organizationId, projectId, mappingId, revisionId);
            var id = ChatRequestParser.Id(conversationId);
            if (path.OrganizationId != chat.OrganizationId) return NotFound();
            var request = await ChatRequestParser.ReadTurnAsync(context.Request, context.RequestAborted);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            var key = new ChatConversationKey(scope, chat.Actor.PrincipalId, id);
            var summary = await chat.Host.Stores.ReaderListings.GetConversationAsync(chat.Actor, key, context.RequestAborted);
            if (summary is null) return NotFound();
            return await StartTurnAsync(context, chat, key, summary, request,
                profile => Task.FromResult<(ChatGroundingPolicyPin?, ChatTurnFocus?)>((chat.Host.GroundingPolicy(profile), null)));
        }, unsafeRequest: true);

    /// <summary>
    /// Starts or replays one turn of a conversation already read under fresh authority. The grounding is chosen only
    /// for a new turn, after replay, so a replayed request never re-observes what its original pinned.
    /// </summary>
    private static async Task<IResult> StartTurnAsync(HttpContext context, ChatRequestContext chat, ChatConversationKey key,
        ChatConversationSummary summary, ChatTurnRequest request,
        Func<ChatModelProfile, Task<(ChatGroundingPolicyPin? Grounding, ChatTurnFocus? Focus)>> grounding)
    {
        var id = key.ConversationId;
        var stores = chat.Host.Stores;
        // Replay compares only browser-supplied input; server-chosen deadline and budgets stay with the original.
        if (await stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, request.TurnId, context.RequestAborted) is { } existing)
            return Replay(existing, request, id);
        var profile = chat.Options.Find(request.ProfileId, request.ProfileVersion);
        if (profile is null) return Error(ChatErrorCodeV1.ProfileUnavailable, StatusCodes.Status409Conflict);
        var turns = await stores.ReaderListings.ListTurnsAsync(chat.Actor, key, 100, context.RequestAborted);
        if (turns is null) return NotFound();
        if (turns.Turns.Any(turn => turn.Phase == ChatTurnPhase.Active))
            return Error(ChatErrorCodeV1.TurnActive, StatusCodes.Status409Conflict);
        var now = chat.Host.Now;
        var deadline = now + chat.Options.TurnDuration;
        if (deadline > summary.Conversation.RetainUntilUtc)
            return Error(ChatErrorCodeV1.TurnNotStarted, StatusCodes.Status409Conflict);
        var (policy, focus) = await grounding(profile);
        if (policy is null) return Error(ChatErrorCodeV1.ProfileUnavailable, StatusCodes.Status409Conflict);
        var envelope = new ChatOriginalEnvelope(request.TurnId, request.Text, request.Selectors,
            ChatGrounding.ProviderBudget(chat.Options, policy), policy, focus);
        var intent = new ChatTurnIntent(request.TurnId, envelope, profile.Pin, ChatGrounding.TurnBudgetFor(policy), deadline);
        var execution = chat.Host.Begin(chat.Actor, key, profile, intent, History(turns.Turns));
        if (execution is null)
            return await stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, request.TurnId, context.RequestAborted) is { } live
                ? Replay(live, request, id) : Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        // The execution outlives this request: a disconnect here neither cancels nor repeats provider work.
        if (await execution.Started.WaitAsync(context.RequestAborted) is { } started)
            return Results.Json(ChatPublicMapper.Turn(started, id), statusCode: StatusCodes.Status201Created);
        ChatOrchestrationOutcome? outcome = null;
        try { outcome = (await execution.Completion.WaitAsync(OutcomeWait, context.RequestAborted)).Outcome; }
        catch (TimeoutException) { }
        if (await stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, request.TurnId, context.RequestAborted) is { } concurrent)
            return Replay(concurrent, request, id);
        var after = await stores.ReaderListings.ListTurnsAsync(chat.Actor, key, 100, context.RequestAborted);
        if (after is null) return NotFound();
        if (after.Turns.Any(turn => turn.Phase == ChatTurnPhase.Active))
            return Error(ChatErrorCodeV1.TurnActive, StatusCodes.Status409Conflict);
        return outcome switch
        {
            ChatOrchestrationOutcome.Denied => NotFound(),
            ChatOrchestrationOutcome.InvalidRequest => Error(ChatErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest),
            _ => Error(ChatErrorCodeV1.TurnNotStarted, StatusCodes.Status409Conflict)
        };
    }

    private static Task<IResult> CancelTurn(string organizationId, string projectId, string mappingId, string revisionId,
        string conversationId, string turnId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            await ChatRequestParser.RequireNoBodyAsync(context.Request, context.RequestAborted);
            var id = ChatRequestParser.Id(conversationId);
            var turnKey = ChatRequestParser.Id(turnId);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            return await CancelTurnAsync(context, chat, new ChatConversationKey(scope, chat.Actor.PrincipalId, id), turnKey);
        }, unsafeRequest: true);

    private static async Task<IResult> CancelTurnAsync(HttpContext context, ChatRequestContext chat, ChatConversationKey key, Guid turnKey)
    {
        var reads = chat.Host.Stores.ReaderTurns;
        var turn = await reads.ReadTurnAsync(chat.Actor, key, turnKey, context.RequestAborted);
        if (turn is null) return NotFound();
        if (turn.Phase == ChatTurnPhase.Active)
        {
            if (chat.Host.Live(key, turnKey) is { } execution)
            {
                // Cancellation closes the live provider stream; the original execution publishes the terminal.
                try { await execution.CancelAsync().WaitAsync(CancelWait, context.RequestAborted); }
                catch (Exception error) when (error is TimeoutException or ChatOrchestrationCleanupException) { }
                try { await execution.Completion.WaitAsync(CancelWait, context.RequestAborted); }
                catch (TimeoutException) { }
            }
            else
            {
                // No execution here owns it: a conditional terminal write, never a provider call.
                await chat.Host.WriterTurns.FinishAsync(chat.Actor, key, turnKey, turn.Version, ChatTurnPhase.Cancelled, context.RequestAborted);
            }
            turn = await reads.ReadTurnAsync(chat.Actor, key, turnKey, context.RequestAborted);
            if (turn is null) return NotFound();
        }
        return Results.Ok(ChatPublicMapper.Turn(turn, key.ConversationId));
    }

    private static IResult Replay(ChatTurnFacts existing, ChatTurnRequest request, Guid conversationId)
    {
        var original = existing.Intent;
        return original.OriginalRequest == request.Text && original.Profile.ProfileId == request.ProfileId &&
            original.Profile.Version == request.ProfileVersion && original.Envelope.Selectors.SequenceEqual(request.Selectors)
            ? Results.Ok(ChatPublicMapper.Turn(existing, conversationId))
            : Error(ChatErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// The most recent contiguous completed exchanges that fit the orchestrator's history bounds. Stored event
    /// bytes bound decoded answer text from above, so a selected history never exceeds those bounds.
    /// </summary>
    private static Guid[] History(IReadOnlyList<ChatTurnFacts> turns)
    {
        var selected = new List<Guid>();
        int events = 0, bytes = 0;
        for (var i = turns.Count - 1; i >= 0 && selected.Count < ChatGrounding.Limits.MaxHistoryTurns; i--)
        {
            var turn = turns[i];
            if (turn.Phase != ChatTurnPhase.Completed) continue;
            var size = Encoding.UTF8.GetByteCount(turn.Intent.OriginalRequest) + turn.EventBytes;
            if (turn.EventCount > ChatGrounding.Limits.MaxHistoryEvents - events || size > ChatGrounding.Limits.MaxHistoryBytes - bytes)
                break;
            events += turn.EventCount;
            bytes += size;
            selected.Add(turn.Intent.TurnId);
        }
        selected.Reverse();
        return [.. selected];
    }
}
