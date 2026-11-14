using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Chat;
using Weft.Contracts.Projections;

namespace Weft.Api;

public static class ApiConfiguration
{
    public static IServiceCollection AddWeftApi(this IServiceCollection services)
    {
        services.AddWeftRegressionCases();
        services.AddWeftRegressionRuns();
        services.AddWeftBuildApi();
        services.AddSingleton(new ReleaseOperationsOptions());
        services.AddSingleton(new LocalSandboxOptions());
        services.AddSingleton(new CatalogAutomationOptions(false));
        services.ConfigureHttpJsonOptions(options => ProjectionJson.Configure(options.SerializerOptions));
        services.AddOpenApi(options => options.AddDocumentTransformer((document, context, _) =>
        {
            document.Info.Title = "Weft API";
            document.Info.Version = "v1";
            document.Servers = [];
            if (document.Components?.Schemas is { } schemas)
            {
                // ASP.NET requires the discriminator on the union, but clients also
                // need it on each referenced variant for exhaustive narrowing.
                foreach (var schema in schemas.Values.OfType<OpenApiSchema>())
                {
                    if (schema.Properties?.TryGetValue("kind", out var kind) == true && kind.Enum?.Count == 1)
                    {
                        schema.Required ??= new HashSet<string>();
                        schema.Required.Add("kind");
                    }
                }

                if (schemas.TryGetValue(nameof(ProjectionDocumentV1), out var documentSchema) &&
                    documentSchema is OpenApiSchema projection && projection.Properties is { } properties)
                {
                    properties["schemaVersion"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Enum = [JsonValue.Create("weft.mapping-projection.v1")!],
                        ReadOnly = true
                    };
                    projection.Required ??= new HashSet<string>();
                    projection.Required.Add("schemaVersion");
                }
            }
            CatalogEndpoints.TransformRevisionIngestionSchemas(document);
            CatalogEndpoints.TransformRevisionReportSchemas(document);
            CatalogEndpoints.TransformPreviewSchemas(document);
            ChatEndpoints.TransformSchemas(document);
            CatalogEndpoints.TransformEditSchemas(document);
            AuthenticationOpenApi.Transform(document);
            // Last: every operation, including the authentication routes added above, declares its admitted schemes.
            ApiSecurity.Transform(document, context.DescriptionGroups);
            Weft.Api.Specs.SpecOpenApi.Transform(document);
            Weft.Api.Creation.CreationOpenApi.Transform(document);
            return Task.CompletedTask;
        }));
        return services;
    }
}
