using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Braid.Application;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void DescribeMetadataRead(RouteHandlerBuilder route)
    {
        Describe<CatalogMappingMetadataV1>(route, "GetCatalogMappingMetadata", "Read the exact mapping metadata representation.");
        route.Produces(StatusCodes.Status304NotModified)
            .Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                AddConditionalHeader(operation, "If-Match", false,
                    "Optional entity-tag list or wildcard, using strong comparison. Combined conditional headers are limited to 4096 characters and each to 16 tags.");
                AddConditionalHeader(operation, "If-None-Match", false,
                    "Optional entity-tag list or wildcard, using weak comparison after If-Match. Matching returns 304 with no body.");
                AddMetadataResponseHeaders(operation, ["200", "304"]);
                return Task.CompletedTask;
            });
    }

    private static void DescribeMetadataEdit(RouteHandlerBuilder route)
    {
        Describe<CatalogMappingMetadataV1>(route, "EditCatalogMappingMetadata",
            "Atomically replace mapping name and description using one strong metadata If-Match validator.");
        route.WithDescription("The edit document contains exactly the case-sensitive string properties name and description. " +
            "Both are required. The strict UTF-8 body is limited to 65536 bytes with no BOM; unknown or duplicate properties are rejected. " +
            "Name is trimmed, nonblank and limited to 200 Unicode scalars after trimming; description is preserved and limited to 4000 Unicode scalars. " +
            "Neither field permits NUL or invalid Unicode. Media type parameters permit only an optional UTF-8 charset; Content-Encoding permits only identity.")
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict)
            .Produces<CatalogErrorV1>(StatusCodes.Status412PreconditionFailed)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge)
            .Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status428PreconditionRequired)
            .AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
            {
                // Accepts metadata activates media matching before the handler's feature/authority checks.
                // Describe the body only in OpenAPI; runtime admission remains the manual parser's responsibility.
                var document = context.Document ?? throw new InvalidOperationException("The metadata edit schema requires its OpenAPI document.");
                var schema = await context.GetOrCreateSchemaAsync(typeof(CatalogMappingMetadataEditV1),
                    parameterDescription: null, cancellationToken: cancellationToken);
                const string schemaName = nameof(CatalogMappingMetadataEditV1);
                document.AddComponent(schemaName, schema);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [CatalogMetadataEditParser.MediaType] = new()
                        {
                            Schema = new OpenApiSchemaReference(schemaName, document)
                        }
                    }
                };
                AddConditionalHeader(operation, "If-Match", true,
                    "Exactly one concrete strong metadata entity-tag, at most 256 characters with surrounding OWS. Lists, wildcard and additional If-None-Match are unsupported.");
                AddMetadataResponseHeaders(operation, ["200"]);
                AddAcceptPatchHeader(operation, "415");
            });
    }

    private static void AddConditionalHeader(OpenApiOperation operation, string name, bool required, string description)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }

    private static void AddMetadataResponseHeaders(OpenApiOperation operation, string[] statuses)
    {
        foreach (var status in statuses)
        {
            if (operation.Responses?.TryGetValue(status, out var response) == true && response is OpenApiResponse concrete)
            {
                concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                concrete.Headers["ETag"] = new OpenApiHeader
                {
                    Description = "Quoted strong metadata-v1 validator bound to the complete mapping scope and positive version.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                };
            }
            AddAcceptPatchHeader(operation, status);
        }
    }

    private static void AddAcceptPatchHeader(OpenApiOperation operation, string status)
    {
        if (operation.Responses?.TryGetValue(status, out var response) == true && response is OpenApiResponse concrete)
        {
            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concrete.Headers["Accept-Patch"] = new OpenApiHeader
            {
                Description = "Supported edit document media type, present only for an enabled edit capability on an admitted resource.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            };
        }
    }

    private static Task<IResult> PatchMetadata(string organizationId, string projectId, string mappingId,
        HttpContext context, CatalogMetadataEditOptions metadataEditOptions)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!metadataEditOptions.Enabled)
            return Task.FromResult(Error(CatalogErrorCodeV1.Unavailable, StatusCodes.Status404NotFound));
        return WithAdmission(context, context.RequestServices.GetRequiredService<CatalogRequestBoundary>(), async admission =>
        {
            RequireQuery(context);
            var scope = Mapping(organizationId, projectId, mappingId);
            if (scope.OrganizationId != admission.AllowedOrganizationId)
                return NotFound();
            CatalogMapping? observed;
            try
            {
                observed = await context.RequestServices.GetRequiredService<ICatalogReader>()
                    .GetMappingAsync(admission.Actor!, scope, context.RequestAborted);
                context.RequestAborted.ThrowIfCancellationRequested();
            }
            catch (InvalidOperationException)
            {
                return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError);
            }
            if (observed is null)
                return NotFound();
            RequireScope(observed.Summary.Scope == scope);
            RequireAccess(observed.Access, admission.Actor!, Project(scope));
            if (!observed.Access.Can(Braid.Domain.ProjectAction.EditMetadata))
                return NotFound();

            context.Response.Headers["Accept-Patch"] = CatalogMetadataEditParser.MediaType;
            CatalogMetadataEditParser.RequireContent(context.Request);
            var original = new CatalogMappingMetadata(scope, observed.Summary.Name,
                observed.Summary.Description, observed.Summary.Version);
            if (!CatalogMetadataConditions.PatchMatches(context.Request.Headers, CatalogMetadataConditions.ETag(original)))
                return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
            var expected = new MappingMetadataVersion(scope, original.Version);
            var edit = await CatalogMetadataEditParser.ReadAsync(context.Request, context.RequestAborted);

            MappingMetadataWriteResult written;
            try
            {
                written = await context.RequestServices.GetRequiredService<IMappingMetadataWriter>()
                    .ReplaceAsync(admission.Actor!, scope, expected, edit, context.RequestAborted);
                context.RequestAborted.ThrowIfCancellationRequested();
            }
            catch (InvalidOperationException)
            {
                return Error(CatalogErrorCodeV1.PersistenceFailure, StatusCodes.Status500InternalServerError);
            }
            switch (written.Outcome)
            {
                case MappingMetadataWriteOutcome.Unavailable:
                    context.Response.Headers.Remove("Accept-Patch");
                    return NotFound();
                case MappingMetadataWriteOutcome.PreconditionFailed:
                    return Error(CatalogErrorCodeV1.PreconditionFailed, StatusCodes.Status412PreconditionFailed);
                case MappingMetadataWriteOutcome.VersionExhausted:
                    return Error(CatalogErrorCodeV1.VersionExhausted, StatusCodes.Status409Conflict);
                case MappingMetadataWriteOutcome.Updated:
                case MappingMetadataWriteOutcome.Unchanged:
                    var metadata = written.Metadata ?? throw new InvalidOperationException("Successful metadata edit has no metadata.");
                    RequireScope(metadata.Scope == scope && metadata.Name == edit.Name && metadata.Description == edit.Description &&
                        (written.Outcome == MappingMetadataWriteOutcome.Unchanged ? metadata.Version == expected.Version :
                            expected.Version < long.MaxValue && metadata.Version == expected.Version + 1));
                    context.Response.Headers.ETag = CatalogMetadataConditions.ETag(metadata);
                    return Results.Ok(CatalogPublicMapper.Metadata(metadata));
                default:
                    throw new InvalidOperationException("The metadata edit outcome is unknown.");
            }
        }, unsafeRequest: true);
    }
}
