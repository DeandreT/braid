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
        MachineInvocationService service, Func<bool> available, byte[]? xmlExpected = null)
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
            byte[]? original = null;
            foreach (var replay in new[] { false, true })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath);
                request.Headers.Authorization = new("Bearer", bearer);
                request.Headers.Add(MappingEndpointRoutes.RequestIdHeader, id.ToString("D"));
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = MappingEndpointContract.DocumentRequestSchema,
                    inputs = new object[] { xmlExpected is null ? new { mediaType = "application/json", content = Convert.ToBase64String(Shipment945Fixture.Input) } : XmlInputWire(XmlInput) },
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
                Assert.Equal(xmlExpected is null ? "application/edi-x12" : XmlDocumentPolicy.MediaType, output.GetProperty("mediaType").GetString());
                Assert.Equal(xmlExpected ?? Shipment945Fixture.NativeExpected, output.GetProperty("content").GetBytesFromBase64());
                if (replay) Assert.Equal(original, bytes);
                original = bytes;
            }
            if (xmlExpected is not null)
            {
                foreach (var (input, expected) in new[] { ("<Order>", "invalidInput"), ("<Order><code>LONGER</code><quantity>2</quantity></Order>", "resourceLimit") })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath);
                    request.Headers.Authorization = new("Bearer", bearer);
                    request.Headers.Add(MappingEndpointRoutes.RequestIdHeader, Guid.NewGuid().ToString("D"));
                    request.Content = new StringContent(JsonSerializer.Serialize(new { schema = MappingEndpointContract.DocumentRequestSchema,
                        inputs = new[] { XmlInputWire(System.Text.Encoding.UTF8.GetBytes(input)) } }), System.Text.Encoding.UTF8, "application/json");
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
