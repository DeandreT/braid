using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static readonly JsonSerializerOptions PreviewJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 8
    };

    private static void MapPreviews(IEndpointRouteBuilder endpoints, string organization, string revisionRoute)
    {
        DescribePreview<CatalogPreviewSubmitResultV1>(endpoints.MapPost(revisionRoute + "/previews", SubmitCatalogPreview),
                "SubmitCatalogPreview", "Submit an idempotent bounded sample preview of this exact revision with explicit legacy JSON or document admission.")
            .AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
            {
                // OpenAPI only: runtime Accepts metadata would refuse media before the handler's
                // feature and authority checks, without the catalog's error body.
                var document = context.Document ?? throw new InvalidOperationException("Preview schemas require their OpenAPI document.");
                var schema = await context.GetOrCreateSchemaAsync(typeof(CatalogPreviewSubmissionV1),
                    parameterDescription: null, cancellationToken: cancellationToken);
                document.AddComponent(nameof(CatalogPreviewSubmissionV1), schema);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = new OpenApiSchemaReference(nameof(CatalogPreviewSubmissionV1), document) }
                    }
                };
                AddReportParameter(operation, "Idempotency-Key", ParameterLocation.Header, true,
                    "Exactly one nonzero canonical lowercase D-format UUID with only surrounding SP/HTAB allowed; If-Match and If-None-Match are rejected. " +
                    "The same key with the same submission replays the original run; with a different submission it is a 409 idempotency conflict.", uuid: true);
            })
            .Produces<CatalogPreviewSubmitResultV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge)
            .Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType);
        DescribePreview<CatalogPreviewRunV1>(endpoints.MapGet(organization + "/previews/{runId}", GetCatalogPreview),
            "GetCatalogPreview", "Read one of the caller's own preview runs.");
        endpoints.MapGet(organization + "/previews/{runId}/outputs/{ordinal}", GetCatalogPreviewOutput)
            .WithName("GetCatalogPreviewOutput").WithTags("Catalog")
            .WithSummary("Read the exact bytes of one output of a succeeded preview run.")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/edi-x12")
            .Produces(StatusCodes.Status200OK, contentType: "application/edifact")
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest)
            .Produces<CatalogErrorV1>(StatusCodes.Status401Unauthorized)
            .Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status500InternalServerError);
        DescribePreview<CatalogPreviewCancelResultV1>(endpoints.MapPost(organization + "/previews/{runId}/cancel", CancelCatalogPreview),
            "CancelCatalogPreview", "Cancel a queued preview run or request cancellation of a running one.");
    }

    internal static void TransformPreviewSchemas(OpenApiDocument document)
    {
        if (document.Components?.Schemas?.TryGetValue(nameof(CatalogPreviewSubmissionV1), out var schema) == true
            && schema is OpenApiSchema submission && submission.Properties is { } properties)
            properties["profile"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
                Description = "Omitted or null preserves legacy JSON admission. Document admission requires explicit native boundary formats; filenames never select them.",
                Enum = [JsonValue.Create(PreviewPolicy.Profile)!, JsonValue.Create(DocumentPreviewPolicy.Profile)!, null!]
            };
        foreach (var path in document.Paths.Values)
            foreach (var operation in path.Operations?.Values.AsEnumerable() ?? Enumerable.Empty<OpenApiOperation>())
                if (operation.OperationId == "GetCatalogPreviewOutput" && operation.Responses?.TryGetValue("200", out var response) == true
                    && response is OpenApiResponse output)
                {
                    output.Content = new Dictionary<string, OpenApiMediaType>();
                    foreach (var media in new[] { "application/json", "application/edi-x12", "application/edifact" })
                        output.Content[media] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } };
                    output.Headers = new Dictionary<string, IOpenApiHeader>
                    {
                        ["Content-Digest"] = new OpenApiHeader { Required = true, Description = "SHA-256 of the exact retained output bytes (sha-256=:base64:).", Schema = new OpenApiSchema { Type = JsonSchemaType.String } },
                        ["Content-Disposition"] = PreviewLiteralHeader("attachment"),
                        ["X-Content-Type-Options"] = PreviewLiteralHeader("nosniff"),
                        ["Cache-Control"] = PreviewLiteralHeader("no-store")
                    };
                }
    }

    private static OpenApiHeader PreviewLiteralHeader(string value) => new()
    { Required = true, Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(value)!] } };

    private static RouteHandlerBuilder DescribePreview<T>(RouteHandlerBuilder route, string name, string summary) =>
        route.WithName(name).WithTags("Catalog").WithSummary(summary)
            .Produces<T>(StatusCodes.Status200OK)
            .Produces<CatalogErrorV1>(StatusCodes.Status400BadRequest)
            .Produces<CatalogErrorV1>(StatusCodes.Status401Unauthorized)
            .Produces<CatalogErrorV1>(StatusCodes.Status403Forbidden)
            .Produces<CatalogErrorV1>(StatusCodes.Status404NotFound)
            .Produces<CatalogErrorV1>(StatusCodes.Status500InternalServerError)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable);

    private static Task<IResult> SubmitCatalogPreview(string organizationId, string projectId, string mappingId,
        string revisionId, HttpContext context) =>
        WithPreviewAdmission(context, async (admission, options) =>
        {
            var scope = new RevisionScope(Id(organizationId), Id(projectId), Id(mappingId), Id(revisionId));
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            RequireQuery(context);
            var key = CatalogRevisionRequestParser.Key(context.Request);
            RequireJsonContent(context.Request);
            var revision = await PreviewPersistence(context, () => context.RequestServices.GetRequiredService<ICatalogReader>()
                .GetRevisionAsync(admission.Actor!, scope, context.RequestAborted));
            if (revision is null)
                return NotFound();
            RequireScope(revision.Summary.Scope == scope);
            RequireAccess(revision.Access, admission.Actor!, new ProjectScope(scope.OrganizationId, scope.ProjectId));
            if (!revision.Access.Can(ProjectAction.SubmitPreview))
                return NotFound();
            var body = await ReadPreviewBodyAsync(context.Request, options.BodyByteLimit, context.RequestAborted);
            var submission = PreviewSubmission(scope, key, body);
            var service = context.RequestServices.GetRequiredService<PreviewRunService>();
            PreviewSubmitResult submitted;
            try
            {
                submitted = await PreviewPersistence(context, () => service.SubmitAsync(admission.Actor!, submission, context.RequestAborted));
            }
            catch (PreviewRunException)
            {
                throw new CatalogInvalidRequestException();
            }
            switch (submitted.Outcome)
            {
                case PreviewSubmitOutcome.Conflict:
                    return Error(CatalogErrorCodeV1.IdempotencyConflict, StatusCodes.Status409Conflict);
                case PreviewSubmitOutcome.NotAuthorizedOrNotFound:
                    return NotFound();
            }
            var run = submitted.RunId ?? throw new InvalidOperationException("A stored preview has no run identity.");
            var snapshot = await PreviewPersistence(context, () => service.ReadAsync(admission.Actor!, scope.OrganizationId, run, context.RequestAborted));
            if (snapshot is null)
                return NotFound();
            RequireScope(snapshot.Revision == scope && snapshot.RequestKey == key);
            var created = submitted.Outcome == PreviewSubmitOutcome.Created;
            var result = new CatalogPreviewSubmitResultV1(created ? "created" : "replayed", PreviewRun(snapshot));
            return created
                ? Results.Created($"/api/v1/organizations/{scope.OrganizationId:D}/previews/{run:D}", result)
                : Results.Ok(result);
        }, unsafeRequest: true);

    private static Task<IResult> GetCatalogPreview(string organizationId, string runId, HttpContext context) =>
        WithPreviewAdmission(context, async (admission, _) =>
        {
            var organization = Id(organizationId);
            var run = Id(runId);
            if (organization != admission.AllowedOrganizationId)
                return NotFound();
            RequireQuery(context);
            var snapshot = await PreviewPersistence(context, () => context.RequestServices.GetRequiredService<PreviewRunService>()
                .ReadAsync(admission.Actor!, organization, run, context.RequestAborted));
            if (snapshot is null)
                return NotFound();
            RequireScope(snapshot.RunId == run && snapshot.Revision.OrganizationId == organization);
            return Results.Ok(PreviewRun(snapshot));
        });

    private static Task<IResult> GetCatalogPreviewOutput(string organizationId, string runId, string ordinal, HttpContext context) =>
        WithPreviewAdmission(context, async (admission, _) =>
        {
            var organization = Id(organizationId);
            var run = Id(runId);
            if (ordinal.Length is 0 or > 2 || !int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index > PreviewPolicy.MaximumArtifacts - 1 || ordinal != index.ToString(CultureInfo.InvariantCulture))
                throw new CatalogInvalidRequestException();
            if (organization != admission.AllowedOrganizationId)
                return NotFound();
            RequireQuery(context);
            var output = await PreviewPersistence(context, () => context.RequestServices.GetRequiredService<PreviewRunService>()
                .ReadOutputAsync(admission.Actor!, organization, run, index, context.RequestAborted));
            if (output is null)
                return NotFound();
            context.Response.Headers["Content-Digest"] = "sha-256=:" + Convert.ToBase64String(Convert.FromHexString(output.Digest.Value)) + ":";
            context.Response.Headers.ContentDisposition = "attachment";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!DocumentPreviewPolicy.AdmitsMediaType(output.MediaType))
                throw new InvalidOperationException("Stored preview media type is not admitted.");
            return Results.Stream(new MemoryStream(output.Content, writable: false), output.MediaType);
        });

    private static Task<IResult> CancelCatalogPreview(string organizationId, string runId, HttpContext context) =>
        WithPreviewAdmission(context, async (admission, _) =>
        {
            var organization = Id(organizationId);
            var run = Id(runId);
            if (organization != admission.AllowedOrganizationId)
                return NotFound();
            RequireQuery(context);
            await CatalogRevisionRequestParser.RequireEmptyBodyAsync(context.Request, context.RequestAborted);
            var service = context.RequestServices.GetRequiredService<PreviewRunService>();
            var outcome = await PreviewPersistence(context, () => service.CancelAsync(admission.Actor!, organization, run, context.RequestAborted));
            if (outcome == PreviewCancelOutcome.NotAuthorizedOrNotFound)
                return NotFound();
            var snapshot = await PreviewPersistence(context, () => service.ReadAsync(admission.Actor!, organization, run, context.RequestAborted));
            if (snapshot is null)
                return NotFound();
            return Results.Ok(new CatalogPreviewCancelResultV1(outcome switch
            {
                PreviewCancelOutcome.Canceled => "canceled",
                PreviewCancelOutcome.Requested => "requested",
                _ => "finished"
            }, PreviewRun(snapshot)));
        }, unsafeRequest: true);

    private static Task<IResult> WithPreviewAdmission(HttpContext context,
        Func<CatalogRequestAdmission, CatalogPreviewOptions, Task<IResult>> action, bool unsafeRequest = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.RequestServices.GetService<CatalogPreviewOptions>() is not { Enabled: true } options)
            return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(),
            admission => action(admission, options), unsafeRequest);
    }

    private static bool PreviewEnabled(HttpContext context) =>
        context.RequestServices.GetService<CatalogPreviewOptions>()?.Enabled == true;

    private static async Task<T> PreviewPersistence<T>(HttpContext context, Func<Task<T>> action)
    {
        try
        {
            var value = await action();
            context.RequestAborted.ThrowIfCancellationRequested();
            return value;
        }
        catch (Exception error) when (error is InvalidOperationException or DbException or TimeoutException)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            throw new CatalogRevisionPersistenceException();
        }
    }

    private static void RequireJsonContent(HttpRequest request)
    {
        var types = request.Headers.ContentType;
        if (types.Count != 1 || !MediaTypeHeaderValue.TryParse(types[0], out var type)
            || !string.Equals(type.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase)
            || type.Parameters.Any(parameter => !string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parameter.Value.Value, "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new CatalogRevisionRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        if (request.Headers.ContentEncoding.Count > 0)
            throw new CatalogRevisionRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
    }

    private static async Task<CatalogPreviewSubmissionV1> ReadPreviewBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
            throw CatalogRevisionRequestException.BodyLimit();
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
                throw CatalogRevisionRequestException.BodyLimit();
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<CatalogPreviewSubmissionV1>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), PreviewJson)
                ?? throw new CatalogInvalidRequestException();
        }
        catch (JsonException)
        {
            throw new CatalogInvalidRequestException();
        }
    }

    private static PreviewRunSubmission PreviewSubmission(RevisionScope scope, Guid key, CatalogPreviewSubmissionV1 body)
    {
        if (body.Target is null || body.Parameters is null || body.Inputs is null || body.Inputs.Count == 0
            || body.Inputs.Count > PreviewPolicy.MaximumInputDocuments || body.Parameters.Count > PreviewPolicy.MaximumParameters)
            throw new CatalogInvalidRequestException();
        var target = (body.Target.Kind, body.Target.Name) switch
        {
            ("all", null) => PreviewTargetSelection.All,
            ("primary", null) => PreviewTargetSelection.Primary,
            ("named", { Length: > 0 } name) => PreviewTargetSelection.Named(name),
            _ => throw new CatalogInvalidRequestException()
        };
        var parameters = body.Parameters.Select(parameter => parameter is null ? throw new CatalogInvalidRequestException()
            : new PreviewParameter(parameter.Name, parameter.Kind switch
            {
                "string" => PreviewParameterKind.String,
                "integer" => PreviewParameterKind.Integer,
                "float" => PreviewParameterKind.Float,
                "boolean" => PreviewParameterKind.Boolean,
                _ => throw new CatalogInvalidRequestException()
            }, parameter.Value)).ToArray();
        var inputs = body.Inputs.Select((input, index) =>
        {
            if (input is null || (index == 0) != (input.Name is null) || input.Content is null)
                throw new CatalogInvalidRequestException();
            var bytes = new byte[(input.Content.Length / 4 * 3) + 3];
            if (!Convert.TryFromBase64String(input.Content, bytes, out var length) || length == 0)
                throw new CatalogInvalidRequestException();
            return new PreviewInputDocument(input.Name, bytes.AsSpan(0, length));
        }).ToArray();
        var profile = body.Profile switch
        {
            null or PreviewPolicy.Profile => PreviewExecutionProfile.LegacyJson,
            DocumentPreviewPolicy.Profile => PreviewExecutionProfile.Document,
            _ => throw new CatalogInvalidRequestException()
        };
        return new PreviewRunSubmission(scope, key, target, inputs[0], inputs[1..], parameters) { Profile = profile };
    }

    private static CatalogPreviewRunV1 PreviewRun(PreviewRunSnapshot run) => new(
        run.RunId.ToString("D"),
        new CatalogRevisionScopeV1(run.Revision.OrganizationId.ToString("D"), run.Revision.ProjectId.ToString("D"),
            run.Revision.MappingId.ToString("D"), run.Revision.RevisionId.ToString("D")),
        run.RequestFingerprint,
        run.CurrentDateTime,
        run.State switch
        {
            PreviewRunState.Queued => "queued",
            PreviewRunState.Running => "running",
            PreviewRunState.Completed => "completed",
            _ => "canceled"
        },
        run.Attempt,
        run.MaxAttempts,
        run.CancelRequested,
        run.Outcome is { } outcome ? JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()) : null,
        run.Failure is { } failure
            ? new CatalogPreviewFailureV1(failure.Code, failure.Message, failure.MessageTruncated, failure.Subject, failure.SubjectTruncated)
            : null,
        run.Provenance is { } provenance
            ? new CatalogPreviewProvenanceV1(provenance.Confirmed, provenance.EngineRevision, provenance.BackendBuild,
                provenance.AdapterBuild, provenance.Profile)
            : null,
        run.RecordsWritten?.ToString(CultureInfo.InvariantCulture),
        run.Outputs.Select(output => new CatalogPreviewOutputV1(output.Ordinal, output.BoundaryId, output.TargetName,
            output.TargetNameTruncated, output.MediaType, output.RecordsWritten.ToString(CultureInfo.InvariantCulture),
            output.Digest.Value, output.ByteCount)).ToArray(),
        run.CreatedAtUtc,
        run.StartedAtUtc,
        run.FinishedAtUtc,
        run.ExpiresAtUtc);
}
