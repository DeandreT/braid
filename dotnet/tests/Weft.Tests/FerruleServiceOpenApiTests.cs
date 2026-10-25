using System.Net;
using System.Text.Json;
using Weft.FerruleService;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;
using static Weft.Tests.InspectionTransportTests;
using static Weft.Tests.OpenApiContractSupport;

namespace Weft.Tests;

/// <summary>
/// <c>contracts/ferrule-service-v1.json</c> against the service's document-only host, the routes each listener
/// configuration serves and the capability document the service writes.
/// </summary>
public sealed class FerruleServiceOpenApiTests
{
    private const string File = "ferrule-service-v1.json";

    [Fact]
    public async Task The_committed_specification_is_the_document_the_service_generates()
    {
        // Only build-time generation selects the document-only host.
        Assert.False(FerruleServiceOpenApi.Generating);
        using var host = FerruleServiceOpenApi.BuildDocumentHost();
        var generated = await GenerateAsync(host);
        Same(Committed(File), generated, File);
        Serves(generated, Endpoints(host.Services), complete: true);
    }

    [Fact]
    public async Task A_pinned_service_with_a_probe_listener_serves_exactly_the_specified_routes()
    {
        await using var test = await FerruleServiceTransportTests.ServiceCase.StartAsync(health: true, compile: true, execute: true);
        Assert.Equal(FerruleServiceOpenApi.Served, test.Server.Routes);
        Serves(Committed(File), test.Server.Endpoints, complete: true);
    }

    [Fact]
    public async Task Sandbox_and_inspection_listeners_serve_only_specified_routes()
    {
        var served = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
        {
            [FerruleServiceProtocol.InspectionProfile] = new ScriptedRunner(),
            [FerruleServiceProtocol.PreviewProfile] = new ScriptedRunner()
        };
        await using (var sandbox = new WorkerTransportServer(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0)), served))
        {
            await sandbox.StartAsync(CancellationToken.None);
            Assert.Equal(WorkerTransportRoutes.Service | WorkerTransportRoutes.Health, sandbox.Routes);
            Serves(Committed(File), sandbox.Endpoints, complete: false);
            await sandbox.StopAsync(CancellationToken.None);
        }

        using var certificate = TransportCase.Certificate("CN=Inspection worker");
        using var client = TransportCase.Certificate("CN=Inspection client");
        await using var inspection = new WorkerTransportServer(
            new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0), certificate, [TransportCase.Pin(client)])
            {
                HealthListen = new IPEndPoint(IPAddress.Loopback, 0)
            },
            new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { [FerruleServiceProtocol.InspectionProfile] = new ScriptedRunner() });
        await inspection.StartAsync(CancellationToken.None);
        Assert.Equal(WorkerTransportRoutes.Health | WorkerTransportRoutes.Inspection, inspection.Routes);
        Serves(Committed(File), inspection.Endpoints, complete: false);
        await inspection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_specification_describes_mutual_tls_frames_limits_and_retryable_refusals()
    {
        var document = Committed(File);
        Assert.StartsWith("3.1.", document["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("mutualTLS", document["components"]!["securitySchemes"]![FerruleServiceOpenApi.ClientCertificate]!["type"]!.GetValue<string>());
        Assert.NotNull(document["security"]![0]![FerruleServiceOpenApi.ClientCertificate]);
        var run = document["paths"]![FerruleServiceProtocol.OperationsPath + "{profile}/{operation}"]!["post"]!;
        Assert.Equal("binary", run["requestBody"]!["content"]![InspectionTransport.MediaType]!["schema"]!["format"]!.GetValue<string>());
        Assert.Equal("binary", run["responses"]!["200"]!["content"]![InspectionTransport.MediaType]!["schema"]!["format"]!.GetValue<string>());
        Assert.Equal(Enum.GetValues<WorkerProcessFailure>().Select(InspectionTransport.Encode),
            run["responses"]!["200"]!["headers"]![InspectionTransport.ResultHeader]!["schema"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.True(run["responses"]!["503"]!["headers"]!["Retry-After"]!["required"]!.GetValue<bool>());
        Assert.Equal(FerruleServiceOpenApi.Profiles, run["parameters"]![0]!["schema"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
        // Each profile's limits come from the table the service and its clients share.
        var description = run["description"]!.GetValue<string>();
        foreach (var name in FerruleServiceOpenApi.Profiles)
        {
            var profile = FerruleServiceProtocol.Profile(name)!;
            Assert.Contains($"| `{name}` | {profile.MaximumRequestBytes} | {profile.MaximumOutputBytes} | {(long)profile.MaximumWallTimeout.TotalSeconds} | " +
                $"{profile.CpuSeconds} | {profile.MemoryBytes} |", description, StringComparison.Ordinal);
        }
        var cancel = document["paths"]![FerruleServiceProtocol.OperationsPath + "{profile}/{operation}" + InspectionTransport.CancelSuffix]!["post"]!;
        Assert.Equal(["204", "400"], cancel["responses"]!.AsObject().Select(response => response.Key));
        Assert.True(document["paths"]![FerruleServiceProtocol.ReadyPath]!["get"]!["responses"]!["503"]!["headers"]!["Retry-After"]!["required"]!.GetValue<bool>());
        // Probes are also served on the plain probe listener.
        Assert.Equal(2, document["paths"]![FerruleServiceProtocol.ReadinessPath]!["get"]!["security"]!.AsArray().Count);
        foreach (var path in document["paths"]!.AsObject())
            foreach (var operation in path.Value!.AsObject())
                foreach (var response in operation.Value!["responses"]!.AsObject())
                {
                    var replica = response.Value!["headers"]![FerruleServiceProtocol.ReplicaHeader]!;
                    Assert.True(replica["required"]!.GetValue<bool>());
                    Assert.Equal("^[0-9a-f]{32}$", replica["schema"]!["pattern"]!.GetValue<string>());
                }
    }

    [Fact]
    public async Task The_capability_document_is_exactly_its_wire_type()
    {
        foreach (var enabled in new[] { false, true })
        {
            await using var test = await FerruleServiceTransportTests.ServiceCase.StartAsync(compile: enabled, execute: enabled);
            using var http = test.Client();
            var body = await http.GetByteArrayAsync(FerruleServiceProtocol.CapabilitiesPath);
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var capabilities = JsonSerializer.Deserialize<FerruleServiceCapabilitiesV1>(body, Strict(web))!;
            Assert.Equal(enabled, capabilities.Profiles.Any(profile => profile.Toolchain is not null));
            Assert.Equal(enabled, capabilities.Profiles.Any(profile => profile.Runtime is not null));
            Assert.Equal(body, JsonSerializer.SerializeToUtf8Bytes(capabilities, web));
        }
    }
}
