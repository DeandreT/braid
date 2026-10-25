using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Weft.Contracts.Catalog;
using Weft.Contracts.Editing;

namespace Weft.Api.Catalog;

public static partial class CatalogEndpoints
{
    internal static void TransformEditSchemas(OpenApiDocument document)
    {
        if (document.Components?.Schemas is not { } schemas) return;
        foreach (var type in typeof(EditScriptV1).Assembly.GetTypes().Where(type => type.Namespace == typeof(EditScriptV1).Namespace))
        {
            if (schemas.TryGetValue(type.Name, out var root) && root is OpenApiSchema { Properties: not null } objectSchema)
                objectSchema.AdditionalPropertiesAllowed = false;
            var polymorphic = type.GetCustomAttribute<JsonPolymorphicAttribute>();
            if (polymorphic?.TypeDiscriminatorPropertyName is not { } discriminator) continue;
            foreach (var variant in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
                if (schemas.TryGetValue(type.Name + variant.DerivedType.Name, out var entry) && entry is OpenApiSchema schema)
                {
                    schema.AdditionalPropertiesAllowed = false;
                    schema.Required ??= new HashSet<string>();
                    schema.Required.Add(discriminator);
                }
        }
        if (schemas.TryGetValue(nameof(EditScriptV1), out var script) && script is OpenApiSchema { Properties: { } properties })
        {
            properties["format"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("ferrule.edit.v1")!] };
            if (properties["operations"] is OpenApiSchema operations) operations.MaxItems = 2000;
        }
    }

    private static async Task DraftErrorSchemas(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken token, bool save)
    {
        var document = context.Document ?? throw new InvalidOperationException("Draft errors require their OpenAPI document.");
        foreach (var type in new[] { typeof(CatalogErrorV1), typeof(MappingDraftResultV1), typeof(CatalogRevisionIngestionResultV1) })
        {
            var schema = await context.GetOrCreateSchemaAsync(type, parameterDescription: null, cancellationToken: token);
            document.AddComponent(type.Name, schema);
        }
        var statuses = save ? new[] { "409", "413", "422", "503" } : new[] { "503" };
        foreach (var status in statuses)
        {
            var names = save ? new[] { nameof(CatalogErrorV1), nameof(MappingDraftResultV1), nameof(CatalogRevisionIngestionResultV1) }
                : new[] { nameof(CatalogErrorV1), nameof(MappingDraftResultV1) };
            operation.Responses ??= new();
            operation.Responses[status] = new OpenApiResponse
            {
                Description = "A bounded request, replay or ingestion outcome; no native bytes are accepted from the caller.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = new OpenApiSchema { OneOf = names.Select(name => (IOpenApiSchema)new OpenApiSchemaReference(name, document)).ToList() } }
                }
            };
        }
    }

    private static RouteHandlerBuilder DraftBody<T>(RouteHandlerBuilder route, bool match = false, bool key = false)
    {
        route.Produces<CatalogErrorV1>(StatusCodes.Status413PayloadTooLarge).Produces<CatalogErrorV1>(StatusCodes.Status415UnsupportedMediaType)
            .AddOpenApiOperationTransformer(async (operation, context, token) =>
            {
                var document = context.Document ?? throw new InvalidOperationException("Draft schemas require their OpenAPI document.");
                var schema = await context.GetOrCreateSchemaAsync(typeof(T), parameterDescription: null, cancellationToken: token);
                document.AddComponent(typeof(T).Name, schema);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = new OpenApiSchemaReference(typeof(T).Name, document) } }
                };
                if (key) AddReportParameter(operation, "Idempotency-Key", ParameterLocation.Header, true, "One canonical nonzero lowercase UUID. Repeating the same intent recovers its original receipt.", uuid: true);
            });
        return match ? DraftCondition(route) : route;
    }

    private static RouteHandlerBuilder DraftCondition(RouteHandlerBuilder route) => route.AddOpenApiOperationTransformer((operation, _, _) =>
    {
        AddReportParameter(operation, "If-Match", ParameterLocation.Header, true, "The draft's current strong ETag. Missing is 428; a stale version is 412. Weak tags, wildcards, lists and If-None-Match are refused.");
        return Task.CompletedTask;
    });
}
