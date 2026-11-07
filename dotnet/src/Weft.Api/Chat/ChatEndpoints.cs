using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Ai;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Chat;
using Weft.Domain;

namespace Weft.Api.Chat;

internal sealed record ChatRequestContext(ActorContext Actor, Guid OrganizationId, ChatTurnHost Host, ChatApiOptions Options)
{
    public override string ToString() => nameof(ChatRequestContext);
}

/// <summary>
/// Creator-private, revision-pinned chat routes behind the existing catalog identity boundary. Every
/// operation reads fresh project/revision authority; the ledger independently rechecks membership.
/// </summary>
public static partial class ChatEndpoints
{
    internal const string RevisionRoute =
        "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/revisions/{revisionId}/chat";

    /// <summary>Registers chat. A disabled default options instance maps routes that answer unavailable.</summary>
    public static IServiceCollection AddWeftChat(this IServiceCollection services, ChatApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (services.Any(item => item.ServiceType == typeof(ChatApiOptions)))
            throw new InvalidOperationException("Chat must be registered exactly once.");
        services.AddSingleton(options);
        if (options.Enabled)
            // Resolved lazily on first admitted chat use, after the host has supplied its ports.
            services.AddSingleton(provider =>
            {
                var workspace = provider.GetService<WorkspaceToolService>();
                return new ChatTurnHost(options, provider.GetRequiredService<ChatPersistencePorts>(),
                    provider.GetRequiredService<IAiCredentialResolver>(), provider.GetService<ChatToolRegistry>() ?? ChatToolRegistry.Empty,
                    new ChatProfilePolicy(options, provider.GetRequiredService<ICatalogReader>(), workspace is null ? null : workspace.AllowsAsync),
                    provider.GetService<TimeProvider>() ?? TimeProvider.System, workspace);
            });
        return services;
    }

    public static IEndpointRouteBuilder MapWeftChat(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var routes = endpoints.BusinessRoutes();
        const string conversation = RevisionRoute + "/conversations/{conversationId}";
        const string turn = conversation + "/turns/{turnId}";
        Describe<ChatAvailabilityV1>(routes.MapGet(RevisionRoute, GetAvailability), "GetChatAvailability",
            "Read the configured model profiles and limits for an authorized revision.");
        Describe<ChatConversationPageV1>(routes.MapGet(RevisionRoute + "/conversations", ListConversations), "GetChatConversations",
            "Read the caller's most recent private conversations about this revision.")
            .DescribeNumber("limit", 1, 100, 50, "The number of most recent conversations to return.");
        Describe<ChatConversationV1>(routes.MapPost(RevisionRoute + "/conversations", CreateConversation), "CreateChatConversation",
            "Create or replay a private conversation pinned to the revision's current verified reports.", StatusCodes.Status201Created)
            .DescribeBody<ChatConversationCreateV1>();
        Describe<ChatConversationDetailV1>(routes.MapGet(conversation, GetConversation), "GetChatConversation",
            "Read one private conversation and its most recent turns.");
        Describe<ChatTurnV1>(routes.MapPost(conversation + "/turns", CreateTurn), "CreateChatTurn",
            "Start or replay a turn identified by its client-generated turn ID.", StatusCodes.Status201Created)
            .DescribeBody<ChatTurnCreateV1>();
        Describe<ChatTurnV1>(routes.MapPost(turn + "/cancel", CancelTurn), "CancelChatTurn",
            "Cancel an active turn; a terminal turn is returned unchanged.");
        Describe<ChatTurnEventV1>(routes.MapGet(turn + "/events", StreamTurn), "StreamChatTurnEvents",
            "Stream the turn's durable events after a sequence number using SSE framing.", contentType: "text/event-stream")
            .DescribeNumber("after", 0, ChatGrounding.MaxEvents, 0, "Stream only events with a greater sequence number.");
        MapWorkspaceChat(routes);
        return endpoints;
    }

