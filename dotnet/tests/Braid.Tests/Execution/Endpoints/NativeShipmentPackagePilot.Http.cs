using System.Net;
using Braid.Application;
using System.Text.Json;
using Braid.Deployment;
using Braid.Identity;
using Braid.MachineEndpoint;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Braid.Tests;

public sealed partial class NativeShipmentPackagePilot
{
    private static async Task InvokeHttp(ReleaseTargetReference target, Guid release, MachinePrincipal caller,
        MachineInvocationService service, Func<bool> available, byte[]? xmlExpected = null, bool contrl = false, BuildPersistenceTests.ByteCase? bytesCase = null, ReleasedDocumentSample? sample = null)
    {
        using var crypto = new EntraProtocolCrypto();
        await using var issuer = new EntraProtocolHttpsIssuer(crypto);
        await issuer.StartAsync();
        await using var validator = new EntraMachineTokenValidator(new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client),
            new EntraHttpsFixtureTransport(issuer.Port, crypto.Root));
        var routes = new MappingEndpointRoutes(new(target, 1, validator, service, TimeProvider.System,
            MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline, available));
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).ConfigureWebHost(web => web
            .UseKestrel(server => server.Listen(IPAddress.Loopback, 0))
            .ConfigureServices(services => services.AddRouting().AddSingleton(routes))
            .Configure(MappingEndpointHost.Compose)).Build();
        await host.StartAsync();
        try
        {
            routes.MarkReady();
            var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new(address), Timeout = MappingEndpointRoutes.Deadline };
            var id = Guid.NewGuid();
            var bearer = crypto.SignClaims(MachineTokenValidationTests.Claims(caller.ObjectId).ToJsonString());
            object InputWire(byte[] input) => sample?.Dialect is { } dialect ? new { mediaType = sample.Media, csvDialect = JsonSerializer.Deserialize<JsonElement>(CsvDialectJson.Encode(dialect)), content = Convert.ToBase64String(input) } : xmlExpected is not null ? XmlInputWire(input) : new { mediaType = sample?.Media ?? bytesCase?.Family.MediaType ?? (contrl ? "application/edifact" : "application/json"), content = Convert.ToBase64String(input) };
            byte[]? original = null;
            foreach (var replay in new[] { false, true })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath);
                request.Headers.Authorization = new("Bearer", bearer);
                request.Headers.Add(MappingEndpointRoutes.RequestIdHeader, id.ToString("D"));
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = MappingEndpointContract.DocumentRequestSchema,
                    inputs = new[] { InputWire(sample is not null ? sample.Input : bytesCase is not null ? bytesCase.Input.CopyBytes() : contrl ? System.Text.Encoding.ASCII.GetBytes(DocumentPreviewTestData.Contrl) : xmlExpected is null ? Shipment945Fixture.Input : XmlInput) },
                    parameters = Array.Empty<object>()
                }));
                request.Content.Headers.ContentType = new("application/json");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(replay ? "true" : "false", Assert.Single(response.Headers.GetValues(MappingEndpointRoutes.ReplayedHeader)));
                var bytes = await response.Content.ReadAsByteArrayAsync();
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                Assert.Equal(MappingEndpointContract.DocumentResponseSchema, root.GetProperty("schema").GetString());
                Assert.Equal(id, root.GetProperty("requestId").GetGuid());
                Assert.Equal(release, root.GetProperty("releaseId").GetGuid());
                var output = Assert.Single(root.GetProperty("outputs").EnumerateArray());
                Assert.Equal(sample is not null ? sample.Media : bytesCase is not null ? bytesCase.Family.MediaType : contrl ? "application/edifact" : xmlExpected is null ? "application/edi-x12" : XmlDocumentPolicy.MediaType, output.GetProperty("mediaType").GetString());
                Assert.Equal(sample?.Expected ?? bytesCase?.Expected ?? xmlExpected ?? (contrl ? DocumentPreviewTestData.ContrlExpected : Shipment945Fixture.NativeExpected), output.GetProperty("content").GetBytesFromBase64());
                if (replay) Assert.Equal(original, bytes);
                original = bytes;
            }
            if (sample is not null)
            {
                foreach (var (input, outcome) in new[] { (sample.Malformed, "invalidInput"), (sample.Longer, "resourceLimit") })
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath);
                    req.Headers.Authorization = new("Bearer", bearer);
                    req.Headers.Add(MappingEndpointRoutes.RequestIdHeader, Guid.NewGuid().ToString("D"));
                    req.Content = new StringContent(JsonSerializer.Serialize(new { schema = MappingEndpointContract.DocumentRequestSchema, inputs = new[] { InputWire(input) } }), System.Text.Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(req);
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                    using var error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                    Assert.Equal(outcome, error.RootElement.GetProperty("outcome").GetString());
                    Assert.False(error.RootElement.TryGetProperty("outputs", out _));
                }
            }
            if (bytesCase is not null)
            {
                async Task<HttpResponseMessage> Post(byte[] bytes, string mediaType, string? token)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath);
                    if (token is not null) req.Headers.Authorization = new("Bearer", token);
                    req.Headers.Add(MappingEndpointRoutes.RequestIdHeader, Guid.NewGuid().ToString("D"));
                    req.Content = new StringContent(JsonSerializer.Serialize(new { schema = MappingEndpointContract.DocumentRequestSchema, inputs = new[] { new { mediaType, content = Convert.ToBase64String(bytes) } } }), System.Text.Encoding.UTF8, "application/json");
                    return await client.SendAsync(req);
                }
                byte[] longer = bytesCase.Family.Key switch { "xlsx" => Convert.FromBase64String(JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FerruleFixtures","xlsx-portable-longer.json")))!), "protobuf" => [10,6,104,101,108,108,111,33], "flextext" => "hello!"u8.ToArray(), _ => DocumentPreviewTestData.XbrlInstance("55") };
                foreach (var (body, media, outcome) in new[] { (longer, bytesCase.Family.MediaType, "resourceLimit"), (bytesCase.Input.CopyBytes(), "application/json", "invalidInput") })
                {
                    using var refused = await Post(body, media, bearer);
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                    using var error = JsonDocument.Parse(await refused.Content.ReadAsByteArrayAsync());
                    Assert.Equal(outcome, error.RootElement.GetProperty("outcome").GetString());
                    Assert.False(error.RootElement.TryGetProperty("outputs", out _));
                }
                // Keep the admitted media type: a mismatched declaration never enters the reader.
                {
                    byte[] invalid = bytesCase.Family.Key switch
                    {
                        "flextext" => [0xff], // Store requires strict UTF-8
                        "protobuf" => [0x0a, 0x02, 0x41], // one byte missing from the declared field
                        "xbrl" => "<xbrl>"u8.ToArray(), // incomplete instance, but valid UTF-8
                        "xlsx" => [..bytesCase.Input.CopyBytes(), 0], // trailing data after the canonical ZIP
                        _ => throw new NotSupportedException()
                    };
                    using var malformed = await Post(invalid, bytesCase.Family.MediaType, bearer);
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
                    using var failure = JsonDocument.Parse(await malformed.Content.ReadAsByteArrayAsync());
                    Assert.Equal("invalidInput", failure.RootElement.GetProperty("outcome").GetString());
                    Assert.False(failure.RootElement.TryGetProperty("outputs", out _));
                }
                var other = crypto.SignClaims(MachineTokenValidationTests.Claims(Guid.NewGuid()).ToJsonString());
                using var ungranted = await Post(bytesCase.Input.CopyBytes(), bytesCase.Family.MediaType, other);
                Assert.Equal(HttpStatusCode.Forbidden, ungranted.StatusCode);
                using var denied = await Post(bytesCase.Input.CopyBytes(), bytesCase.Family.MediaType, null);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            if (xmlExpected is not null || contrl)
            {
                foreach (var (input, expected) in contrl ? new[] {
                    (DocumentPreviewTestData.Contrl.Replace("UNT+3+M1", "UNT+3+M2", StringComparison.Ordinal), "invalidInput"),
                    (DocumentPreviewTestData.Contrl.Replace("PREVIOUS", "PREVIOUS2", StringComparison.Ordinal), "resourceLimit") }
                    : [("<Order>", "invalidInput"), ("<Order><code>LONGER</code><quantity>2</quantity></Order>", "resourceLimit")])
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath);
                    request.Headers.Authorization = new("Bearer", bearer);
                    request.Headers.Add(MappingEndpointRoutes.RequestIdHeader, Guid.NewGuid().ToString("D"));
                    request.Content = new StringContent(JsonSerializer.Serialize(new { schema = MappingEndpointContract.DocumentRequestSchema,
                        inputs = new[] { InputWire(System.Text.Encoding.UTF8.GetBytes(input)) } }), System.Text.Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                    using var error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                    Assert.Equal(expected, error.RootElement.GetProperty("outcome").GetString());
                    Assert.False(error.RootElement.TryGetProperty("outputs", out _));
                }
                using var denied = await client.PostAsync(MappingEndpointRoutes.InvocationsPath, new StringContent("{}"));
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        finally { await host.StopAsync(); }
    }

    private static object XmlInputWire(byte[] bytes) => new { mediaType = XmlDocumentPolicy.MediaType,
        content = Convert.ToBase64String(bytes), xmlMetadata = new { schema = XmlDocumentPolicy.MetadataSchema,
            safetyProfile = XmlDocumentPolicy.SafetyProfile, schemaHints = (object?)null } };
}
