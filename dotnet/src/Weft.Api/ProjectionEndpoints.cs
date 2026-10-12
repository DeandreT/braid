using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Weft.Api.Fixtures;
using Weft.Contracts.Projections;

namespace Weft.Api;

public static class ProjectionEndpoints
{
    public static IEndpointRouteBuilder MapWeftProjectionFixtures(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/fixtures/projections/{fixtureId}", GetFixture)
            .WithName("GetProjectionFixture")
            .WithTags("Projection fixtures")
            .WithSummary("Read a deterministic synthetic mapping projection (development only).");
        return endpoints;
    }

    private static Results<Ok<ProjectionDocumentV1>, NotFound> GetFixture(string fixtureId, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || !ProjectionFixtures.TryGet(fixtureId, out var projection))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(projection!);
    }
}
