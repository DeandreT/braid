using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.MachineEndpoint;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTestSupport;
using static Braid.Tests.InspectionTransportTests;

namespace Braid.Tests;

/// <summary>
/// The machine endpoint's own configuration and runtime composition selecting the packaged Ferrule service, whose
/// startup probes the runtime sandbox before readiness and whose fresh child executes the real compiled bytes.
/// </summary>
[Collection(FerruleServiceProcessCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class FerruleCompiledEndpointTests
{
    [ServiceCompileFact]
    public async Task The_endpoint_runs_real_compiled_bytes_through_the_packaged_service_and_its_sandboxed_child()
    {
        var root = TemporaryRoot();
        try
        {
            var compileRoot = Private(root, "compile");
            await using var compilerRunner = new ProcessCompileRunner(CompileOptions(compileRoot));
            var (compiler, intent, native) = FerruleServiceCompileTests.Compiler(compilerRunner, "valid-basic");
            var capability = await compiler.InspectAsync(intent);
            var bytes = await compiler.BuildAsync(intent, new(native, native.Length));
            var artifact = BuildArtifactReference.Create(intent, capability, bytes.Digest, bytes.Length);

            using var serverCertificate = TransportCase.Certificate("CN=Ferrule service");
            using var clientCertificate = TransportCase.Certificate("CN=Mapping endpoint");
            var serviceRoot = Private(root, "service");
            await using var service = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(serverCertificate),
                [TransportCase.Pin(clientCertificate)], serviceRoot, extra: new Dictionary<string, string>
                {
                    ["BRAID_FERRULE_SERVICE_EXECUTION_ENABLED"] = "true",
                    ["BRAID_FERRULE_SERVICE_RUNTIME_ASSEMBLY"] = FerruleCompiledServiceTests.Assembly,
                    ["BRAID_FERRULE_SERVICE_RUNTIME_LAUNCHER"] = Launcher!,
                    ["BRAID_FERRULE_SERVICE_RUNTIME_DOTNET_ROOT"] = Toolchain
                });
            var runtimeIdentity = await RuntimeIdentityAsync(service, serverCertificate, clientCertificate);

            // The endpoint's admitted settings select the service with protected client deliveries.
            var credentials = Private(root, "endpoint");
            using var files = new MappingEndpointLiveTests.PrivateFiles(credentials);
            var certificateFile = Path.Combine(credentials, "client.crt");
            var keyFile = Path.Combine(credentials, "client.key");
            File.WriteAllText(certificateFile, clientCertificate.ExportCertificatePem());
            using (var key = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(clientCertificate)!)
                File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
            File.SetUnixFileMode(certificateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var settings = MappingEndpointSettings.Read(files.Environment(new Dictionary<string, string>
            {
                [MappingEndpointSettings.Listen] = "127.0.0.1:" + MappingEndpointLiveTests.FreePort().ToString(CultureInfo.InvariantCulture),
                [MappingEndpointSettings.Transport] = "platformIngress",
                ["BRAID_ENDPOINT_ORGANIZATION_ID"] = Scope.OrganizationId.ToString("D"),
                ["BRAID_ENDPOINT_PROJECT_ID"] = Scope.ProjectId.ToString("D"),
                ["BRAID_ENDPOINT_MAPPING_ID"] = Scope.MappingId.ToString("D"),
                ["BRAID_ENDPOINT_ENVIRONMENT_ID"] = intent.Environment.EnvironmentId.ToString("D"),
                ["BRAID_ENDPOINT_ENVIRONMENT_VERSION"] = "1",
                ["BRAID_ENDPOINT_TARGET_ID"] = Guid.NewGuid().ToString("D"),
                [MappingEndpointSettings.TenantSetting] = EntraProtocolFixturePolicy.Tenant.ToString("D"),
                [MappingEndpointSettings.AudienceSetting] = EntraProtocolFixturePolicy.Client.ToString("D"),
                [MappingEndpointSettings.RuntimeImageSetting] = runtimeIdentity,
                [MappingEndpointSettings.CompilerImageSetting] = PackagedFerruleService.Identity,
                [FerruleRunnerSelection.Setting] = "service",
                [FerruleRunnerSelection.CompileToolchainSetting] = compilerRunner.ToolchainIdentity,
                [FerruleServiceEndpoint.Url] = "https://127.0.0.1:" + service.Port.ToString(CultureInfo.InvariantCulture),
                [FerruleServiceEndpoint.CertificatePin] = TransportCase.Pin(serverCertificate),
                [FerruleServiceEndpoint.ClientCertificateFile] = certificateFile,
                [FerruleServiceEndpoint.ClientPrivateKeyFile] = keyFile
            }, MappingEndpointSettings.Logins.ToDictionary(login => login.Setting,
                login => "Host=127.0.0.1;Database=braid;SSL Mode=Disable;Username=" + login.Role + ";Password=synthetic")));
            Assert.Equal(FerruleRunnerKind.Service, settings.Runner.Kind);
            Assert.Equal(compiler.Backend.EngineBuild, settings.CompilerImage);

            // Composed exactly as the endpoint's resources compose it.
            var owned = settings.Runner.CreateRunner(MappingEndpointRoutes.Settlement(MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline));
            try
            {
                var runtime = new FerruleCompiledBuildRuntime(owned.Runner,
                    new("ferrule", FerruleInspectionBackend.EngineRevision, settings.CompilerImage, FerrulePreviewBackend.AdapterBuild));
                Assert.Equal(runtimeIdentity, runtime.RuntimeImage);
                var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-22T00:00:00Z", PreviewLimits.Default,
                    new(null, "{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}"u8), [], []);
                var expected = "{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n"u8.ToArray();
                var result = await runtime.ExecuteAsync(artifact, bytes, replay);
                Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
                Assert.Equal(expected, Assert.Single(result.Outputs!.Outputs).CopyBytes());

                // An invalid frame fails in its own child; the next execution still completes and the runner stays open.
                Assert.Equal(WorkerProcessFailure.WorkerFailed, (await owned.Runner.RunAsync(new byte[] { 0 }, default)).Failure);
                Assert.Equal(CompiledBuildOutcome.Completed, (await runtime.ExecuteAsync(artifact, bytes, replay)).Outcome);
                Assert.True(owned.Available);
            }
            finally { await owned.DisposeAsync(); }
            Assert.False(owned.Available);
            Assert.Equal(0, await service.StopAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(serviceRoot));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Private(string root, string name) =>
        Directory.CreateDirectory(Path.Combine(root, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;

    /// <summary>The execution identity the service advertises in its capabilities, read over its pinned mutual TLS.</summary>
    private static async Task<string> RuntimeIdentityAsync(PackagedFerruleService service,
        System.Security.Cryptography.X509Certificates.X509Certificate2 server, System.Security.Cryptography.X509Certificates.X509Certificate2 client)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new()
            {
                ClientCertificates = [client],
                RemoteCertificateValidationCallback = (_, presented, _, _) => InspectionTransport.HasDigest(presented, TransportCase.Pin(server))
            }
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://127.0.0.1:" + service.Port.ToString(CultureInfo.InvariantCulture)),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var capabilities = JsonDocument.Parse(await http.GetStringAsync(FerruleServiceProtocol.CapabilitiesPath));
        Assert.DoesNotContain(capabilities.RootElement.GetProperty("reserved").EnumerateArray(),
            value => value.GetString() == FerruleServiceProtocol.CompiledProfile);
        return capabilities.RootElement.GetProperty("profiles").EnumerateArray()
            .Single(profile => profile.GetProperty("name").GetString() == FerruleServiceProtocol.CompiledProfile)
            .GetProperty("runtime").GetProperty("identity").GetString()!;
    }
}
