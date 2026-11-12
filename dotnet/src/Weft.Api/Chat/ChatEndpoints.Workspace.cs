using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Ai;
using Weft.Api.Catalog;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Contracts.Chat;
using Weft.Domain;

namespace Weft.Api.Chat;

/// <summary>
/// Organization-level chat: the caller's conversations of both kinds, and workspace conversations with their turns.
/// Revision conversations are listed here and continued through the revision routes. The same idempotency,
/// antiforgery, streaming, reconnect and cancellation rules apply as for revision chat.
/// </summary>
public static partial class ChatEndpoints
{
    private const string OrganizationRoute = "/api/v1/organizations/{organizationId}/chat";

    private static void MapWorkspaceChat(IEndpointRouteBuilder routes)
    {
        const string conversation = OrganizationRoute + "/conversations/{conversationId}";
        const string turn = conversation + "/turns/{turnId}";
        Describe<ChatWorkspaceAvailabilityV1>(routes.MapGet(OrganizationRoute, GetWorkspaceAvailability), "GetWorkspaceChatAvailability",
            "Read the model profiles that can answer organization-wide questions, their data policy and the limits.");
        Describe<ChatOrganizationConversationPageV1>(routes.MapGet(OrganizationRoute + "/conversations", ListOrganizationConversations),
            "GetOrganizationChatConversations",
            "Read the caller's conversations of both kinds in the organization, newest first, optionally about one revision.")
            .DescribeWorkspaceQueries(listing: true);
        Describe<ChatOrganizationConversationV1>(routes.MapPost(OrganizationRoute + "/conversations", CreateWorkspaceConversation),
            "CreateWorkspaceChatConversation", "Create or replay a private workspace conversation, optionally focused on a mapping or revision.",
            StatusCodes.Status201Created).DescribeBody<ChatWorkspaceConversationCreateV1>();
        Describe<ChatWorkspaceConversationDetailV1>(routes.MapGet(conversation, GetWorkspaceConversation), "GetWorkspaceChatConversation",
            "Read one private workspace conversation and its most recent turns.");
        Describe<ChatTurnV1>(routes.MapPost(conversation + "/turns", CreateWorkspaceTurn), "CreateWorkspaceChatTurn",
            "Start or replay a workspace turn identified by its client-generated turn ID.", StatusCodes.Status201Created)
            .DescribeBody<ChatTurnCreateV1>();
        Describe<ChatTurnV1>(routes.MapPost(turn + "/cancel", CancelWorkspaceTurn), "CancelWorkspaceChatTurn",
            "Cancel an active workspace turn; a terminal turn is returned unchanged.");
        Describe<ChatTurnEventV1>(routes.MapGet(turn + "/events", StreamWorkspaceTurn), "StreamWorkspaceChatTurnEvents",
            "Stream the workspace turn's durable events after a sequence number using SSE framing.", contentType: "text/event-stream")
            .DescribeWorkspaceQueries(listing: false);
        Describe<ChatMappingWriteReceiptPageV1>(routes.MapGet(turn + "/mapping-writes", MappingWriteReceipts), "GetChatMappingWriteReceipts",
            "Read durable save receipts independently of turn streaming, with fresh creator and project access.");
        Describe<ChatMappingWriteReceiptPageV1>(routes.MapPost(turn + "/mapping-writes/recover", MappingWriteReceipts), "RecoverChatMappingWriteReceipts",
            "Resume already reserved saves using their original exact bytes and keys, with current edit permission. Never rerun a turn.");
    }

    /// <summary>Profiles that can run a workspace turn: selectable, with function tools and permission to receive mapping metadata.</summary>
    private static IEnumerable<ChatModelProfile> WorkspaceProfiles(ChatApiOptions options) => options.Profiles.Where(profile => profile.Selectable &&
        (profile.Spec.Capabilities & AiCapability.FunctionTools) != 0 && profile.Spec.MappingMetadataAllowed);

