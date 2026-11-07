using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Administration;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Api.Administration;

public static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapWeftAdministration(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.BusinessRoutes();
        const string org = "/api/v1/organizations/{organizationId}/administration";
        const string project = "/api/v1/organizations/{organizationId}/projects/{projectId}/administration";
        Describe<OrganizationAdministrationV1>(routes.MapGet(org, (string organizationId, HttpContext context) =>
            Handle(context, organizationId, null, false, async (store, actor, id, _) =>
            {
                var snapshot = await store.ReadOrganizationAsync(actor, id, context.RequestAborted);
                if (snapshot is null) return Error("notFound", 404);
                context.Response.Headers.ETag = ETag(id, snapshot.Version);
                return Results.Ok(snapshot);
            })), "GetOrganizationAdministration");
        Describe<ProjectAdministrationV1>(routes.MapGet(project, (string organizationId, string projectId, HttpContext context) =>
            Handle(context, organizationId, projectId, false, async (store, actor, id, pid) =>
            {
                var snapshot = await store.ReadProjectAsync(actor, new(id, pid!.Value), context.RequestAborted);
                if (snapshot is null) return Error("notFound", 404);
                context.Response.Headers.ETag = ETag(id, snapshot.Version);
                return Results.Ok(snapshot);
            })), "GetProjectAdministration");

        Member(org + "/members/{principalId}", "PutOrganizationMember", AdministrationOperation.OrganizationMember);
        Member(project + "/members/{principalId}", "PutProjectMember", AdministrationOperation.ProjectMember);
        Describe<AdministrationReceiptV1>(routes.MapPut(project + "/members/{principalId}/payload-read",
            (string organizationId, string projectId, string principalId, HttpContext context) =>
                Write(context, organizationId, projectId, principalId, AdministrationOperation.PayloadRead, async () =>
                {
                    var value = await Body<AdministrationPayloadPutV1>(context);
                    return JsonSerializer.SerializeToElement(value, ProjectionJson.CreateOptions());
                })), "PutProjectPayloadRead").Accepts<AdministrationPayloadPutV1>("application/json");
        Empty(org + "/principals/{principalId}/disable", "DisableAdministrationPrincipal", AdministrationOperation.PrincipalDisable, "principalId");
        Empty(org + "/bindings/{bindingId}/disable", "DisableAdministrationBinding", AdministrationOperation.BindingDisable, "bindingId");
        Empty(org + "/invitations/{invitationId}/revoke", "RevokeOrganizationInvitation", AdministrationOperation.InviteRevoke, "invitationId");
        Empty(project + "/invocation-grants/{grantId}/revoke", "RevokeAdministrationInvocationGrant", AdministrationOperation.InvocationRevoke, "grantId");
        Describe<AdministrationReceiptV1>(routes.MapPut(project + "/invocation-grants/{grantId}",
            (string organizationId, string projectId, string grantId, HttpContext context) =>
                Write(context, organizationId, projectId, grantId, AdministrationOperation.InvocationGrant, async () =>
                {
                    var value = await Body<AdministrationInvocationGrantPutV1>(context);
                    if (value.TargetId == Guid.Empty || value.TenantId == Guid.Empty || value.ObjectId == Guid.Empty) throw new InvalidInput();
                    return JsonSerializer.SerializeToElement(value, ProjectionJson.CreateOptions());
                })), "PutAdministrationInvocationGrant").Accepts<AdministrationInvocationGrantPutV1>("application/json");
        Describe<AdministrationInvitationCreatedV1>(routes.MapPost(org + "/invitations", (string organizationId, HttpContext context) =>
            Handle(context, organizationId, null, true, async (store, actor, id, _) =>
            {
                var version = Expected(context, id);
                var value = await Body<AdministrationInvitationPostV1>(context);
                if (value.InvitationId == Guid.Empty || value.TenantId == Guid.Empty || value.ExpectedObjectId == Guid.Empty ||
                    value.ProjectRoles is null || value.ProjectRoles.Count > 32 ||
                    value.ProjectRoles.Any(r => r.ProjectId == Guid.Empty || !ProjectRole(r.Role)) ||
                    value.ProjectRoles.Select(r => r.ProjectId).Distinct().Count() != value.ProjectRoles.Count ||
                    value.ExpiresAt <= DateTimeOffset.UtcNow || value.ExpiresAt > DateTimeOffset.UtcNow.AddDays(7)) throw new InvalidInput();
                var secret = OrganizationInvitationSecret.Create();
                var data = JsonSerializer.SerializeToElement(new
                {
                    value.TenantId,
                    value.ExpectedObjectId,
                    value.ProjectRoles,
                    value.ExpiresAt,
                    secretHash = OrganizationInvitationSecret.Hash(secret)
                }, ProjectionJson.CreateOptions());
                var outcome = await store.WriteAsync(actor, id, null, version, AdministrationOperation.Invite, value.InvitationId, data, context.RequestAborted);
                return outcome == "updated" ? Results.Ok(new AdministrationInvitationCreatedV1(value.InvitationId, "/invitations#" + secret)) : Outcome(outcome);
            })), "CreateOrganizationInvitation").Accepts<AdministrationInvitationPostV1>("application/json");
        return endpoints;

        void Member(string path, string name, AdministrationOperation operation) =>
            Describe<AdministrationReceiptV1>(routes.MapPut(path, (string organizationId, string principalId, HttpContext context) =>
                Write(context, organizationId, context.Request.RouteValues["projectId"] as string, principalId, operation, async () =>
                {
                    var value = await Body<AdministrationMemberPutV1>(context);
                    if (operation == AdministrationOperation.OrganizationMember ? value.Role is not ("member" or "administrator") : !ProjectRole(value.Role)) throw new InvalidInput();
                    return JsonSerializer.SerializeToElement(value, ProjectionJson.CreateOptions());
                })), name).Accepts<AdministrationMemberPutV1>("application/json");

        void Empty(string path, string name, AdministrationOperation operation, string key) =>
            Describe<AdministrationReceiptV1>(routes.MapPost(path, (string organizationId, HttpContext context) =>
                Write(context, organizationId, context.Request.RouteValues["projectId"] as string,
                    context.Request.RouteValues[key] as string ?? "", operation, () =>
                    {
                        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                            context.Request.Headers.ContainsKey("Content-Type") ||
                            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false) throw new InvalidInput();
                        return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
                    })), name);
    }

    private static Task<IResult> Write(HttpContext context, string organizationId, string? projectId, string target,
        AdministrationOperation operation, Func<Task<JsonElement>> read) =>
        Handle(context, organizationId, projectId, true, async (store, actor, org, project) =>
            Outcome(await store.WriteAsync(actor, org, project, Expected(context, org), operation, Id(target), await read(), context.RequestAborted)));

    private static async Task<IResult> Handle(HttpContext context, string organizationId, string? projectId, bool write,
        Func<IAdministrationStore, ActorContext, Guid, Guid?, Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var admission = await context.RequestServices.GetRequiredService<CatalogRequestBoundary>().AuthenticateAsync(context, write, context.RequestAborted);
            if (!admission.IsAdmitted) return Error(admission.Error?.ToString() is { } e ? char.ToLowerInvariant(e[0]) + e[1..] : "identityRejected", admission.StatusCode);
            if (context.Request.QueryString.HasValue || context.Request.Headers.ContentEncoding.Count != 0) throw new InvalidInput();
            var org = Id(organizationId);
            if (org != admission.AllowedOrganizationId) return Error("notFound", 404);
            var project = projectId is null ? (Guid?)null : Id(projectId);
            var store = context.RequestServices.GetService<IAdministrationStore>();
            return store is null ? Error("unavailable", 404) : await action(store, admission.Actor!, org, project);
        }
        catch (MissingCondition) { return Error("preconditionRequired", 428); }
        catch (InvalidInput) { return Error("invalidRequest", 400); }
        catch (JsonException) { return Error("invalidRequest", 400); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            return Error("unavailable", 503);
        }
    }

    private static async Task<T> Body<T>(HttpContext context)
    {
        if (context.Request.ContentType != "application/json" || context.Request.ContentLength is > 8192) throw new InvalidInput();
        using var bytes = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (count == 0) break;
            if (bytes.Length + count > 8192) throw new InvalidInput();
            bytes.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        Unique(json.RootElement);
        var options = ProjectionJson.CreateOptions();
        options.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
        options.RespectRequiredConstructorParameters = true;
        return json.RootElement.Deserialize<T>(options) ?? throw new InvalidInput();
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidInput(); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    private static bool ProjectRole(string? value) => value is "viewer" or "runner" or "publisher" or "administrator";
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D") ? id : throw new InvalidInput();
    internal static string ETag(Guid org, long version) => $"\"admin-{org:D}-{version.ToString(CultureInfo.InvariantCulture)}\"";
    private static long Expected(HttpContext context, Guid org)
    {
        var values = context.Request.Headers.IfMatch;
        if (values.Count == 0) throw new MissingCondition();
        var prefix = $"\"admin-{org:D}-";
        if (values.Count != 1 || values[0] is not { } text || !text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('"') ||
            !long.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 1 || ETag(org, version) != text) throw new InvalidInput();
        return version;
    }
    private static IResult Outcome(string outcome) => outcome switch
    {
        "updated" or "unchanged" => Results.Ok(new AdministrationReceiptV1(outcome)),
        "refused" => Error("notFound", 404),
        "preconditionFailed" => Error(outcome, 412),
        "lastAdministrator" or "conflict" => Error(outcome, 409),
        _ => Error("invalidRequest", 400)
    };
    private static IResult Error(string code, int status) => Results.Json(new AdministrationErrorV1(code), statusCode: status);
    private static RouteHandlerBuilder Describe<T>(RouteHandlerBuilder route, string name) =>
        route.WithName(name).WithTags("Administration").WithSummary(name)
            .Produces<T>().Produces<AdministrationErrorV1>(400).Produces<AdministrationErrorV1>(404)
            .Produces<AdministrationErrorV1>(409).Produces<AdministrationErrorV1>(412).Produces<AdministrationErrorV1>(428)
            .Produces<AdministrationErrorV1>(503).AddOpenApiOperationTransformer((operation, description, _) =>
            {
                // Shared bodyless handlers read some route values from HttpContext.
                // Declare those values too, not only parameters inferred from a delegate signature.
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    description.Description.RelativePath ?? "", @"\{([A-Za-z]+)\}"))
                {
                    var parameter = match.Groups[1].Value;
                    operation.Parameters ??= [];
                    if (!operation.Parameters.Any(p => p.In == ParameterLocation.Path && p.Name == parameter))
                        operation.Parameters.Add(new OpenApiParameter
                        {
                            Name = parameter,
                            In = ParameterLocation.Path,
                            Required = true,
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                        });
                }
                if (name.StartsWith("Get", StringComparison.Ordinal))
                {
                    if (operation.Responses?["200"] is OpenApiResponse response)
                    { response.Headers ??= new Dictionary<string, IOpenApiHeader>(); response.Headers["ETag"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String } }; }
                }
                else
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "If-Match",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Strong administration ETag from the latest authorized organization or project administration read.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                }
                return Task.CompletedTask;
            });
    private sealed class InvalidInput : Exception;
    private sealed class MissingCondition : Exception;
}
