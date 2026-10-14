using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Weft.Contracts.Catalog;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    private static void DescribeRevisionUpload(RouteHandlerBuilder route, bool create)
    {
        DescribeIngestionMutation(route, create ? "CreateCatalogMapping" : "AppendCatalogRevision",
            create ? "Create a mapping and its first revision from exact native bytes." :
                "Append one native revision using the mapping's persisted backend.");
        route.WithDescription("One raw application/octet-stream source with no media parameters, at most 4194304 bytes. " +
            "Content-Encoding permits only identity. A canonical nonzero UUID Idempotency-Key is required; If-Match and If-None-Match are unsupported. " +
            "The exact body is consumed only after fresh Publisher or Administrator authority and bounded work admission. " +
            "Accepted results expose safe own receipts only; only committed outcomes include a committed revision URL.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                // OpenAPI only: runtime Accepts metadata would preempt feature/authority checks.
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        [CatalogRevisionRequestParser.MediaType] = new()
                        {
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                        }
                    }
                };
                AddRevisionParameter(operation, "Idempotency-Key", ParameterLocation.Header, true,
                    "One canonical nonzero lowercase UUID, with surrounding protocol OWS allowed. Repeated fields and lists are rejected.", uuid: true);
                AddRevisionParameter(operation, "Content-Encoding", ParameterLocation.Header, false,
                    "Absent or one identity token; compressed or multiple codings are unsupported.");
                if (create)
                {
                    AddRevisionParameter(operation, "name", ParameterLocation.Query, true,
                        "Required display name: trim before nonblank and 200 Unicode scalar validation; valid Unicode and no NUL.");
                    AddRevisionParameter(operation, "backendId", ParameterLocation.Query, true,
                        "Required exact backend ID, nonblank and at most 128 UTF-8 bytes; no profile or default backend is accepted.");
                    AddRevisionParameter(operation, "description", ParameterLocation.Query, false,
                        "Preserved text, default empty, at most 4000 Unicode scalars; valid Unicode and no NUL.");
                }
                else
                {
                    AddRevisionParameter(operation, "head", ParameterLocation.Query, false,
                        "Exact any (default) or equal. equal requires expectedHeadRevisionId; any forbids it.", values: ["any", "equal"]);
                    AddRevisionParameter(operation, "expectedHeadRevisionId", ParameterLocation.Query, false,
                        "Canonical nonzero lowercase revision UUID, required only with head=equal.", uuid: true);
                }
                AddRevisionParameter(operation, "originalFilename", ParameterLocation.Query, false,
                    "Safe display basename, default entry.native.json; at most 255 UTF-8 bytes, no control characters or path separators.");
                return Task.CompletedTask;
            });
    }

    private static void DescribeIngestionReceipt(RouteHandlerBuilder route)
    {
        Describe<CatalogRevisionIngestionReceiptV1>(route, "GetCatalogRevisionIngestionReceipt",
            "Read the original actor's safe durable revision ingestion receipt after fresh upload authority.");
        route.WithDescription("No query fields. Fresh Publisher or Administrator authority and original operation ownership are required. " +
            "This read never acquires a work permit, claims, executes, or touches native storage. Future scopes do not imply that resources exist.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                AddIngestionNoStoreHeaders(operation);
                return Task.CompletedTask;
            });
    }

    private static void DescribeIngestionResume(RouteHandlerBuilder route)
    {
        DescribeIngestionMutation(route, "ResumeCatalogRevisionIngestion",
            "Attempt one explicit source-free recovery of the original actor's ingestion operation.");
        route.WithDescription("No query fields, Idempotency-Key, If-Match, If-None-Match or body. " +
            "Fresh own-receipt authority precedes the bounded empty-body check and immediate work admission. " +
            "This synchronous operation attempts one claim without retry; terminal results replay and missing reserved native source yields NeedsSource.");
    }

    private static void DescribeIngestionMutation(RouteHandlerBuilder route, string name, string summary)
    {
        Describe<CatalogRevisionIngestionResultV1>(route, name, summary);
        route.Produces<CatalogRevisionIngestionResultV1>(StatusCodes.Status202Accepted)
            .Produces<CatalogRevisionIngestionResultV1>(StatusCodes.Status409Conflict)
            .Produces<CatalogRevisionIngestionResultV1>(StatusCodes.Status422UnprocessableEntity)
            .Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge)
            .Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<CatalogErrorV1>(StatusCodes.Status503ServiceUnavailable)
            .AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
            {
                var document = context.Document ?? throw new InvalidOperationException("Ingestion schemas require their OpenAPI document.");
                var result = await context.GetOrCreateSchemaAsync(typeof(CatalogRevisionIngestionResultV1),
                    parameterDescription: null, cancellationToken: cancellationToken);
                var error = await context.GetOrCreateSchemaAsync(typeof(CatalogErrorV1),
                    parameterDescription: null, cancellationToken: cancellationToken);
                document.AddComponent(nameof(CatalogRevisionIngestionResultV1), result);
                document.AddComponent(nameof(CatalogErrorV1), error);
                // These statuses distinguish actual C2 outcomes from preflight/transport/busy failures.
                foreach (var status in new[] { "400", "404", "413", "503" })
                    if (operation.Responses?.TryGetValue(status, out var response) == true && response is OpenApiResponse concrete)
                        concrete.Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    OneOf = [new OpenApiSchemaReference(nameof(CatalogRevisionIngestionResultV1), document),
                                        new OpenApiSchemaReference(nameof(CatalogErrorV1), document)]
                                }
                            }
                        };
                if (operation.Responses?.TryGetValue("202", out var pending) == true && pending is OpenApiResponse accepted)
                {
                    accepted.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    accepted.Headers["Location"] = new OpenApiHeader
                    {
                        Description = "Canonical full-scoped own receipt URL for the actual InProgress operation.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    };
                }
                AddIngestionNoStoreHeaders(operation);
            });
    }

    private static void AddRevisionParameter(OpenApiOperation operation, string name, ParameterLocation location,
        bool required, string description, bool uuid = false, string[]? values = null)
    {
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = uuid ? "uuid" : null };
        if (values is not null)
            schema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = location,
            Required = required,
            Description = description,
            Schema = schema
        });
    }

    private static void AddIngestionNoStoreHeaders(OpenApiOperation operation)
    {
        if (operation.Responses is null)
            return;
        foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["Cache-Control"] = new OpenApiHeader
            {
                Description = "Always no-store; receipts and operation results are not cacheable.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no-store")!] }
            };
        }
    }
}
