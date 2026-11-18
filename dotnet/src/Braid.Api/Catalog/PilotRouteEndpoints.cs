using System.Globalization;
using System.Text;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Contracts.Operations;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static Task<IResult> ApplyPilotRoute(string organizationId, string projectId, string mappingId, string targetId, string triggerId,
        string action, string operationId, HttpContext context) => WithOperations(context, async admission =>
    {
        RequireQuery(context);
        var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
        if (mapping is null || !await BuildProjectAccess(context, admission, new(mapping.OrganizationId, mapping.ProjectId), administrator: true)) return NotFound();
        if (!context.Request.Headers.TryGetValue("If-Match", out var match)) return Error(CatalogErrorCodeV1.PreconditionFailed, 428);
        if (match.Count != 1 || match[0] is not { Length: > 2 } etag || etag[0] != '"' || etag[^1] != '"') throw new CatalogInvalidRequestException();
        var expected = etag == "\"0\"" ? 0 : CatalogBuildRequest.Version(etag[1..^1]);
        string[] settings = ["sourceBinding", "source", "sourceVersion", "destinationBinding", "destination", "destinationVersion", "tenant", "principal", "application", "cron", "effectiveUtc", "tzVersion", "referenceName", "referenceJson"];
        string[] generalFields = ["destinationKind", "inputMediaType", "envelope"];
        var values = await CatalogBuildRequest.ReadAsync(context.Request, "application/json", ["environmentId", "environmentVersion", .. settings, .. generalFields], context.RequestAborted, [.. settings, .. generalFields], 12288, generalFields);
        string Get(string key) => values[key] ?? throw new CatalogInvalidRequestException();
        Guid Id(string key) => CatalogBuildRequest.Id(Get(key));
        long Version(string key) => CatalogBuildRequest.Version(Get(key));
        var target = new ReleaseTargetReference(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), Id("environmentId"), Version("environmentVersion")), CatalogBuildRequest.Id(targetId));
        var trigger = CatalogBuildRequest.Id(triggerId);
        var command = action switch { "configure" or "configureQueue" or "configureBlob" => TriggerAction.Configure, "pause" => TriggerAction.Disable, "enable" => TriggerAction.Enable, _ => throw new CatalogInvalidRequestException() };
        TriggerConfiguration? config = null;
        if (command == TriggerAction.Configure)
        {
            var queue = action == "configureQueue";
            var general = action != "configure";
            if (!general && generalFields.Any(key => values[key] is not null)) throw new CatalogInvalidRequestException();
            var destination = !general ? TriggerDestinationKind.As2 : Get("destinationKind") switch {
                "blob" => TriggerDestinationKind.Blob, "sftp" => TriggerDestinationKind.Sftp, "https" => TriggerDestinationKind.Https,
                "serviceBusTopic" => TriggerDestinationKind.ServiceBusTopic, _ => throw new CatalogInvalidRequestException() };
            var envelope = !general ? TriggerEnvelope.SingleOrder : Get("envelope") switch { "singleOrder" => TriggerEnvelope.SingleOrder, "batch" => TriggerEnvelope.Batch, _ => throw new CatalogInvalidRequestException() };
            var media = general ? Get("inputMediaType") : "application/edi-x12";
            TriggerSchedule? schedule = null;
            if (queue) {
                if (new[]{"effectiveUtc","tzVersion","cron","referenceName","referenceJson"}.Any(k => values[k] is not null)) throw new CatalogInvalidRequestException();
            } else {
            if (!DateTimeOffset.TryParseExact(Get("effectiveUtc"), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var epoch)) throw new CatalogInvalidRequestException();
            schedule = new TriggerSchedule(new(Get("cron")), "UTC", Get("tzVersion"), epoch, ScheduleGap.Skip, ScheduleFold.First, ScheduleOverlap.SkipIfRunning, ScheduleCatchUp.Bounded, TimeSpan.FromHours(1), 2);
            }
            PreviewParameter[] refs = values["referenceName"] is null && values["referenceJson"] is null ? [] : [PilotRouteInputs.Attach(Get("referenceName"), new UTF8Encoding(false, true).GetBytes(Get("referenceJson")))];
            config = new(trigger, target, queue ? TriggerKind.ServiceBusQueue : TriggerKind.Schedule, Id("sourceBinding"), new(Get("source"), Version("sourceVersion")), new(MachinePrincipal.Entra, Id("tenant"), Id("principal")), Id("application"), media, refs,
                [new(null, Id("destinationBinding"), destination, new(Get("destination"), Version("destinationVersion")))],
                new(new(2, [TimeSpan.FromMinutes(1)], TimeSpan.FromSeconds(30), TimeSpan.FromHours(4)), TimeSpan.FromMinutes(1), 1024 * 1024, TimeSpan.FromDays(1), 100, 32 * 1024 * 1024), envelope, schedule);
        }
        else if (settings.Concat(generalFields).Any(key => values[key] is not null)) throw new CatalogInvalidRequestException();
        var operation = new TriggerOperation(CatalogBuildRequest.Id(operationId), trigger, command, expected, config);
        var store = context.RequestServices.GetRequiredService<ITriggerAdministrationStore>();
        var result = await store.OperateAsync(admission.Actor!, target, operation, context.RequestAborted);
        return result switch
        {
            TriggerOperationOutcome.Applied or TriggerOperationOutcome.Unchanged => CaseResponse(PrepareBuild(new ReleaseOperationReceiptV1(operationId, "applied"), context.RequestAborted)),
            TriggerOperationOutcome.IdempotencyConflict => Error(CatalogErrorCodeV1.IdempotencyConflict, 409),
            TriggerOperationOutcome.Stale => Error(CatalogErrorCodeV1.PreconditionFailed, 412),
            TriggerOperationOutcome.Incomplete => Error(CatalogErrorCodeV1.InvalidRequest, 400),
            _ => NotFound()
        };
    }, true);
}
