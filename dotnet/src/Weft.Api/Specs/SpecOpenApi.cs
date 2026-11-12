using Microsoft.OpenApi;

namespace Weft.Api.Specs;

internal static class SpecOpenApi
{
    internal static void Transform(OpenApiDocument document)
    {
        foreach (var (path, item) in document.Paths)
        {
            if (!path.Contains("/spec-bundles/", StringComparison.Ordinal) || item.Operations is null) continue;
            foreach (var (method, operation) in item.Operations)
            {
                operation.Description = "Explicit project content grants are required. No AI-sharing authority is granted. " +
                    "Unavailable until the exact registry schema and private Blob backend are admitted. Development identity bypass is refused. " +
                    "On an uncertain mutation, retry the exact identities and bytes; do not allocate replacements.";
                operation.Parameters ??= [];
                if (operation.OperationId == "ReadSpecExtraction")
                    operation.Responses!["200"] = new OpenApiResponse
                    {
                        Description = "Exact canonical weft.spec-evidence.v1 JSON array: format marker, fixed outcome code, then zero or more chunk tuples [ordinal, text, nullable JSON pointer, byte offset, byte length, start line, end line]. Limit failures contain no chunks.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Array,
                                    MinItems = 2,
                                    Items = new OpenApiSchema
                                    {
                                        OneOf = [new OpenApiSchema { Type = JsonSchemaType.String },
                                    new OpenApiSchema { Type = JsonSchemaType.Array, MinItems = 7, MaxItems = 7,
                                        Items = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Integer | JsonSchemaType.Null } }]
                                    }
                                }
                            }
                        }
                    };
                if (operation.OperationId is ("GetSpecBundleInventory" or "CreateSpecBundle" or "TombstoneSpecBundle" or "TombstoneSpecSource") &&
                    operation.Responses!["200"] is OpenApiResponse versionResponse)
                    versionResponse.Headers = new Dictionary<string, IOpenApiHeader>
                    {
                        ["X-Weft-Spec-Version"] = new OpenApiHeader
                        {
                            Description = "Positive mutation CAS version, also in the response body. Not a representation validator: publication acknowledgements can change inventory without changing this version. No ETag is issued.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
                        }
                    };
                if (method == HttpMethod.Post || method == HttpMethod.Delete)
                    Header(operation, "If-Match", "One quoted positive bundle version, for example \"1\". Required; stale CAS answers 409. Original reservation replay retains its original version.", 22);
                if (method == HttpMethod.Delete)
                    Header(operation, "Idempotency-Key", "Stable nonzero UUID for exact tombstone replay. Logical deletion only; retirement is asynchronous maintenance.", 36);
                if (path.EndsWith("/uploads", StringComparison.Ordinal))
                {
                    Header(operation, SpecEndpoints.UploadHeader, "Strict JSON SpecUploadV1: sourceId, sourceVersion, originalObjectId, uploadKey, extractionObjectId, extractionKey, extractionExpectedVersion, format. All fields required; IDs nonzero and publication IDs/keys distinct. Format: text, json, openapi-json. Raw body at most 10 MiB. The extraction CAS normally starts at If-Match+1; after a conflict before its reservation, refresh inventory and update only extractionExpectedVersion. Malformed source content is retained with a Malformed extraction outcome.", 2048);
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        { ["application/octet-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", MaxLength = 10 * 1024 * 1024 } } }
                    };
                }
            }
        }
        if (document.Components?.Schemas is not { } schemas) return;
        foreach (var (name, schema) in schemas)
            if (name.StartsWith("Spec", StringComparison.Ordinal) && schema is OpenApiSchema { Properties: not null } concrete)
            {
                concrete.AdditionalPropertiesAllowed = false;
                concrete.Required = concrete.Properties.Keys.ToHashSet(StringComparer.Ordinal);
            }
    }
    private static void Header(OpenApiOperation operation, string name, string description, int maximum) =>
        operation.Parameters!.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = maximum }
        });
}
