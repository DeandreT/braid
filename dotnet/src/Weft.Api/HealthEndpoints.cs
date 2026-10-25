using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Contracts;
using Weft.MappingBackends.Workers;

namespace Weft.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapWeftHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/health", () => TypedResults.Ok(new HealthResponseV1("ok", "weft-api", "0.1.0", "v1")))
            .WithName("GetHealth")
            .WithTags("Health");
        endpoints.MapGet("/api/v1/health/ready", Results<Ok<HealthResponseV1>, JsonHttpResult<HealthResponseV1>> (HttpContext context) =>
            context.RequestServices.GetService<IInspectionWorkerRunner>() is IExecutionAdmission { CanExecute: false }
                ? TypedResults.Json(new HealthResponseV1("unavailable", "weft-api", "0.1.0", "v1"), statusCode: StatusCodes.Status503ServiceUnavailable)
                : TypedResults.Ok(new HealthResponseV1("ok", "weft-api", "0.1.0", "v1")))
            .Produces<HealthResponseV1>(StatusCodes.Status503ServiceUnavailable)
            .WithName("GetReadiness")
            .WithTags("Health");
        return endpoints;
    }
}
