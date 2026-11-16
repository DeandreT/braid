using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.OpenApi;
using Braid.Contracts.Chat;

namespace Braid.Api.Chat;

public static partial class ChatEndpoints
{
    private static OpenApiSchema ProposalFingerprintSchema() => new()
    {
        Type = JsonSchemaType.String,
        MinLength = 64,
        MaxLength = 64,
        Pattern = "^[0-9a-f]{64}$"
    };

    private static RouteHandlerBuilder DescribeProposalFingerprint(this RouteHandlerBuilder route) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "expectedFingerprint",
                In = ParameterLocation.Query,
                Required = true,
                Description = "The exact immutable proposal fingerprint being reviewed.",
                Schema = ProposalFingerprintSchema()
            });
            return Task.CompletedTask;
        });

    private static RouteHandlerBuilder DescribeChatResponseHeaders(this RouteHandlerBuilder route, bool stream) =>
        route.AddOpenApiOperationTransformer((operation, context, _) =>
        {
            if (context.Description.HttpMethod == "POST")
            {
                operation.Parameters ??= [];
                foreach (var (name, description) in new[]
                {
                    ("Origin", "The exact configured origin for this browser request."),
                    ("X-XSRF-TOKEN", "The antiforgery request token issued by the catalog context; session tokens bind to the session epoch.")
                })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = description,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
            }
            if (operation.Responses is not null)
                foreach (var (status, response) in operation.Responses)
                    if (response is OpenApiResponse concrete)
                    {
                        concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        concrete.Headers["Cache-Control"] = new OpenApiHeader
                        {
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no-store")!] }
                        };
                        if (stream && status == "200")
                            concrete.Headers["X-Accel-Buffering"] = new OpenApiHeader
                            {
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("no")!] }
                            };
                    }
            return Task.CompletedTask;
        });

    private static RouteHandlerBuilder DescribeWorkspaceQueries(this RouteHandlerBuilder route, bool listing) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = listing ? "limit" : "after",
                In = ParameterLocation.Query,
                Description = listing ? "Maximum conversations, newest first; canonical decimal."
                    : "Resume strictly after this durable event sequence; canonical decimal.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Minimum = listing ? "1" : "0",
                    Maximum = listing ? "100" : ChatGrounding.MaxEvents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Default = JsonValue.Create(listing ? 50 : 0)
                }
            });
            if (listing)
                foreach (var (name, description) in new[]
                {
                    ("after", "Continue after the last conversation identity from the previous page."),
                    ("revisionId", "Only conversations about this freshly readable exact revision.")
                })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name,
                        In = ParameterLocation.Query,
                        Description = description,
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = "uuid",
                            Pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
                        }
                    });
            return Task.CompletedTask;
        });

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
        if (schemas.TryGetValue(nameof(ChatMappingEditApprovalV1), out var approvalSchema) && approvalSchema is OpenApiSchema { Properties: { } approval } approvalObject)
        {
            approval["expectedFingerprint"] = ProposalFingerprintSchema();
            approvalObject.Required = new HashSet<string> { "expectedFingerprint" };
            approvalObject.AdditionalPropertiesAllowed = false;
        }
        foreach (var name in new[] { nameof(ChatMappingEditProposalV1), nameof(ChatMappingEditOperationsV1) })
            if (schemas.TryGetValue(name, out var proposalSchema) && proposalSchema is OpenApiSchema { Properties: { } proposal })
            {
                proposal["fingerprint"] = ProposalFingerprintSchema();
                proposal["operationsDigest"] = ProposalFingerprintSchema();
                if (proposal.TryGetValue("canonicalScript", out var script) && script is OpenApiSchema scriptSchema)
                    scriptSchema.MaxLength = 1024 * 1024;
                if (proposal.TryGetValue("changes", out var changes) && changes is OpenApiSchema changesSchema)
                    changesSchema.MaxItems = 64;
                if (proposal.ContainsKey("status")) proposal["status"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = new[] { "awaitingApproval", "approved", "pending", "saved", "notSaved", "notPermitted", "outOfDate" }
                        .Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
                };
            }
        if (schemas.TryGetValue(nameof(ChatMappingEditProposalPageV1), out var pageSchema) && pageSchema is OpenApiSchema { Properties: { } page } &&
            page.TryGetValue("proposals", out var proposalsSchema) && proposalsSchema is OpenApiSchema proposals)
            proposals.MaxItems = 8;
        if (schemas.TryGetValue(nameof(ChatMappingWriteReceiptV1), out var receiptSchema) && receiptSchema is OpenApiSchema { Properties: { } receipt })
        {
            receipt["status"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create("pending")!, JsonValue.Create("saved")!, JsonValue.Create("notSaved")!]
            };
            receipt["write"] = new OpenApiSchema
            {
                OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null },
                new OpenApiSchemaReference(nameof(ChatTurnEventV1) + nameof(ChatMappingWriteEventV1), document)]
            };
        }
        foreach (var name in new[] { nameof(ChatAvailabilityV1), nameof(ChatWorkspaceAvailabilityV1) })
        {
            if (schemas.TryGetValue(name, out var availabilitySchema) &&
                availabilitySchema is OpenApiSchema { Properties: { } availability } availabilityObject)
            {
                availability["schemaVersion"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("braid.chat.v1")!],
                    ReadOnly = true
                };
                availabilityObject.Required ??= new HashSet<string>();
                availabilityObject.Required.Add("schemaVersion");
                availabilityObject.Required.Add("profiles");
            }
        }
        if (schemas.TryGetValue(nameof(ChatFocusV1), out var focusSchema) && focusSchema is OpenApiSchema focus)
            focus.AdditionalPropertiesAllowed = false;
        Nullable(nameof(ChatOrganizationConversationV1), "focus", nameof(ChatFocusV1));
        Nullable(nameof(ChatOrganizationConversationV1), "revision", "CatalogRevisionScopeV1");
        if (schemas.TryGetValue(nameof(ChatWorkspaceConversationCreateV1), out var createSchema) &&
            createSchema is OpenApiSchema { Properties: { } createProperties } create &&
            focusSchema is OpenApiSchema { Properties: { } focusProperties })
        {
            create.Required?.Remove("focus");
            createProperties["focus"] = new OpenApiSchema
            {
                OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>(focusProperties),
                    Required = new HashSet<string> { "projectId", "mappingId" },
                    AdditionalPropertiesAllowed = false
                }]
            };
        }
        Nullable(nameof(ChatTurnFocusV1), "revision", "CatalogRevisionScopeV1");
        Nullable(nameof(ChatTurnV1), "focus", nameof(ChatTurnFocusV1));
        Nullable(nameof(ChatWorkspaceCitationV1), "target", nameof(ChatSelectorV1));

        void Nullable(string owner, string property, string target)
        {
            if (schemas.TryGetValue(owner, out var schema) && schema is OpenApiSchema { Properties: { } properties })
                properties[property] = new OpenApiSchema
                {
                    OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, new OpenApiSchemaReference(target, document)]
                };
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
