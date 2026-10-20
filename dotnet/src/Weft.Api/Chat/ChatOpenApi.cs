using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Weft.Contracts.Chat;

namespace Weft.Api.Chat;

public static partial class ChatEndpoints
{
    /// <summary>OpenAPI only: runtime Accepts metadata would refuse media types before chat admission.</summary>
    private static RouteHandlerBuilder DescribeBody<T>(this RouteHandlerBuilder route) =>
        route.AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
        {
            var document = context.Document ?? throw new InvalidOperationException("Chat request schemas require their OpenAPI document.");
            var schema = await context.GetOrCreateSchemaAsync(typeof(T), parameterDescription: null, cancellationToken: cancellationToken);
            schema.AdditionalPropertiesAllowed = false;
            document.AddComponent(typeof(T).Name, schema);
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = new OpenApiSchemaReference(typeof(T).Name, document) }
                }
            };
        });

    /// <summary>Exact chat vocabulary for generated clients, applied in the final document transform.</summary>
    internal static void TransformSchemas(OpenApiDocument document)
    {
        if (document.Components?.Schemas is not { } schemas)
            return;
        if (schemas.TryGetValue(nameof(ChatAvailabilityV1), out var availabilitySchema) &&
            availabilitySchema is OpenApiSchema { Properties: { } availability } availabilityObject)
        {
            availability["schemaVersion"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("weft.chat.v1")!],
                ReadOnly = true
            };
            availabilityObject.Required ??= new HashSet<string>();
            availabilityObject.Required.Add("schemaVersion");
            availabilityObject.Required.Add("profiles");
        }
        foreach (var (name, schema) in schemas)
        {
            // Each SSE variant carries its discriminator; clients narrow on it exhaustively.
            if (name.StartsWith(nameof(ChatTurnEventV1), StringComparison.Ordinal) && name != nameof(ChatTurnEventV1) &&
                schema is OpenApiSchema { Properties: { } properties } variant && properties.ContainsKey("type"))
            {
                variant.Required ??= new HashSet<string>();
                variant.Required.Add("type");
                if (properties.ContainsKey("providerFailure"))
                    properties["providerFailure"] = new OpenApiSchema
                    {
                        OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, new OpenApiSchemaReference(nameof(ChatProviderFailureV1), document)]
                    };
            }
        }
        if (schemas.TryGetValue(nameof(ChatProviderFailureV1), out var failureSchema) && failureSchema is OpenApiSchema failure)
        {
            // Null belongs to the nullable event property, not the shared closed enum.
            failure.Type = JsonSchemaType.String;
            failure.Enum = Enum.GetNames<ChatProviderFailureV1>()
                .Select(value => (JsonNode)JsonValue.Create(char.ToLowerInvariant(value[0]) + value[1..])!).ToList();
        }
    }
}