    // The only query field an operation accepts: a canonical decimal within its bounds, or the default when absent.
    private static RouteHandlerBuilder DescribeNumber(this RouteHandlerBuilder route, string name, int minimum, int maximum,
        int fallback, string description) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Query,
                Description = $"{description} A canonical decimal from {minimum} to {maximum}, {fallback} when absent.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Minimum = minimum.ToString(CultureInfo.InvariantCulture),
                    Maximum = maximum.ToString(CultureInfo.InvariantCulture),
                    Default = JsonValue.Create(fallback)
                }
            });
            return Task.CompletedTask;
        });

    private static RouteHandlerBuilder Describe<T>(RouteHandlerBuilder route, string name, string summary,
        int success = StatusCodes.Status200OK, string contentType = "application/json")
    {
        route.WithName(name).WithTags("Chat").WithSummary(summary).Produces<T>(StatusCodes.Status200OK, contentType);
        if (success != StatusCodes.Status200OK) route.Produces<T>(success, contentType);
        foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 415, 429, 500, 503 })
            route.Produces<ChatErrorV1>(status);
        return route.DescribeChatResponseHeaders(contentType == "text/event-stream");
    }

    private static Task<IResult> GetAvailability(string organizationId, string projectId, string mappingId, string revisionId,
        HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            var profiles = chat.Options.Profiles.Where(profile => profile.Selectable).Select(ChatPublicMapper.Profile).ToArray();
            if (profiles.Length == 0) return Error(ChatErrorCodeV1.ProfileUnavailable, StatusCodes.Status409Conflict);
            return Results.Ok(new ChatAvailabilityV1(CatalogPublicMapper.RevisionScope(scope), profiles,
                new(ChatApiOptions.MaximumTextBytes, ChatApiOptions.MaximumSelectors, (int)chat.Options.TurnDuration.TotalSeconds),
                chat.Host.ToolsEnabled));
        });

    private static Task<IResult> ListConversations(string organizationId, string projectId, string mappingId, string revisionId,
        HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request, "limit");
            var limit = ChatRequestParser.Number(context.Request, "limit", 1, 100, 50);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            var page = await chat.Host.Stores.ReaderListings.ListConversationsAsync(chat.Actor, scope, limit, context.RequestAborted);
            return Results.Ok(new ChatConversationPageV1([.. page.Conversations.Select(ChatPublicMapper.Conversation)], page.HasMore));
        });

    private static Task<IResult> GetConversation(string organizationId, string projectId, string mappingId, string revisionId,
        string conversationId, HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var id = ChatRequestParser.Id(conversationId);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            var key = new ChatConversationKey(scope, chat.Actor.PrincipalId, id);
            var summary = await chat.Host.Stores.ReaderListings.GetConversationAsync(chat.Actor, key, context.RequestAborted);
            var turns = summary is null ? null : await chat.Host.Stores.ReaderListings.ListTurnsAsync(chat.Actor, key, 100, context.RequestAborted);
            if (summary is null || turns is null) return NotFound();
            return Results.Ok(new ChatConversationDetailV1(ChatPublicMapper.Conversation(summary),
                [.. turns.Turns.Select(turn => ChatPublicMapper.Turn(turn, id))], turns.HasEarlier));
        });

    private static Task<IResult> CreateConversation(string organizationId, string projectId, string mappingId, string revisionId,
        HttpContext context) =>
        WithChat(context, async chat =>
        {
            ChatRequestParser.RequireQuery(context.Request);
            var path = PathScope(organizationId, projectId, mappingId, revisionId);
            if (path.OrganizationId != chat.OrganizationId) return NotFound();
            var id = await ChatRequestParser.ReadConversationAsync(context.Request, context.RequestAborted);
            var scope = await RevisionAsync(context, chat, organizationId, projectId, mappingId, revisionId);
            if (scope is null) return NotFound();
            var key = new ChatConversationKey(scope, chat.Actor.PrincipalId, id);
            var listings = chat.Host.Stores.ReaderListings;
            // The client-generated ID is the idempotency key; report pins chosen by the original creation are retained.
            if (await listings.GetConversationAsync(chat.Actor, key, context.RequestAborted) is { } existing)
                return Results.Ok(ChatPublicMapper.Conversation(existing));
            var pins = await ReportPinsAsync(context, chat.Actor, scope);
            if (pins is null) return Error(ChatErrorCodeV1.ReportsUnavailable, StatusCodes.Status409Conflict);
            var intent = new ChatConversationIntent(key, pins.Value.Projection, pins.Value.Validation, chat.Host.Now + chat.Options.Retention);
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
            var body = ChatPublicMapper.Conversation(created);
            return outcome == ChatStoreOutcome.Applied ? Results.Json(body, statusCode: StatusCodes.Status201Created) : Results.Ok(body);
        }, unsafeRequest: true);

    private static async Task<(ChatReportPin Projection, ChatReportPin Validation)?> ReportPinsAsync(HttpContext context,
        ActorContext actor, RevisionScope scope)
    {
        if (context.RequestServices.GetService<IRevisionReportReader>() is not { } reports) return null;
        try
        {
            var projection = await reports.GetCurrentAsync(actor, scope, RevisionReportOperation.Projection, null, context.RequestAborted);
            var validation = await reports.GetCurrentAsync(actor, scope, RevisionReportOperation.Validation, null, context.RequestAborted);
            if (projection.Report is not VerifiedProjectionReport verifiedProjection ||
                validation.Report is not VerifiedValidationReport verifiedValidation)
                return null;
            return (Pin(verifiedProjection.Metadata, scope), Pin(verifiedValidation.Metadata, scope));
        }
        catch (RevisionReportServiceException) when (!context.RequestAborted.IsCancellationRequested)
        {
            throw new ChatRequestException(ChatErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        }

        static ChatReportPin Pin(RevisionReportResult metadata, RevisionScope scope)
        {
            if (metadata.Key.Scope != scope) throw new InvalidOperationException("The chat report scope is inconsistent.");
            return new(metadata.CacheEntryId, metadata.DerivationId, metadata.Key.Fingerprint, metadata.DerivedObject.Digest);
        }
    }

    private static async Task<IResult> WithChat(HttpContext context, Func<ChatRequestContext, Task<IResult>> action,
        bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.RequestServices.GetService<ChatApiOptions>() is not { Enabled: true } options)
            return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status404NotFound);
        try
        {
            var boundary = context.RequestServices.GetRequiredService<CatalogRequestBoundary>();
            var admission = await boundary.AuthenticateAsync(context, unsafeRequest, context.RequestAborted);
            if (!admission.IsAdmitted)
                return Error(admission.Error switch
                {
                    CatalogErrorCodeV1.IdentityRejected => ChatErrorCodeV1.IdentityRejected,
                    CatalogErrorCodeV1.InvalidRequest => ChatErrorCodeV1.InvalidRequest,
                    CatalogErrorCodeV1.RateLimited => ChatErrorCodeV1.RateLimited,
                    _ => ChatErrorCodeV1.Unavailable
                }, admission.StatusCode);
            var host = context.RequestServices.GetRequiredService<ChatTurnHost>();
            return await action(new(admission.Actor!, admission.AllowedOrganizationId!.Value, host, options));
        }
        catch (ChatRequestException error)
        {
            return Error(error.Code, error.StatusCode);
        }
        catch (CatalogSessionBoundaryUnavailableException)
        {
            return Error(ChatErrorCodeV1.Unavailable, StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            throw;
        }
        catch (InvalidOperationException) when (!context.Response.HasStarted)
        {
            return Error(ChatErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError);
        }
        catch (Exception) when (!context.Response.HasStarted)
        {
            return Error(ChatErrorCodeV1.InternalFailure, StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>Fresh catalog revision authority; null when absent, foreign or not readable.</summary>
    private static async Task<RevisionScope?> RevisionAsync(HttpContext context, ChatRequestContext chat,
        string organizationId, string projectId, string mappingId, string revisionId)
    {
        var scope = PathScope(organizationId, projectId, mappingId, revisionId);
        if (scope.OrganizationId != chat.OrganizationId) return null;
        var revision = await context.RequestServices.GetRequiredService<ICatalogReader>()
            .GetRevisionAsync(chat.Actor, scope, context.RequestAborted);
        context.RequestAborted.ThrowIfCancellationRequested();
        if (revision is null) return null;
        var access = revision.Access;
        if (revision.Summary.Scope != scope || access.Scope != new ProjectScope(scope.OrganizationId, scope.ProjectId) ||
            access.PrincipalId != chat.Actor.PrincipalId)
            throw new InvalidOperationException("The chat revision scope is inconsistent.");
        return access.Can(ProjectAction.ReadMetadata) ? scope : null;
    }

    private static RevisionScope PathScope(string organizationId, string projectId, string mappingId, string revisionId) =>
        new(ChatRequestParser.Id(organizationId), ChatRequestParser.Id(projectId), ChatRequestParser.Id(mappingId),
            ChatRequestParser.Id(revisionId));

    private static IResult NotFound() => Error(ChatErrorCodeV1.NotFound, StatusCodes.Status404NotFound);
    private static IResult Error(ChatErrorCodeV1 code, int status) => Results.Json(new ChatErrorV1(code), statusCode: status);
}
