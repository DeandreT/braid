using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Weft.Api.Auth;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Operations;
using Weft.Deployment.Sandbox;
using Weft.Domain;

namespace Weft.Api.Catalog;

/// <summary>Enabled only by a local Development host; hosted profiles refuse the setting at startup.</summary>
public sealed record LocalSandboxOptions(bool Enabled = false);

public static partial class CatalogEndpoints
{
    private const int MaximumSandboxBodyBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions SandboxJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static IEndpointRouteBuilder MapWeftLocalSandbox(this IEndpointRouteBuilder endpoints)
    {
        const string root = "/api/v1/organizations/{organizationId}/projects/{projectId}/mappings/{mappingId}/sandbox";
        var routes = endpoints.BusinessRoutes();
        DescribeSandbox<LocalSandboxStateV1>(routes.MapGet(root, GetLocalSandbox), "GetLocalSandbox",
            "Read the local sandbox targets, deployable builds and sample sources (local simulation only).");
        DescribeSandbox<LocalSandboxDeploymentV1>(routes.MapPost(root + "/deployments/{deploymentId}", DeployLocalSandbox), "DeployLocalSandbox",
            "Qualify a completed build, register or reuse the sandbox target and activate it through release operations.")
            .Accepts<LocalSandboxDeployV1>("application/json").Produces<CatalogErrorV1>(408).Produces<CatalogErrorV1>(413).Produces<CatalogErrorV1>(415);
        DescribeSandbox<LocalSandboxMessagePageV1>(routes.MapGet(root + "/{targetId}/messages", GetLocalSandboxMessages), "GetLocalSandboxMessages",
            "Read the newest retained sandbox messages of a target, without document contents.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                AddReportParameter(operation, "limit", Microsoft.OpenApi.ParameterLocation.Query, false, "Canonical decimal page limit, default 20 and maximum 50.", maximumLength: 2);
                return Task.CompletedTask;
            });
        DescribeSandbox<LocalSandboxMessageV1>(routes.MapGet(root + "/{targetId}/messages/{messageId}", GetLocalSandboxMessage), "GetLocalSandboxMessage",
            "Read one retained sandbox message with its inputs and outputs.");
        DescribeSandbox<LocalSandboxMessageV1>(routes.MapPost(root + "/{targetId}/messages/{messageId}", SendLocalSandboxMessage), "SendLocalSandboxMessage",
            "Send a sample to the target's active release through the machine invocation path. Repeating a message ID returns its recorded result.")
            .Accepts<LocalSandboxSendV1>("application/json").Produces<CatalogErrorV1>(408).Produces<CatalogErrorV1>(413).Produces<CatalogErrorV1>(415);
        return endpoints;
    }

    private static RouteHandlerBuilder DescribeSandbox<T>(RouteHandlerBuilder route, string name, string summary) =>
        DescribeBuild<T>(route, name, summary).WithTags("Sandbox")
            .Produces<CatalogErrorV1>(401).Produces<CatalogErrorV1>(403).Produces<CatalogErrorV1>(500);

    private static Task<IResult> WithSandbox(HttpContext context, string organizationId, string projectId, string mappingId,
        Func<CatalogRequestAdmission, MappingScope, LocalSandboxService, Task<IResult>> action, bool write = false, bool administrator = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.RequestServices.GetRequiredService<LocalSandboxOptions>().Enabled) return Task.FromResult(NotFound());
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            try
            {
                var mapping = BuildMapping(organizationId, projectId, mappingId, admission);
                // Deploying registers a target and grants the sandbox caller, which needs an Administrator.
                if (mapping is null || !await BuildProjectAccess(context, admission, new(mapping.OrganizationId, mapping.ProjectId), administrator)) return NotFound();
                var result = await action(admission, mapping, context.RequestServices.GetRequiredService<LocalSandboxService>());
                // File-backed history has no database policy of its own. Recheck current authority before
                // releasing private sample bytes, including after a long compiled invocation.
                return await BuildProjectAccess(context, admission, new(mapping.OrganizationId, mapping.ProjectId), administrator) ? result : NotFound();
            }
            catch (CatalogBuildRequestException error) { return Error(error.Code, error.Status); }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or JsonException)
            { return Error(CatalogErrorCodeV1.InvalidRequest, 400); }
        }, write);
    }

    private static Task<IResult> GetLocalSandbox(string organizationId, string projectId, string mappingId, HttpContext context) =>
        WithSandbox(context, organizationId, projectId, mappingId, async (admission, mapping, sandbox) =>
        {
            RequireQuery(context);
            var state = await sandbox.ReadStateAsync(admission.Actor!, mapping, context.RequestAborted);
            return CaseResponse(PrepareBuild(new LocalSandboxStateV1("localSimulation", state.Targets.Select(SandboxTarget).ToArray(),
                state.Builds.Select(value => new LocalSandboxBuildV1(value.BuildId.ToString("D"), value.RevisionId.ToString("D"), value.EnvironmentId.ToString("D"),
                    value.EnvironmentVersion.ToString(CultureInfo.InvariantCulture), value.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))).ToArray(),
                state.Samples.Select(value => new LocalSandboxSampleV1(value.Kind, value.Reference, value.Name)).ToArray()), context.RequestAborted));
        });

    private static Task<IResult> DeployLocalSandbox(string organizationId, string projectId, string mappingId, string deploymentId, HttpContext context) =>
        WithSandbox(context, organizationId, projectId, mappingId, async (admission, mapping, sandbox) =>
        {
            RequireQuery(context);
            var id = CatalogBuildRequest.Id(deploymentId);
            var body = await SandboxBodyAsync<LocalSandboxDeployV1>(context.Request, 1024, context.RequestAborted);
            var result = await sandbox.DeployAsync(admission.Actor!, mapping, id, CatalogBuildRequest.Id(body.BuildId), context.RequestAborted);
            var response = new LocalSandboxDeploymentV1(deploymentId, result.Outcome, result.Target is null ? null : SandboxTarget(result.Target),
                result.ReleaseId?.ToString("D"), result.QualificationId?.ToString("D"));
            // Every admitted attempt reports its outcome; only a refusal hides whether the build or target exists.
            return result.Outcome == "refused" ? NotFound() : CaseResponse(PrepareBuild(response, context.RequestAborted));
        }, write: true, administrator: true);

    private static Task<IResult> GetLocalSandboxMessages(string organizationId, string projectId, string mappingId, string targetId, HttpContext context) =>
        WithSandbox(context, organizationId, projectId, mappingId, async (admission, mapping, sandbox) =>
        {
            RequireQuery(context, "limit");
            var limit = context.Request.Query.TryGetValue("limit", out var raw) ? checked((int)CatalogBuildRequest.Version(raw[0]!)) : 20;
            var items = await sandbox.ListAsync(admission.Actor!, mapping, CatalogBuildRequest.Id(targetId), limit, context.RequestAborted);
            return items is null ? NotFound() : CaseResponse(PrepareBuild(new LocalSandboxMessagePageV1(items.Select(value => SandboxMessage(value, false)).ToArray()),
                context.RequestAborted));
        });

    private static Task<IResult> GetLocalSandboxMessage(string organizationId, string projectId, string mappingId, string targetId, string messageId, HttpContext context) =>
        WithSandbox(context, organizationId, projectId, mappingId, async (admission, mapping, sandbox) =>
        {
            RequireQuery(context);
            var value = await sandbox.ReadAsync(admission.Actor!, mapping, CatalogBuildRequest.Id(targetId), CatalogBuildRequest.Id(messageId), context.RequestAborted);
            return value is null ? NotFound() : CaseResponse(PrepareBuild(SandboxMessage(value, true), context.RequestAborted));
        });

    private static Task<IResult> SendLocalSandboxMessage(string organizationId, string projectId, string mappingId, string targetId, string messageId, HttpContext context) =>
        WithSandbox(context, organizationId, projectId, mappingId, async (admission, mapping, sandbox) =>
        {
            RequireQuery(context);
            var target = CatalogBuildRequest.Id(targetId);
            var id = CatalogBuildRequest.Id(messageId);
            var body = await SandboxBodyAsync<LocalSandboxSendV1>(context.Request, MaximumSandboxBodyBytes, context.RequestAborted);
            var token = context.RequestAborted;
            Guid? replayOf = null;
            SandboxSample? sample;
            switch (body.Source)
            {
                case "inline" when body.Inputs is { Count: > 0 } inputs && body.Reference is null && body.ReplayOf is null:
                    if (inputs.Any(input => input is null) || inputs.Count(input => input.Name is null) != 1 || inputs.Any(input => input.Content is null || input.MediaType is not null || input.Sha256 is not null) ||
                        body.Parameters?.Any(parameter => parameter is null) == true)
                        throw new ArgumentException("Invalid sandbox inputs.");
                    var documents = inputs.Select(input => new PreviewInputDocument(input.Name, Convert.FromBase64String(input.Content!))).ToArray();
                    var parameters = (body.Parameters ?? []).Select(value => new PreviewParameter(value.Name, value.Kind switch
                    {
                        "string" => PreviewParameterKind.String,
                        "integer" => PreviewParameterKind.Integer,
                        "float" => PreviewParameterKind.Float,
                        "boolean" => PreviewParameterKind.Boolean,
                        _ => throw new ArgumentException("Invalid parameter kind.")
                    }, value.Value)).ToArray();
                    sample = new(documents.Single(input => input.Name is null), documents.Where(input => input.Name is not null).ToArray(), parameters, "inline");
                    break;
                case "replay" when body.ReplayOf is { } original && body.Inputs is null && body.Parameters is null && body.Reference is null:
                    replayOf = CatalogBuildRequest.Id(original);
                    sample = await sandbox.ReplaySampleAsync(admission.Actor!, mapping, target, replayOf.Value, token);
                    break;
                case not ("inline" or "replay") when body.Reference is { } reference && body.Inputs is null && body.Parameters is null && body.ReplayOf is null:
                    sample = await sandbox.ResolveSampleAsync(admission.Actor!, mapping, body.Source, reference, token);
                    break;
                default: throw new ArgumentException("Invalid sandbox source.");
            }
            if (sample is null) return NotFound();
            var message = await sandbox.SendAsync(admission.Actor!, mapping, target, id, sample, replayOf, token);
            return message is null ? NotFound() : CaseResponse(PrepareBuild(SandboxMessage(message, true), token));
        }, write: true);

    private static LocalSandboxTargetV1 SandboxTarget(SandboxTarget value) => new(value.TargetId.ToString("D"), value.EnvironmentId.ToString("D"),
        value.EnvironmentVersion.ToString(CultureInfo.InvariantCulture), value.Enabled, value.ActiveReleaseId?.ToString("D"),
        value.Version.ToString(CultureInfo.InvariantCulture), value.RuntimeImage);

    private static LocalSandboxMessageV1 SandboxMessage(SandboxMessage value, bool contents)
    {
        LocalSandboxDocumentV1 Document(SandboxDocument document) =>
            new(document.Name, document.MediaType, document.Sha256, contents ? Convert.ToBase64String(document.Content) : null);
        return new(value.MessageId.ToString("D"), value.TargetId.ToString("D"), value.SentAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            value.DurationMs, value.Status, value.Outcome, value.Replayed, value.ReleaseId?.ToString("D"), value.BuildId?.ToString("D"), value.Source,
            value.ReplayOf?.ToString("D"), value.Inputs.Select(Document).ToArray(),
            contents ? value.Parameters.Select(parameter => new LocalSandboxParameterV1(parameter.Name, parameter.Kind.ToString().ToLowerInvariant(), parameter.Value)).ToArray() : null,
            value.Outputs.Select(Document).ToArray());
    }

    private static async Task<T> SandboxBodyAsync<T>(HttpRequest request, int maximum, CancellationToken token)
    {
        var types = request.Headers.ContentType;
        if (types.Count != 1 || !MediaTypeHeaderValue.TryParse(types[0], out var media) ||
            !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase) ||
            media.Charset.HasValue && !string.Equals(media.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContainsKey("Content-Encoding"))
            throw new CatalogBuildRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength > maximum) throw new CatalogBuildRequestException(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var buffer = new MemoryStream();
        var block = new byte[81920];
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(block, timeout.Token)) != 0)
            {
                if (count > maximum - buffer.Length) throw new CatalogBuildRequestException(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
                buffer.Write(block, 0, count);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CatalogBuildRequestException(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status408RequestTimeout); }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), SandboxJson) ?? throw new ArgumentException("A request body is required.");
    }
}
