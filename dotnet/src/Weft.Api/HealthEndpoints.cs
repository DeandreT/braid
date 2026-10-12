using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Weft.Contracts;

namespace Weft.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapWeftHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/health", () => TypedResults.Ok(
            new HealthResponseV1("ok", "weft-api", "0.1.0", "v1")))
            .WithName("GetHealth")
            .WithTags("Health");
        return endpoints;
    }
}
