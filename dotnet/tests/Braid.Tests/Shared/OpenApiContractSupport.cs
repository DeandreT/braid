using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace Braid.Tests;

/// <summary>
/// Compares a committed OpenAPI document with what its host generates and with the routes a live host serves. The build
/// rewrites the committed documents; with generation off, or without committing the rewrite, these comparisons and the
/// repository's generated-contract diff fail.
/// </summary>
internal static class OpenApiContractSupport
{
    /// <summary>A committed document, copied into the test output by the build.</summary>
    internal static JsonObject Committed(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", file)))!.AsObject();

    /// <summary>The document a document-only host generates, as build-time generation reads it.</summary>
    internal static async Task<JsonObject> GenerateAsync(IHost host)
    {
        await host.StartAsync();
        try
        {
            var document = await host.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync();
            return JsonNode.Parse(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1))!.AsObject();
        }
        finally { await host.StopAsync(); }
    }

    internal static void Same(JsonObject committed, JsonObject generated, string file) =>
        Assert.True(JsonNode.DeepEquals(committed, generated),
            $"contracts/{file} differs from the generated document at {Difference(committed, generated, "#")}; build the host and commit the rewritten file.");

    /// <summary>The JSON pointer of the first difference.</summary>
    private static string Difference(JsonNode? committed, JsonNode? generated, string pointer)
    {
        if (committed is JsonObject left && generated is JsonObject right)
        {
            foreach (var name in left.Select(property => property.Key).Union(right.Select(property => property.Key)))
                if (!JsonNode.DeepEquals(left[name], right[name]))
                    return Difference(left[name], right[name], pointer + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        }
        else if (committed is JsonArray first && generated is JsonArray second && first.Count == second.Count)
        {
            for (var index = 0; index < first.Count; index++)
                if (!JsonNode.DeepEquals(first[index], second[index])) return Difference(first[index], second[index], pointer + "/" + index);
        }
        return pointer;
    }

    /// <summary>
    /// Each live route appears in the document with its method, operation ID and exactly its declared statuses. With
    /// <paramref name="complete"/>, the document describes no other route.
    /// </summary>
    internal static void Serves(JsonObject document, IEnumerable<Endpoint> endpoints, bool complete)
    {
        var paths = document["paths"]!.AsObject();
        var served = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var path = endpoint.RoutePattern.RawText!;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            Assert.True(methods is { Count: > 0 }, path + " is mapped without a method.");
            foreach (var method in methods)
            {
                var key = method.ToLowerInvariant() + " " + path;
                Assert.True(paths[path]?[method.ToLowerInvariant()] is JsonObject, key + " is served but not documented.");
                var operation = paths[path]![method.ToLowerInvariant()]!.AsObject();
                Assert.Equal(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName, operation["operationId"]?.GetValue<string>());
                var declared = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                    .Select(response => response.StatusCode.ToString(CultureInfo.InvariantCulture)).Distinct().Order(StringComparer.Ordinal);
                Assert.Equal(declared, operation["responses"]!.AsObject().Select(response => response.Key).Order(StringComparer.Ordinal));
                served.Add(key);
            }
        }

        if (!complete) return;
        var documented = paths.SelectMany(path => path.Value!.AsObject().Select(operation => operation.Key + " " + path.Key));
        Assert.Equal(documented.Order(StringComparer.Ordinal), served.Order(StringComparer.Ordinal));
    }

    internal static IEnumerable<Endpoint> Endpoints(IServiceProvider services) => services.GetRequiredService<EndpointDataSource>().Endpoints;

    /// <summary>A strict reader: unknown members fail, and only the exact property names match.</summary>
    internal static JsonSerializerOptions Strict(JsonSerializerOptions options) => new(options)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };
}
