using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Specs;
using Weft.Domain;

namespace Weft.Api.Specs;

public sealed record SpecApiOptions(bool Enabled);

public static class SpecEndpoints
{
    public const string UploadHeader = "X-Weft-Spec-Upload";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 4,
        PropertyNameCaseInsensitive = false
    };

    public static IEndpointRouteBuilder MapWeftSpecs(this IEndpointRouteBuilder endpoints) => MapWeftSpecs(endpoints, null);

    // Trusted in-assembly fixture seam; the public production route composition always uses CatalogRequestBoundary.
    internal static IEndpointRouteBuilder MapWeftSpecs(IEndpointRouteBuilder endpoints,
        Func<HttpContext, bool, Task<CatalogRequestAdmission>>? authenticate)
    {
        Task<IResult> Run(HttpContext c, string organization, string project, string bundle, bool write,
            Func<SpecBundleService, SpecBundleScope, Task<IResult>> operation) =>
            RunAdmitted(c, organization, project, bundle, write, operation, authenticate);
        var group = endpoints.BusinessRoutes();
        foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 503 })
            group.WithMetadata(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(SpecErrorV1), status, "application/json"));
        endpoints = group;
        const string root = "/api/v1/organizations/{organizationId}/projects/{projectId}/spec-bundles/{bundleId}";
        endpoints.MapGet(root + "/capabilities", (HttpContext c, string organizationId, string projectId, string bundleId) =>
            Run(c, organizationId, projectId, bundleId, false, async (service, scope) =>
            {
                var formats = await service.IntakeFormatsAsync(scope, c.RequestAborted);
                return Results.Ok(new SpecIntakeCapabilitiesV1(1, 1024 * 1024, formats.Select(f =>
                    new SpecIntakeFormatV1(f.Format, f.MaximumSourceBytes, f.ExtractorVersion, f.EvidenceFormat)).ToArray()));
            })).WithName("GetSpecIntakeCapabilities").WithTags("Specifications").Produces<SpecIntakeCapabilitiesV1>();
        endpoints.MapPut(root, (HttpContext c, string organizationId, string projectId, string bundleId) =>
            Run(c, organizationId, projectId, bundleId, true, async (service, scope) =>
            {
                var request = await Body<SpecCreateV1>(c, 1024);
                RequireId(request.CreationKey);
                return Status(c, await service.CreateAsync(scope, request.CreationKey, c.RequestAborted));
            })).WithName("CreateSpecBundle").WithTags("Specifications").Accepts<SpecCreateV1>("application/json").Produces<SpecStatusV1>();
        endpoints.MapGet(root, (HttpContext c, string organizationId, string projectId, string bundleId) =>
            Run(c, organizationId, projectId, bundleId, false, async (service, scope) =>
            {
                var inventory = await service.InventoryAsync(scope, c.RequestAborted);
                // Bundle version is a mutation CAS token, not a validator for publication-state changes.
                SetVersion(c, inventory.Bundle.Version);
                return Results.Ok(new SpecInventoryV1(Map(inventory.Bundle), inventory.Objects.Select(item =>
                    new SpecObjectV1(item.Reference.Id.ObjectId, item.Reference.Id.Source.SourceId, item.Reference.Id.Source.Version,
                        item.Reference.Id.Kind.ToString(), item.Reference.Digest.Value, item.Reference.ByteCount,
                        item.Format.ToString(), item.State, item.Outcome?.ToString())).ToArray()));
            })).WithName("GetSpecBundleInventory").WithTags("Specifications").Produces<SpecInventoryV1>();
        endpoints.MapPost(root + "/uploads", (HttpContext c, string organizationId, string projectId, string bundleId) =>
            Run(c, organizationId, projectId, bundleId, true, async (service, scope) =>
            {
                Media(c, "application/octet-stream");
                if (c.Request.ContentLength > SpecCapturedSource.MaximumBytes) throw new SpecStorageException(SpecStorageError.Limit);
                var metadata = Header(c, UploadHeader, 2048);
                var request = Deserialize<SpecUploadV1>(Encoding.UTF8.GetBytes(metadata));
                return await Intake(c, service, scope, request, c.Request.Body);
            })).WithName("UploadSpecSource").WithTags("Specifications").Accepts<byte[]>("application/octet-stream").Produces<SpecIntakeV1>();
        endpoints.MapPost(root + "/text", (HttpContext c, string organizationId, string projectId, string bundleId) =>
            Run(c, organizationId, projectId, bundleId, true, async (service, scope) =>
            {
                await service.AuthorizeAsync(scope, true, c.RequestAborted);
                var request = await Body<SpecTextV1>(c, 1024 * 1024);
                if (request.Upload is null || request.Text is null) throw new ArgumentException();
                if (request.Upload.Format != "text") throw new ArgumentException();
                using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(request.Text), false);
                return await Intake(c, service, scope, request.Upload, stream);
            })).WithName("PasteSpecText").WithTags("Specifications").Accepts<SpecTextV1>("application/json").Produces<SpecIntakeV1>();
        endpoints.MapGet(root + "/extractions/{objectId}", (HttpContext c, string organizationId, string projectId, string bundleId, string objectId) =>
            Run(c, organizationId, projectId, bundleId, false, async (service, scope) =>
            {
                var bytes = await service.ReadExtractionAsync(scope, ParseId(objectId), c.RequestAborted);
                return Results.Bytes(bytes, "application/json");
            })).WithName("ReadSpecExtraction").WithTags("Specifications").Produces<JsonElement>(200, "application/json");
        endpoints.MapDelete(root, (HttpContext c, string organizationId, string projectId, string bundleId) =>
            Run(c, organizationId, projectId, bundleId, true, async (service, scope) =>
                Status(c, await service.TombstoneAsync(scope, null, Version(c), ParseId(Header(c, "Idempotency-Key", 36)), c.RequestAborted))))
            .WithName("TombstoneSpecBundle").WithTags("Specifications").Produces<SpecStatusV1>();
        endpoints.MapDelete(root + "/sources/{sourceId}/versions/{sourceVersion}",
            (HttpContext c, string organizationId, string projectId, string bundleId, string sourceId, string sourceVersion) =>
                Run(c, organizationId, projectId, bundleId, true, async (service, scope) =>
                    Status(c, await service.TombstoneAsync(scope, new(scope, ParseId(sourceId), Positive(sourceVersion)),
                        Version(c), ParseId(Header(c, "Idempotency-Key", 36)), c.RequestAborted))))
            .WithName("TombstoneSpecSource").WithTags("Specifications").Produces<SpecStatusV1>();
        return endpoints;
    }

    private static async Task<IResult> Intake(HttpContext c, SpecBundleService service, SpecBundleScope scope, SpecUploadV1 request, Stream input)
    {
        RequireId(request.SourceId); RequireId(request.OriginalObjectId); RequireId(request.UploadKey);
        RequireId(request.ExtractionObjectId); RequireId(request.ExtractionKey);
        var format = request.Format switch
        {
            "text" => SpecSourceFormat.Text,
            "json" => SpecSourceFormat.Json,
            "openapi-json" => SpecSourceFormat.OpenApiJson,
            "docx" => SpecSourceFormat.Docx,
            "xlsx" => SpecSourceFormat.Xlsx,
            "pdf" => SpecSourceFormat.Pdf,
            _ => throw new ArgumentException()
        };
        var result = await service.IntakeAsync(new(scope, request.SourceId, request.SourceVersion), format, request.OriginalObjectId,
            request.UploadKey, request.ExtractionObjectId, request.ExtractionKey, Version(c), request.ExtractionExpectedVersion, input, c.RequestAborted);
        // ReservationVersion is the replay-stable acknowledgement, not a claim about the current inventory version.
        return Results.Ok(new SpecIntakeV1(result.OriginalObjectId, result.ExtractionObjectId, result.ReservationVersion, result.Outcome.ToString()));
    }

    private static async Task<IResult> RunAdmitted(HttpContext c, string organization, string project, string bundle, bool write,
        Func<SpecBundleService, SpecBundleScope, Task<IResult>> operation, Func<HttpContext, bool, Task<CatalogRequestAdmission>>? authenticate)
    {
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            var boundary = c.RequestServices.GetService<CatalogRequestBoundary>();
            if (boundary is null && authenticate is null) return Error(503, "Unavailable");
            var admission = authenticate is not null ? await authenticate(c, write) : await boundary!.AuthenticateAsync(c, write, c.RequestAborted);
            if (!admission.IsAdmitted) return Error(admission.StatusCode, "IdentityRejected");
            if (admission.ContextMode is not ("session" or "bearer")) return Error(403, "IdentityRejected");
            if (c.RequestServices.GetService<SpecApiOptions>()?.Enabled != true) return Error(503, "Unavailable");
            var scope = new SpecBundleScope(new(ParseId(organization), ParseId(project)), ParseId(bundle));
            if (scope.Project.OrganizationId != admission.AllowedOrganizationId) return Error(404, "NotFound");
            if (c.Request.QueryString.HasValue || c.Request.Headers.ContentEncoding.Count != 0) return Error(400, "InvalidRequest");
            var factory = c.RequestServices.GetRequiredService<ISpecBundleServiceFactory>();
            var service = await factory.OpenAsync(admission.Actor!, write, c.RequestAborted);
            return await operation(service, scope);
        }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is SpecDocumentIntakeUnavailableException or SpecOfficeWorkerUnavailableException or SpecPdfWorkerUnavailableException)
        { return Error(503, "DocumentWorkerUnavailable"); }
        catch (SpecStorageException e)
        {
            return e.Error switch
            {
                SpecStorageError.NotAdmitted or SpecStorageError.Missing => Error(404, "NotFound"),
                SpecStorageError.Conflict => Error(409, "Conflict"),
                SpecStorageError.Limit => Error(413, "LimitExceeded"),
                _ => Error(503, "Unavailable")
            };
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or OverflowException)
        { return Error(400, "InvalidRequest"); }
        catch (BadHttpRequestException e) { return Error(e.StatusCode == 413 ? 413 : 400, "InvalidRequest"); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return Error(503, "Unavailable"); }
    }

    private static IResult Error(int status, string code) => Results.Json(new SpecErrorV1(code), statusCode: status);
    private static SpecStatusV1 Map(SpecBundleStatus status) => new(status.Scope.BundleId, status.Version, status.Tombstoned);
    private static IResult Status(HttpContext c, SpecBundleStatus status) { SetVersion(c, status.Version); return Results.Ok(Map(status)); }
    private static void SetVersion(HttpContext c, long version) => c.Response.Headers["X-Weft-Spec-Version"] = version.ToString(CultureInfo.InvariantCulture);
    private static long Version(HttpContext c)
    {
        var value = Header(c, "If-Match", 22);
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"') throw new ArgumentException();
        return Positive(value[1..^1]);
    }
    private static long Positive(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
        ? number : throw new ArgumentException();
    private static Guid ParseId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException();
    private static void RequireId(Guid value) { if (value == Guid.Empty) throw new ArgumentException(); }
    private static string Header(HttpContext c, string name, int maximum)
    {
        var values = c.Request.Headers[name];
        return values.Count == 1 && values[0] is { Length: > 0 } value && value.Length <= maximum ? value : throw new ArgumentException();
    }
    private static void Media(HttpContext c, string type)
    {
        var contentType = c.Request.ContentType;
        if (!string.Equals(contentType, type, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(contentType, type + "; charset=utf-8", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
    }
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException();
    private static async Task<T> Body<T>(HttpContext c, int maximum)
    {
        Media(c, "application/json");
        if (c.Request.ContentLength > maximum) throw new SpecStorageException(SpecStorageError.Limit);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await c.Request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum + 1 - (int)buffer.Length)), c.RequestAborted);
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
            if (buffer.Length > maximum) throw new SpecStorageException(SpecStorageError.Limit);
        }
        return Deserialize<T>(buffer.ToArray());
    }
}
