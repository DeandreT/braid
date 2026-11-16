using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Contracts.Chat;
using Braid.Domain;

namespace Braid.Api.Chat;

public static partial class ChatEndpoints
{
    private static Task<IResult> MappingWriteReceipts(string organizationId, string conversationId, string turnId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var resume = HttpMethods.IsPost(context.Request.Method);
            if (resume) await ChatRequestParser.RequireNoBodyAsync(context.Request, context.RequestAborted);
            var key = WorkspaceKey(chat, organizationId, conversationId);
            var turn = ChatRequestParser.Id(turnId);
            if (key is null || await chat.Host.Stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, turn, context.RequestAborted) is null) return NotFound();
            if (context.RequestServices.GetService<ChatMappingWriteRecovery>() is not { } recovery)
                return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status404NotFound);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            bounded.CancelAfter(TimeSpan.FromSeconds(60));
            var receipts = await recovery.ReadAsync(chat.Actor, key, turn,
                resume && context.RequestServices.GetService<Catalog.CatalogMappingEditOptions>()?.Enabled == true, bounded.Token);
            return Results.Ok(new ChatMappingWriteReceiptPageV1(receipts.Select(receipt => new ChatMappingWriteReceiptV1(
                ChatPublicMapper.Id(receipt.Key), receipt.Status, receipt.Saved is { } saved
                    ? ChatPublicMapper.Event(new ChatStoredEvent(1, receipt.Key, ChatEventKind.MappingWrite, "", receipt.RoundId,
                        ChatMetadataCodec.MappingWrite(saved))) : null)).ToArray()));
        }, unsafeRequest: HttpMethods.IsPost(context.Request.Method));
}
