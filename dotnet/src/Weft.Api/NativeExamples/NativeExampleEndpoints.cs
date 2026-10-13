using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Contracts.Examples;
using Weft.MappingBackends;

namespace Weft.Api;

public static class NativeExampleEndpoints
{
    public static IServiceCollection AddWeftNativeExamples(this IServiceCollection services)
    {
        services.AddSingleton<NativeExampleManifest>();
        services.AddSingleton(provider => new NativeExampleService(provider.GetRequiredService<NativeExampleManifest>(),
            provider.GetService<IMappingInspectionBackend>(), provider.GetService<IMappingProjectionBackend>()));
        return services;
    }

    public static IEndpointRouteBuilder MapWeftNativeExamples(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/examples/mappings", GetExamples)
            .WithName("GetNativeExamples").WithTags("Native examples")
            .WithSummary("List immutable self-authored native examples (development only).");
        endpoints.MapGet("/api/v1/examples/mappings/{exampleId}", GetExample)
            .WithName("GetNativeExample").WithTags("Native examples")
            .WithSummary("Read separate native projection and validation reports (development only).");
        return endpoints;
    }

    private static Results<Ok<IReadOnlyList<NativeExampleDescriptorV1>>, NotFound> GetExamples(IHostEnvironment environment, NativeExampleService examples)
        => environment.IsDevelopment() ? TypedResults.Ok(examples.Examples) : TypedResults.NotFound();

    private static async Task<Results<Ok<NativeExampleResultV1>, NotFound>> GetExample(
        string exampleId, IHostEnvironment environment, NativeExampleService examples, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return TypedResults.NotFound();
        var result = await examples.GetAsync(exampleId, cancellationToken).ConfigureAwait(false);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }
}
