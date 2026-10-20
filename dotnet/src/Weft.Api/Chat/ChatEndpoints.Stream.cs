using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Weft.Application;
using Weft.Contracts.Chat;
using Weft.Domain;

namespace Weft.Api.Chat;

public static partial class ChatEndpoints
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // Ledger writes by this process wake followers at once; the fallback poll only bounds missed signals.
    private static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StreamGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Replays durable events after a sequence, then follows the live turn until its terminal event.
    /// Reading never starts, resumes or repeats provider work; an abandoned turn is closed as interrupted.
    /// </summary>
    private static Task<IResult> StreamTurn(string organizationId, string projectId, string mappingId, string revisionId,
        string conversationId, string turnId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request, "after");
            var after = ChatRequestParser.Number(context.Request, "after", 0, ChatGrounding.MaxEvents, 0);
            var id = ChatRequestParser.Id(conversationId);
            var turnKey = ChatRequestParser.Id(turnId);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            var key = new ChatConversationKey(scope, chat.Actor.PrincipalId, id);
            var turn = await chat.Host.Stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, turnKey, context.RequestAborted);
            if (turn is null) return NotFound();
            return new ChatEventStreamResult(chat, key, turn, after);
        });

    private sealed class ChatEventStreamResult(ChatRequestContext chat, ChatConversationKey key, ChatTurnFacts turn, int after) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            var stopping = context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            var ct = linked.Token;
            var json = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var reads = chat.Host.Stores.ReaderTurns;
            var turnId = turn.Intent.TurnId;
            var sequence = (long)after;
            var lastWrite = chat.Host.Now;
            // Followers are bounded by the turn deadline; an unfinished turn is then abandoned and closed.
            var horizon = turn.Intent.DeadlineUtc + StreamGrace;
            using var follower = chat.Host.Follow(key, turnId);
            try
            {
                await response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    var changed = follower.Changed;
                    var page = await reads.ReadEventsAsync(chat.Actor, key, turnId, sequence, 100, ct);
                    var builder = new StringBuilder();
                    var terminal = false;
                    foreach (var stored in page)
                    {
                        sequence = stored.Sequence;
                        if (ChatPublicMapper.Event(stored) is { } item) Frame(builder, item, json);
                        if (ChatPublicMapper.Terminal(stored.Kind)) { terminal = true; break; }
                    }
                    if (builder.Length != 0)
                    {
                        await response.WriteAsync(builder.ToString(), ct);
                        await response.Body.FlushAsync(ct);
                        lastWrite = chat.Host.Now;
                    }
                    if (terminal) return;
                    if (page.Count == 100) continue;
                    var current = await reads.ReadTurnAsync(chat.Actor, key, turnId, ct);
                    // Revocation, expiry or a closed turn whose terminal was already read end the stream.
                    if (current is null || current.Phase != ChatTurnPhase.Active && current.EventCount <= sequence) return;
                    if (current.EventCount > sequence) continue;
                    // A refused conditional close (for example a concurrent write) is retried only after the bounded wait.
                    if (chat.Host.Abandoned(key, current) && (await chat.Host.WriterTurns.FinishAsync(chat.Actor, key, turnId,
                        current.Version, ChatTurnPhase.Interrupted, ct)).Outcome == ChatStoreOutcome.Applied)
                        continue;
                    if (chat.Host.Now > horizon) return;
                    if (chat.Host.Now - lastWrite >= Heartbeat)
                    {
                        await response.WriteAsync(": keep-alive\n\n", ct);
                        await response.Body.FlushAsync(ct);
                        lastWrite = chat.Host.Now;
                    }
                    await chat.Host.WaitForChangeAsync(changed,
                        chat.Host.Live(key, turnId) is null ? PollInterval : LivePollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                // Headers are committed; end without a fabricated event. The client observes no terminal and reconnects.
                context.Abort();
            }
        }

        private static void Frame(StringBuilder builder, ChatTurnEventV1 item, JsonSerializerOptions json)
        {
            builder.Append("id: ").Append(item.Sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("event: ").Append(ChatPublicMapper.Name(item)).Append('\n');
            // Compact JSON never contains a raw line break, so one data line carries the whole event.
            builder.Append("data: ").Append(JsonSerializer.Serialize(item, json)).Append("\n\n");
        }

        public override string ToString() => nameof(ChatEventStreamResult);
    }
}
