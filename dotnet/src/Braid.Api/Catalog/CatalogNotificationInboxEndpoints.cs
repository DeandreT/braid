using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application.Automation;
using Braid.Contracts.Automation;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void MapOrganizationNotifications(IEndpointRouteBuilder routes)
    {
        const string root = "/api/v1/organizations/{organizationId}/notifications";
        var read = routes.MapGet(root, (string organizationId, HttpContext context, CatalogRequestBoundary boundary, CatalogAutomationOptions options) =>
            WithAdmission(context, boundary, async admission =>
            {
                RequireQuery(context, "limit", "afterId");
                var org = Id(organizationId);
                if (!options.Enabled || org != admission.AllowedOrganizationId) return NotFound();
                var limit = 50;
                if (context.Request.Query.TryGetValue("limit", out var maximum))
                {
                    limit = AutomationLimit(maximum[0]!);
                }
                var after = context.Request.Query.TryGetValue("afterId", out var cursor) ? Id(cursor[0]!) : (Guid?)null;
                var valuePage = await context.RequestServices.GetRequiredService<IAutomationStore>()
                    .ReadInboxAsync(admission.Actor!, org, after, limit, context.RequestAborted);
                return valuePage is null ? NotFound() : Results.Json(valuePage.Value);
            }));
        Describe<NotificationsV1>(read, "GetNotificationInbox", "Read visible project activity and recipient-only organization invitations.");
        read.DescribePage("afterId", "Continue after the previous page's notification ID.");

        var write = routes.MapPut(root + "/{notificationId}/read", (string organizationId, string notificationId, HttpContext context, CatalogRequestBoundary boundary, CatalogAutomationOptions options) =>
            WithAdmission(context, boundary, async admission =>
            {
                RequireQuery(context);
                var org = Id(organizationId); var id = Id(notificationId);
                if (!options.Enabled || org != admission.AllowedOrganizationId) return NotFound();
                try { _ = await AutoBody<NotificationReadV1>(context); }
                catch (AutomationBodyException error) { return Error(CatalogErrorCodeV1.InvalidRequest, error.Status); }
                var updated = await context.RequestServices.GetRequiredService<IAutomationStore>()
                    .MarkInboxReadAsync(admission.Actor!, org, id, context.RequestAborted);
                return updated ? Results.Ok(new AutomationSavedV1(true)) : NotFound();
            }, unsafeRequest: true));
        Describe<AutomationSavedV1>(write, "ReadInboxNotification", "Mark one currently visible own notification read without changing its provenance.");
        write.Accepts<NotificationReadV1>("application/json");
    }
}
