using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Contracts;
using Braid.MappingBackends.Workers;

namespace Braid.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapBraidHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/health", () => TypedResults.Ok(new HealthResponseV1("ok", "braid-api", "0.1.0", "v1")))
            .WithName("GetHealth")
            .WithTags("Health");
        endpoints.MapGet("/api/v1/health/ready", Results<Ok<HealthResponseV1>, JsonHttpResult<HealthResponseV1>> (HttpContext context) =>
            context.RequestServices.GetService<IInspectionWorkerRunner>() is IExecutionAdmission { CanExecute: false }
                ? TypedResults.Json(new HealthResponseV1("unavailable", "braid-api", "0.1.0", "v1"), statusCode: StatusCodes.Status503ServiceUnavailable)
                : TypedResults.Ok(new HealthResponseV1("ok", "braid-api", "0.1.0", "v1")))
            .Produces<HealthResponseV1>(StatusCodes.Status503ServiceUnavailable)
            .WithName("GetReadiness")
            .WithTags("Health");
        return endpoints;
    }
}
