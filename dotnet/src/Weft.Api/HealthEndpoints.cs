using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Weft.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapWeftHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/health", () => Results.Ok(new
        {
            Status = "ok",
            Service = "weft-api",
            Version = "0.1.0",
            ApiVersion = "v1"
        }));
        return endpoints;
    }
}
