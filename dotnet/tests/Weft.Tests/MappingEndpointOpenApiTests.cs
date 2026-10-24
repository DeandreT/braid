using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.Identity;
using Weft.MachineEndpoint;
using static Weft.Tests.OpenApiContractSupport;

namespace Weft.Tests;

/// <summary>
/// <c>contracts/weft-endpoint-v1.json</c> against the endpoint's document-only host, its live routes and the bodies the
/// endpoint actually reads and writes.
/// </summary>
public sealed class MappingEndpointOpenApiTests
{
    private const string File = "weft-endpoint-v1.json";

    [Fact]
    public async Task The_committed_specification_is_the_document_the_endpoint_generates()
    {
        // Only build-time generation selects the document-only host.
        Assert.False(MappingEndpointOpenApi.Generating);
        using var host = MappingEndpointOpenApi.BuildDocumentHost();
        var generated = await GenerateAsync(host);
        Same(Committed(File), generated, File);
        // The document-only host maps exactly the endpoint's own registrations.
        Serves(generated, Endpoints(host.Services), complete: true);
    }

    [Fact]
    public void The_specification_describes_the_client_credentials_call_and_every_refusal()
    {
        var document = Committed(File);
        Assert.StartsWith("3.1.", document["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var scheme = document["components"]!["securitySchemes"]![MappingEndpointOpenApi.MachineToken]!;
        Assert.Equal("oauth2", scheme["type"]!.GetValue<string>());
        var flow = scheme["flows"]!["clientCredentials"]!;
        Assert.Equal("https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token", flow["tokenUrl"]!.GetValue<string>());
        Assert.Equal([MappingEndpointOpenApi.Scope], flow["scopes"]!.AsObject().Select(scope => scope.Key));

        var invoke = document["paths"]![MappingEndpointRoutes.InvocationsPath]!["post"]!;
        Assert.Equal(MappingEndpointOpenApi.Scope, invoke["security"]![0]![MappingEndpointOpenApi.MachineToken]![0]!.GetValue<string>());
        var requestId = Assert.Single(invoke["parameters"]!.AsArray());
        Assert.Equal(MappingEndpointRoutes.RequestIdHeader, requestId!["name"]!.GetValue<string>());
        Assert.True(requestId["required"]!.GetValue<bool>());
        Assert.Equal("#/components/schemas/MappingInvocationRequestV1",
            invoke["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
        var responses = invoke["responses"]!.AsObject();
        Assert.Equal(["200", "400", "401", "403", "409", "410", "413", "415", "422", "500", "503"], responses.Select(response => response.Key));
        Assert.Equal("Bearer", responses["401"]!["headers"]!["WWW-Authenticate"]!["schema"]!["enum"]![0]!.GetValue<string>());
        Assert.True(responses["503"]!["headers"]!["Retry-After"]!["required"]!.GetValue<bool>());
        Assert.NotNull(responses["409"]!["headers"]!["Retry-After"]);
        Assert.NotNull(responses["200"]!["headers"]![MappingEndpointRoutes.ReplayedHeader]);

        // Each refusal lists exactly its outcomes, and together they are every outcome once.
        var listed = new List<string>();
        foreach (var (status, response) in responses.Where(response => response.Key != "200"))
        {
            var schema = response!["content"]!["application/json"]!["schema"]!;
            Assert.Equal("#/components/schemas/MappingInvocationErrorV1", schema["allOf"]![0]!["$ref"]!.GetValue<string>());
            foreach (var outcome in schema["properties"]!["outcome"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()))
            {
                Assert.Equal(int.Parse(status, System.Globalization.CultureInfo.InvariantCulture),
                    MappingEndpointRoutes.Status(Enum.Parse<MappingInvocationOutcomeV1>(outcome, ignoreCase: true)));
                listed.Add(outcome);
            }
        }
        Assert.Equal(Enum.GetValues<MappingInvocationOutcomeV1>().Select(value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString())).Order(),
            listed.Order());
        Assert.Equal(["204"], document["paths"]![MappingEndpointRoutes.LivePath]!["get"]!["responses"]!.AsObject().Select(response => response.Key));
        Assert.Equal(["204", "503"], document["paths"]![MappingEndpointRoutes.ReadyPath]!["get"]!["responses"]!.AsObject().Select(response => response.Key));
    }

    [Fact]
    public void The_completed_response_and_the_refusal_are_exactly_their_wire_types()
    {
        var outputs = RegressionOutputSet.Capture([
            RegressionOutput.Capture(RegressionOutputKey.Primary, "{\"code\":\"A1\"}"u8.ToArray()),
            RegressionOutput.Capture(RegressionOutputKey.Named("rates"), "[1,2]"u8.ToArray())]);
        var provenance = new MachineInvocationProvenance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Sha256Digest.FromBytes([1]),
            Sha256Digest.FromBytes([2]), Guid.NewGuid(), "sha256:" + new string('a', 64));
        var encoded = MachineInvocationService.Encode(Guid.NewGuid(), provenance, "2026-10-22T12:00:00.0000000Z", outputs);
        var response = JsonSerializer.Deserialize<MappingInvocationResponseV1>(encoded, Strict(MappingEndpointContract.Json))!;
        Assert.Equal(MappingEndpointContract.ResponseSchema, response.Schema);
        Assert.Null(response.Outputs[0].Name);
        Assert.Equal(encoded, JsonSerializer.SerializeToUtf8Bytes(response, MappingEndpointContract.Json));

        Assert.Equal("{\"outcome\":\"unauthorized\"}",
            JsonSerializer.Serialize(new MappingInvocationErrorV1(null, MappingInvocationOutcomeV1.Unauthorized), MappingEndpointContract.Json));
        var id = Guid.NewGuid();
        Assert.Equal("{\"requestId\":\"" + id.ToString("D") + "\",\"outcome\":\"inProgress\"}",
            JsonSerializer.Serialize(new MappingInvocationErrorV1(id, MappingInvocationOutcomeV1.InProgress), MappingEndpointContract.Json));
    }

    [Fact]
    public void The_request_type_is_the_body_the_strict_parser_admits()
    {
        var request = new MappingInvocationRequestV1([new([1, 2, 3]), new([4]) { Name = "rates" }])
        {
            Parameters = [new("region", MappingInvocationParameterKindV1.String, "west"), new("limit", MappingInvocationParameterKindV1.Integer, "2")]
        };
        var parsed = MappingEndpointRoutes.Parse(JsonSerializer.SerializeToUtf8Bytes(request, MappingEndpointContract.Json));
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Primary.Bytes.ToArray());
        Assert.Equal("rates", Assert.Single(parsed.NamedInputs).Name);
        Assert.Equal([("limit", PreviewParameterKind.Integer, "2"), ("region", PreviewParameterKind.String, "west")],
            parsed.Parameters.Select(parameter => (parameter.Name, parameter.Kind, parameter.Value)));
        // The wire kinds are the parser's kinds, by the same names.
        Assert.Equal(Enum.GetNames<PreviewParameterKind>(), Enum.GetNames<MappingInvocationParameterKindV1>());

        var strict = Strict(MappingEndpointContract.Json);
        Assert.NotNull(JsonSerializer.Deserialize<MappingInvocationRequestV1>("""{"inputs":[{"content":"e30="}]}""", strict));
        foreach (var body in new[]
        {
            """{"inputs":[{"content":"e30="}],"extra":1}""", """{"inputs":[{"content":"e30=","extra":1}]}""",
            """{"inputs":[{"content":"e30="}],"parameters":[{"name":"a","kind":"string","value":"x","extra":1}]}""",
            """{"inputs":[{"content":"e30="}],"parameters":[{"name":"a","kind":"date","value":"x"}]}"""
        })
        {
            // A member or kind outside the type is refused by both.
            Assert.ThrowsAny<Exception>(() => MappingEndpointRoutes.Parse(System.Text.Encoding.UTF8.GetBytes(body)));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MappingInvocationRequestV1>(body, strict));
        }
    }
}

