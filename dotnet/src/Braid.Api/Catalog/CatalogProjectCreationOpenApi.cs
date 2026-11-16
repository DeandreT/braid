using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void DescribeProjectCreation(RouteHandlerBuilder route)
    {
        Describe<CatalogProjectCreationResultV1>(route, "CreateCatalogProject",
            "Atomically create one project and its original actor's first Administrator membership, or replay its original creation.");
        route.Produces<CatalogProjectCreationResultV1>(StatusCodes.Status201Created)
            .Produces<CatalogErrorV1>(StatusCodes.Status409Conflict)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge)
            .Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable)
            .WithDescription("Requires fresh active organization admission and an explicit project-creation capability, independent of existing project roles. " +
                "The document contains exactly the case-sensitive string properties name and description; both are required, unknown and duplicate fields are rejected. " +
                "The strict UTF-8 entity is limited to 65536 bytes with no BOM, comments, trailing commas or decompression. " +
                "Name is trimmed once and limited to 1–200 Unicode scalars and 800 UTF-8 bytes; description is preserved exactly and limited to 4000 scalars and 16000 UTF-8 bytes. " +
                "Neither permits NUL or invalid Unicode. Only an optional UTF-8 charset and absent or one identity Content-Encoding are supported. " +
                "No query, If-Match or If-None-Match fields are supported. One canonical nonzero lowercase UUID Idempotency-Key is required. " +
                "Fresh capability admission and bounded immediate work admission precede body consumption; the final short atomic operation rechecks authority. " +
                "201 means actual creation and 200 exact replay after commit; changed intent or another actor's key is opaque 409. " +
                "Initial membership facts are historical and never establish current project access or repair a revoked membership.")
            .AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
            {
                // Describe the document only in OpenAPI. Runtime Accepts/body
                // binding would preempt the feature and trusted authority checks.
                var document = context.Document ?? throw new InvalidOperationException("Project creation schemas require their OpenAPI document.");
                var requestSchema = await context.GetOrCreateSchemaAsync(typeof(CatalogProjectCreateV1),
                    parameterDescription: null, cancellationToken: cancellationToken);
                requestSchema.AdditionalPropertiesAllowed = false;
                requestSchema.Required ??= new HashSet<string>();
                requestSchema.Required.Add("name");
                requestSchema.Required.Add("description");
                const string schemaName = nameof(CatalogProjectCreateV1);
                document.AddComponent(schemaName, requestSchema);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [CatalogProjectCreationParser.MediaType] = new()
                        {
                            Schema = new OpenApiSchemaReference(schemaName, document)
                        }
                    }
                };
                AddProjectCreationHeader(operation, "Idempotency-Key", true,
                    "Exactly one nonzero canonical lowercase D-format UUID with only surrounding SP/HTAB OWS allowed. Raw value at most 128 characters; duplicates and lists are rejected.", maximumLength: 128);
                AddProjectCreationHeader(operation, "Origin", true,
                    "One exact canonical configured browser origin, checked before any body read.");
                AddProjectCreationHeader(operation, CatalogRequestBoundary.RequestTokenHeader, true,
                    "Cookie-bound framework request token from a successfully admitted catalog context, at most 4096 characters.", maximumLength: 4096);
                AddProjectCreationHeader(operation, "Content-Encoding", false,
                    "Absent or one identity value; compressed and multiple codings are unsupported.");
                if (operation.Responses is not null)
                    foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                    {
                        response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        response.Headers["Cache-Control"] = new OpenApiHeader
                        {
                            Description = "Always no-store.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no-store")!] }
                        };
                    }
                foreach (var status in new[] { "200", "201" })
                    if (operation.Responses?.TryGetValue(status, out var response) == true && response is OpenApiResponse successful)
                    {
                        successful.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        successful.Headers["Location"] = new OpenApiHeader
                        {
                            Description = "Exact full-scoped relative ordinary GET project URL. That read still checks current project membership.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                        };
                    }
            });
    }

    private static void AddProjectCreationHeader(OpenApiOperation operation, string name, bool required,
        string description, int? maximumLength = null)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = maximumLength }
        });
    }
}
