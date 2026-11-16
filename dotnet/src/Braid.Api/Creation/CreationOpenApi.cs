using Microsoft.OpenApi;

namespace Braid.Api.Creation;

internal static class CreationOpenApi
{
    internal static void Transform(OpenApiDocument document)
    {
        foreach (var (path, item) in document.Paths)
        {
            if (path.EndsWith("/creation-capabilities", StringComparison.Ordinal) && item.Operations is { } capabilities)
                foreach (var operation in capabilities.Values)
                    operation.Description = "Report current server creation-runtime availability under authenticated current project metadata access. Returns available=false when runtime ports are absent or disabled.";
            if (!path.Contains("/creation-proposals", StringComparison.Ordinal) || item.Operations is null) continue;
            foreach (var operation in item.Operations.Values)
            {
                operation.Description = "Creator-private exact retained proposal. Requires current project content and creation authority. " +
                    "Approval requires the exact fingerprint and every critical assumption ID; critical questions block ingestion. " +
                    "Recovery resumes an existing approved operation. Sample preview returns ephemeral verified output.";
                if (operation.OperationId == "ReadCreationProposal")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "expectedFingerprint",
                        In = ParameterLocation.Query,
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[a-f0-9]{64}$", MinLength = 64, MaxLength = 64 }
                    });
                }
                if (operation.RequestBody is OpenApiRequestBody body) body.Required = true;
                if (operation.OperationId == "ListCreationProposals")
                {
                    operation.Description = "Discover creator-private retained proposals under current project content access, independent of chat history and source freshness. Exclusive ascending UUID cursor.";
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "afterProposal",
                        In = ParameterLocation.Query,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                    });
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "limit",
                        In = ParameterLocation.Query,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "20" }
                    });
                }
            }
        }
        if (document.Components?.Schemas is not { } schemas) return;
        foreach (var (name, schema) in schemas)
            if (name.StartsWith("Creation", StringComparison.Ordinal) && schema is OpenApiSchema { Properties: not null } value)
            {
                value.AdditionalPropertiesAllowed = false;
                value.Required = value.Properties.Keys.ToHashSet(StringComparer.Ordinal);
            }
    }
}