/// <summary>The published composition's live routes and the gate in front of them.</summary>
[SupportedOSPlatform("linux")]
public sealed class MappingEndpointLiveRouteTests
{
    [MappingEndpointLinuxFact]
    public async Task The_live_host_serves_exactly_the_specified_routes_and_its_gate_answers_everything_else()
    {
        var root = Directory.CreateTempSubdirectory("weft-endpoint-");
        try
        {
            using var files = new MappingEndpointLiveTests.PrivateFiles(root.FullName);
            var port = MappingEndpointLiveTests.FreePort();
            var settings = MappingEndpointSettings.Read(files.Environment(MappingEndpointHostTests.Settings(port), MappingEndpointHostTests.Connections()));
            var resources = new MappingEndpointResources(settings, new EntraMachineTokenValidator(new(settings.TenantId, settings.Audience)),
                TimeProvider.System);
            try
            {
                using var host = MappingEndpointHost.Build(resources);
                await host.StartAsync();
                try
                {
                    Serves(Committed("weft-endpoint-v1.json"), Endpoints(host.Services), complete: true);
                    host.Services.GetRequiredService<MappingEndpointRoutes>().MarkReady();
                    using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(30) };
                    foreach (var (method, path, status, allow) in new (HttpMethod, string, HttpStatusCode, string?)[]
                    {
                        (HttpMethod.Get, MappingEndpointRoutes.LivePath, HttpStatusCode.NoContent, null),
                        (HttpMethod.Get, MappingEndpointRoutes.ReadyPath, HttpStatusCode.NoContent, null),
                        (HttpMethod.Post, MappingEndpointRoutes.InvocationsPath, HttpStatusCode.Unauthorized, null),
                        (HttpMethod.Get, MappingEndpointRoutes.InvocationsPath, HttpStatusCode.MethodNotAllowed, "POST"),
                        (HttpMethod.Head, MappingEndpointRoutes.LivePath, HttpStatusCode.MethodNotAllowed, "GET"),
                        (HttpMethod.Post, MappingEndpointRoutes.ReadyPath, HttpStatusCode.MethodNotAllowed, "GET"),
                        // Routing alone would match these case, trailing-slash and query variants.
                        (HttpMethod.Get, "/V1/health/live", HttpStatusCode.NotFound, null),
                        (HttpMethod.Get, MappingEndpointRoutes.LivePath + "/", HttpStatusCode.NotFound, null),
                        (HttpMethod.Get, MappingEndpointRoutes.LivePath + "?probe=1", HttpStatusCode.NotFound, null),
                        (HttpMethod.Post, "/V1/Invocations", HttpStatusCode.NotFound, null),
                        (HttpMethod.Get, "/", HttpStatusCode.NotFound, null),
                        (HttpMethod.Get, "/openapi/v1.json", HttpStatusCode.NotFound, null)
                    })
                    {
                        using var response = await client.SendAsync(new HttpRequestMessage(method, path));
                        Assert.Equal(status, response.StatusCode);
                        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
                        Assert.Equal(allow, response.Content.Headers.Allow.Count == 0 ? null : string.Join(", ", response.Content.Headers.Allow));
                        var body = await response.Content.ReadAsStringAsync();
                        Assert.Equal(status == HttpStatusCode.Unauthorized ? "{\"outcome\":\"unauthorized\"}" : "", body);
                    }
                }
                finally { await host.StopAsync(); }
            }
            finally { await resources.DisposeAsync(); }
        }
        finally { root.Delete(recursive: true); }
    }
}
