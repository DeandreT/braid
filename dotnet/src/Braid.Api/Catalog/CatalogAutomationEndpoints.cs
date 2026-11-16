using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Auth;
using Braid.Application.Automation;
using Braid.Contracts.Automation;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Catalog;

public sealed record CatalogAutomationOptions(bool Enabled);

public static partial class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapBraidAutomation(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.BusinessRoutes();
        MapOrganizationNotifications(routes);
        const string project = "/api/v1/organizations/{organizationId}/projects/{projectId}";
        AutoRead<AutomationPolicyV1>(routes, project + "/automation", "GetAutomationPolicy", "policy");
        AutoWrite<AutomationPolicyV1>(routes, project + "/automation", "PutAutomationPolicy", "policy");
        AutoRead<MappingAutomationV1>(routes, project + "/mappings/{resourceId}/automation", "GetMappingAutomation", "mapping");
        AutoWrite<MappingAutomationV1>(routes, project + "/mappings/{resourceId}/automation", "PutMappingAutomation", "mapping");
        AutoRead<RegressionSuitesV1>(routes, project + "/mappings/{resourceId}/regression-suites", "GetRegressionSuites", "suites", true);
        AutoRead<RegressionSuiteV1>(routes, project + "/regression-suites/{resourceId}", "GetRegressionSuite", "suite");
        AutoWrite<RegressionRecheckV1>(routes, project + "/regression-suites/{resourceId}", "PutRegressionRecheck", "recheck");
        AutoRead<RegressionEvaluationsV1>(routes, project + "/regression-suites/{resourceId}/cases", "GetRegressionEvaluations", "evaluations", true);
        AutoRead<NotificationSubscriptionV1>(routes, project + "/notification-subscription", "GetNotificationSubscription", "subscription");
        AutoWrite<NotificationSubscriptionV1>(routes, project + "/notification-subscription", "PutNotificationSubscription", "subscription");
        AutoRead<NotificationsV1>(routes, project + "/notifications", "GetNotifications", "notifications", true);
        AutoWrite<NotificationReadV1>(routes, project + "/notifications/{resourceId}/read", "ReadNotification", "read");
        AutoRead<NotificationDeliveriesV1>(routes, project + "/notification-deliveries", "GetNotificationDeliveries", "deliveries", true);
        AutoRead<NotificationDestinationsV1>(routes, project + "/notification-destinations", "GetNotificationDestinations", "destinations");
        AutoWrite<NotificationDestinationEnableV1>(routes, project + "/notification-destinations/enable", "EnableNotificationDestination", "enableDestination");
        Describe<NotificationOptionsV1>(routes.MapGet(project + "/notification-options", (HttpContext context, string organizationId, string projectId,
            CatalogRequestBoundary boundary, CatalogAutomationOptions options) => AutomationRequest(context, organizationId, projectId, boundary, options,
            async (store, admission, scope) =>
            {
                if (await store.ReadAsync(admission.Actor!, scope, "subscription", null, null, 1, context.RequestAborted) is null) return NotFound();
                var configuration = context.RequestServices.GetRequiredService<NotificationConfiguration>();
                return Results.Ok(new NotificationOptionsV1(NotificationEvents.All, configuration.Email is not null &&
                    configuration.Recipients.Any(r => r.OrganizationId == scope.OrganizationId && r.PrincipalId == admission.Actor!.PrincipalId.Value),
                    configuration.Webhooks.Where(h => h.OrganizationId == scope.OrganizationId && h.ProjectId == scope.ProjectId).Select(h => h.Id).ToArray()));
            })), "GetNotificationOptions", "Read configured channel IDs; never addresses, URLs or secrets.");
        return endpoints;
    }

    private static void AutoRead<T>(IEndpointRouteBuilder routes, string path, string name, string operation, bool page = false)
    {
        var route = routes.MapGet(path, (HttpContext context, string organizationId, string projectId, CatalogRequestBoundary boundary, CatalogAutomationOptions options) =>
            AutomationRequest(context, organizationId, projectId, boundary, options, async (store, admission, scope) =>
            {
                if (page) RequireQuery(context, "limit", "afterId"); else RequireQuery(context);
                var limit = 50;
                if (context.Request.Query.TryGetValue("limit", out var raw))
                {
                    limit = AutomationLimit(raw[0]!);
                }
                var after = context.Request.Query.TryGetValue("afterId", out var cursor) ? Id(cursor[0]!) : (Guid?)null;
                var result = await store.ReadAsync(admission.Actor!, scope, operation, AutoResource(context), after, limit, context.RequestAborted);
                return result is null ? NotFound() : Results.Json(result.Value);
            }));
        Describe<T>(route, name, "Read scoped automation metadata under fresh project authority.");
        AutoResourceParameter(route, path);
        if (page) route.DescribePage("afterId", "Continue after the previous page's ID.");
    }

    private static void AutoWrite<T>(IEndpointRouteBuilder routes, string path, string name, string operation) where T : notnull
    {
        var route = routes.MapPut(path, (HttpContext context, string organizationId, string projectId, CatalogRequestBoundary boundary, CatalogAutomationOptions options) =>
            AutomationRequest(context, organizationId, projectId, boundary, options, async (store, admission, scope) =>
            {
                RequireQuery(context);
                var value = await AutoBody<T>(context);
                ValidateAutoBody(operation, value, context.RequestServices.GetRequiredService<NotificationConfiguration>(), scope, admission);
                var saved = await store.WriteAsync(admission.Actor!, scope, operation, AutoResource(context), value, context.RequestAborted);
                return saved ? Results.Ok(new AutomationSavedV1(true)) : NotFound();
            }, true));
        Describe<AutomationSavedV1>(route, name, "Update scoped automation controls; no payload or secret is accepted.");
        route.Accepts<T>("application/json");
        route.Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge)
            .Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType);
        AutoResourceParameter(route, path);
    }

    private static void AutoResourceParameter(RouteHandlerBuilder route, string path)
    {
        if (!path.Contains("{resourceId}", StringComparison.Ordinal)) return;
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            AddReportParameter(operation, "resourceId", Microsoft.OpenApi.ParameterLocation.Path, true,
                "Canonical mapping, revision or notification identifier for this operation.", uuid: true);
            return Task.CompletedTask;
        });
    }

    private static Task<IResult> AutomationRequest(HttpContext context, string org, string project, CatalogRequestBoundary boundary,
        CatalogAutomationOptions options, Func<IAutomationStore, CatalogRequestAdmission, ProjectScope, Task<IResult>> action, bool write = false) =>
        WithAdmission(context, boundary, async admission =>
        {
            if (!options.Enabled) return NotFound();
            var scope = Project(org, project);
            if (scope.OrganizationId != admission.AllowedOrganizationId) return NotFound();
            try { return await action(context.RequestServices.GetRequiredService<IAutomationStore>(), admission, scope); }
            catch (AutomationBodyException error) { return Error(CatalogErrorCodeV1.InvalidRequest, error.Status); }
        }, write);

    private static Guid? AutoResource(HttpContext context) => context.Request.RouteValues.TryGetValue("resourceId", out var value) ? Id((string)value!) : null;
    private static int AutomationLimit(string raw) =>
        int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 100 &&
            raw == value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ? value : throw new CatalogInvalidRequestException();
    private static async Task<JsonElement> AutoBody<T>(HttpContext context)
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
            !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            media.CharSet is not null && !string.Equals(media.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            throw new AutomationBodyException(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength is > 8192) throw new AutomationBodyException(StatusCodes.Status413PayloadTooLarge);
        var buffer = new byte[8193]; var length = 0;
        while (length < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length > 8192) throw new AutomationBodyException(StatusCodes.Status413PayloadTooLarge);
        if (length == 0) throw new CatalogInvalidRequestException();
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count()) throw new JsonException();
            var expected = typeof(T).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray();
            if (!root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(expected)) throw new JsonException();
            _ = root.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            return root.Clone();
        }
        catch (JsonException) { throw new CatalogInvalidRequestException(); }
    }
    private sealed class AutomationBodyException(int status) : Exception("Automation body refused.")
    {
        internal int Status { get; } = status;
    }
    private static void ValidateAutoBody(string operation, JsonElement body, NotificationConfiguration config, ProjectScope scope, CatalogRequestAdmission admission)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        if (operation == "policy")
        {
            var value = body.Deserialize<AutomationPolicyV1>(json)!;
            if (value.ErrorRatePercent is < 1 or > 100 || value.MinimumRequests is < 1 or > 10000 || value.WindowSeconds is < 60 or > 3600)
                throw new CatalogInvalidRequestException();
        }
        if (operation == "recheck")
        {
            var value = body.Deserialize<RegressionRecheckV1>(json)!;
            _ = Id(value.MappingId); _ = Id(value.RevisionId);
        }
        if (operation == "subscription")
        {
            var value = body.Deserialize<NotificationSubscriptionV1>(json)!;
            if (value.Events is null || value.Webhooks is null || value.Events.Count > 9 || value.Webhooks.Count > 16 ||
                value.Events.Distinct().Count() != value.Events.Count || value.Webhooks.Distinct().Count() != value.Webhooks.Count ||
                value.Events.Any(e => !NotificationEvents.All.Contains(e)) ||
                value.Webhooks.Any(id => !config.Webhooks.Any(h => h.OrganizationId == scope.OrganizationId && h.ProjectId == scope.ProjectId && h.Id == id)) ||
                value.Email && (config.Email is null || !config.Recipients.Any(r => r.OrganizationId == scope.OrganizationId && r.PrincipalId == admission.Actor!.PrincipalId.Value)))
                throw new CatalogInvalidRequestException();
        }
        if (operation == "enableDestination")
        {
            var id = body.Deserialize<NotificationDestinationEnableV1>(json)!.DestinationId;
            if (id is null || id.Length > 80 || !(id.StartsWith("email:", StringComparison.Ordinal) && Guid.TryParseExact(id[6..], "D", out _) ||
                id.StartsWith("webhook:", StringComparison.Ordinal) && NotificationConfiguration.ValidId(id[8..]))) throw new CatalogInvalidRequestException();
        }
    }
}