    private static Task<IResult> GetWorkspaceAvailability(string organizationId, HttpContext context) =>
        WithChat(context, chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            if (ChatRequestParser.Id(organizationId) != chat.OrganizationId) return Task.FromResult(NotFound());
            if (chat.Host.Workspace is null) return Task.FromResult(Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
            var profiles = WorkspaceProfiles(chat.Options).Select(profile => new ChatWorkspaceProfileV1(ChatPublicMapper.Profile(profile),
                profile.Spec.PayloadsAllowed && chat.Host.Workspace.PayloadManifest is not null)).ToArray();
            if (profiles.Length == 0) return Task.FromResult(Error(ChatErrorCodeV1.ProfileUnavailable, StatusCodes.Status409Conflict));
            return Task.FromResult(Results.Ok(new ChatWorkspaceAvailabilityV1(ChatPublicMapper.Id(chat.OrganizationId), profiles,
                new(ChatApiOptions.MaximumTextBytes, ChatApiOptions.MaximumSelectors, (int)chat.Options.TurnDuration.TotalSeconds))));
        });

    private static Task<IResult> ListOrganizationConversations(string organizationId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request, "limit", "after", "revisionId");
            var limit = ChatRequestParser.Number(context.Request, "limit", 1, 100, 50);
            var after = ChatRequestParser.OptionalId(context.Request, "after");
            var revisionId = ChatRequestParser.OptionalId(context.Request, "revisionId");
            if (ChatRequestParser.Id(organizationId) != chat.OrganizationId) return NotFound();
            RevisionScope? revision = null;
            if (revisionId is { } id)
            {
                // The filter names a revision by identity; it must be one the caller can read now.
                revision = await context.RequestServices.GetRequiredService<ICatalogResourceLocator>()
                    .LocateRevisionAsync(chat.Actor, chat.OrganizationId, id, context.RequestAborted);
                if (revision is null) return NotFound();
            }
            var page = await chat.Host.Stores.ReaderListings.ListOrganizationConversationsAsync(chat.Actor, chat.OrganizationId, revision, limit,
                after, context.RequestAborted);
            return Results.Ok(new ChatOrganizationConversationPageV1([.. page.Conversations.Select(ChatPublicMapper.OrganizationConversation)],
                page.HasMore));
        });

    private static Task<IResult> CreateWorkspaceConversation(string organizationId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            if (ChatRequestParser.Id(organizationId) != chat.OrganizationId) return NotFound();
            var (id, requested) = await ChatRequestParser.ReadWorkspaceConversationAsync(context.Request, context.RequestAborted);
            if (chat.Host.Workspace is null) return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status404NotFound);
            var key = ChatConversationKey.ForWorkspace(chat.OrganizationId, chat.Actor.PrincipalId, id);
            var listings = chat.Host.Stores.ReaderListings;
            // The client-generated ID is the idempotency key; the focus chosen by the original creation is retained.
            ChatConversationFocus? focus = null;
            if (requested is { } value)
            {
                var mapping = new MappingScope(chat.OrganizationId, value.Project, value.Mapping);
                focus = new ChatConversationFocus(mapping, value.Revision);
            }
            if (await listings.GetConversationAsync(chat.Actor, key, context.RequestAborted) is { } existing)
                return existing.Conversation.Focus == focus ? Results.Ok(ChatPublicMapper.OrganizationConversation(existing))
                    : Error(ChatErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
            // A focus must be readable now; it is stored as context and grants nothing later.
            if (focus is not null && (focus.Revision is { } revision
                    ? await CatalogEndpoints.ReadableRevisionAsync(context.RequestServices, chat.Actor, revision, context.RequestAborted) is null
                    : await CatalogEndpoints.ReadableMappingAsync(context.RequestServices, chat.Actor, focus.Mapping, context.RequestAborted) is null))
                return NotFound();
            var intent = ChatConversationIntent.ForWorkspace(key, focus, chat.Host.Now + chat.Options.Retention);
            var outcome = await chat.Host.Stores.WriterConversations.CreateAsync(chat.Actor, intent, context.RequestAborted);
            if (outcome is not (ChatStoreOutcome.Applied or ChatStoreOutcome.Replayed))
                return outcome switch
                {
                    ChatStoreOutcome.NotAuthorizedOrNotFound => NotFound(),
                    ChatStoreOutcome.Conflict or ChatStoreOutcome.IdentityRetired =>
                        Error(ChatErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict),
                    _ => Error(ChatErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError)
                };
            var created = await listings.GetConversationAsync(chat.Actor, key, context.RequestAborted);
            if (created is null) return NotFound();
            var body = ChatPublicMapper.OrganizationConversation(created);
            return outcome == ChatStoreOutcome.Applied ? Results.Json(body, statusCode: StatusCodes.Status201Created) : Results.Ok(body);
        }, unsafeRequest: true);

    private static Task<IResult> GetWorkspaceConversation(string organizationId, string conversationId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var key = WorkspaceKey(chat, organizationId, conversationId);
            if (key is null) return NotFound();
            var listings = chat.Host.Stores.ReaderListings;
            var summary = await listings.GetConversationAsync(chat.Actor, key, context.RequestAborted);
            var turns = summary is null ? null : await listings.ListTurnsAsync(chat.Actor, key, 100, context.RequestAborted);
            if (summary is null || turns is null) return NotFound();
            return Results.Ok(new ChatWorkspaceConversationDetailV1(ChatPublicMapper.OrganizationConversation(summary),
                [.. turns.Turns.Select(turn => ChatPublicMapper.Turn(turn, key.ConversationId))], turns.HasEarlier));
        });

    private static Task<IResult> CreateWorkspaceTurn(string organizationId, string conversationId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var key = WorkspaceKey(chat, organizationId, conversationId);
            if (key is null) return NotFound();
            var request = await ChatRequestParser.ReadTurnAsync(context.Request, context.RequestAborted);
            if (chat.Host.Workspace is not { } workspace) return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status404NotFound);
            var summary = await chat.Host.Stores.ReaderListings.GetConversationAsync(chat.Actor, key, context.RequestAborted);
            if (summary is null) return NotFound();
            // A new turn pins the manifest the profile and the creator's grants allow, and records the focus resolved now.
            return await StartTurnAsync(context, chat, key, summary, request, async profile =>
            {
                var policy = await chat.Host.WorkspaceGroundingPolicyAsync(chat.Actor, chat.OrganizationId, profile, context.RequestAborted);
                return policy is null ? (null, null)
                    : (policy, await workspace.ResolveFocusAsync(chat.Actor, summary.Conversation, context.RequestAborted));
            });
        }, unsafeRequest: true);

    private static Task<IResult> CancelWorkspaceTurn(string organizationId, string conversationId, string turnId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            await ChatRequestParser.RequireNoBodyAsync(context.Request, context.RequestAborted);
            var key = WorkspaceKey(chat, organizationId, conversationId);
            var turnKey = ChatRequestParser.Id(turnId);
            if (key is null) return NotFound();
            return await CancelTurnAsync(context, chat, key, turnKey);
        }, unsafeRequest: true);

    private static Task<IResult> StreamWorkspaceTurn(string organizationId, string conversationId, string turnId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request, "after");
            var after = ChatRequestParser.Number(context.Request, "after", 0, ChatGrounding.MaxEvents, 0);
            var key = WorkspaceKey(chat, organizationId, conversationId);
            var turnKey = ChatRequestParser.Id(turnId);
            if (key is null) return NotFound();
            var turn = await chat.Host.Stores.ReaderTurns.ReadTurnAsync(chat.Actor, key, turnKey, context.RequestAborted);
            if (turn is null) return NotFound();
            return new ChatEventStreamResult(chat, key, turn, after);
        });

    /// <summary>The caller's workspace conversation key in their admitted organization; null for another organization.</summary>
    private static ChatConversationKey? WorkspaceKey(ChatRequestContext chat, string organizationId, string conversationId)
    {
        var organization = ChatRequestParser.Id(organizationId);
        var id = ChatRequestParser.Id(conversationId);
        return organization == chat.OrganizationId ? ChatConversationKey.ForWorkspace(organization, chat.Actor.PrincipalId, id) : null;
    }
}
