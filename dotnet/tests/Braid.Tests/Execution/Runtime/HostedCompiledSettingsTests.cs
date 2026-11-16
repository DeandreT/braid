using System.Security.Cryptography.X509Certificates;
using Braid.FerruleService;
using Braid.MachineEndpoint;
using Braid.MappingBackends.AzureSessions;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTransportTests;

namespace Braid.Tests;

public sealed class HostedCompiledSettingsTests
{
    private static readonly string Native = "sha256:" + new string('1', 64);
    private static readonly string Toolchain = "sha256:" + new string('2', 64);
    private static readonly string Runtime = "sha256:" + new string('3', 64);
    private const string PoolEndpoint = "https://braid-ferrule.example-env.westus2.azurecontainerapps.io";

    [Fact]
    public void Execution_is_opt_in_independently_from_compilation()
    {
        var values = new Dictionary<string, string>
        {
            [FerruleServiceSettings.Listen] = "127.0.0.1:8080",
            [FerruleServiceSettings.Authentication] = "platformSandbox"
        };
        Assert.Null(FerruleServiceSettings.Read(values.GetValueOrDefault).Runtime);
        values[FerruleServiceSettings.ExecutionEnabled] = "true";
        var settings = FerruleServiceSettings.Read(values.GetValueOrDefault);
        Assert.NotNull(settings.Runtime); Assert.Null(settings.Compile);
        values[FerruleServiceSettings.ExecutionEnabled] = "false";
        values[FerruleServiceSettings.RuntimeAssemblySetting] = "/runtime/custom.dll";
        Assert.Throws<InvalidOperationException>(() => FerruleServiceSettings.Read(values.GetValueOrDefault));
    }

    [Fact]
    public void The_endpoint_keeps_runtime_and_original_compiler_identity_separate()
    {
        var values = Selection("service");
        var selection = MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native);
        Assert.Equal(Runtime, selection.RuntimeIdentity);
        Assert.Equal(CompileWorkerIdentity.Combine(Native, Toolchain), selection.CompilerIdentity);
        values.Remove(FerruleRunnerSelection.CompileToolchainSetting);
        Assert.Throws<InvalidOperationException>(() => MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native));
        values[MappingRuntimeSelection.CompilerBuild] = Runtime;
        Assert.Equal(Runtime, MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native).CompilerIdentity);
        values.Remove(MappingRuntimeSelection.CompilerBuild);
        values[FerruleRunnerSelection.CompileToolchainSetting] = Toolchain;
        values[FerruleRunnerSelection.Setting] = "docker";
        Assert.Throws<InvalidOperationException>(() => MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native));
    }

    /// <summary>Each runner's own settings beside each other selection, with a value that runner would admit.</summary>
    public static TheoryData<string, string, string> MixedSettings()
    {
        var service = new[]
        {
            (FerruleServiceEndpoint.Url, "https://127.0.0.1:8443"), (FerruleServiceEndpoint.CertificatePin, new string('a', 64)),
            (FerruleServiceEndpoint.ClientCertificateFile, "/private/certificate"), (FerruleServiceEndpoint.ClientPrivateKeyFile, "/private/key")
        };
        var sessions = new[]
        {
            (AzureSessionsRunnerSettings.PoolEndpoint, PoolEndpoint), (AzureSessionsRunnerSettings.ClientId, "5c5b1a3e-0d3c-4f69-9d8e-0a8f0c3c1a2b")
        };
        var data = new TheoryData<string, string, string>();
        foreach (var (setting, value) in service) { data.Add("docker", setting, value); data.Add("azureContainerAppsSessions", setting, value); }
        foreach (var (setting, value) in sessions) { data.Add("docker", setting, value); data.Add("service", setting, value); }
        data.Add("service", MappingEndpointSettings.DockerSetting, "/usr/bin/docker");
        data.Add("azureContainerAppsSessions", MappingEndpointSettings.DockerSetting, "/usr/bin/docker");
        return data;
    }

    [Theory]
    [MemberData(nameof(MixedSettings))]
    public void Another_runners_setting_is_refused_and_an_empty_one_is_unset(string runner, string setting, string value)
    {
        var values = Selection(runner);
        var kind = MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native).Kind;
        values[setting] = value;
        Assert.Throws<InvalidOperationException>(() => MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native));
        values[setting] = "";
        Assert.Equal(kind, MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native).Kind);
    }

    [Fact]
    public void Empty_runtime_settings_fall_back_like_every_other_endpoint_setting()
    {
        var values = new Dictionary<string, string> { [FerruleRunnerSelection.Setting] = "", [MappingEndpointSettings.DockerSetting] = "" };
        var selection = MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native);
        Assert.Equal(FerruleRunnerKind.Docker, selection.Kind);
        Assert.Equal("/usr/bin/docker", selection.Docker);
        values[MappingEndpointSettings.DockerSetting] = "/opt/docker/bin/docker";
        Assert.Equal("/opt/docker/bin/docker", MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native).Docker);
        values[MappingEndpointSettings.DockerSetting] = "docker";
        Assert.Throws<InvalidOperationException>(() => MappingRuntimeSelection.Read(values.GetValueOrDefault, Runtime, Native));

        // An empty client delivery is missing, not a path; an empty compiler pin leaves the toolchain pin required.
        foreach (var setting in new[] { FerruleServiceEndpoint.ClientCertificateFile, FerruleServiceEndpoint.ClientPrivateKeyFile, FerruleRunnerSelection.CompileToolchainSetting })
        {
            var service = Selection("service");
            service[setting] = "";
            Assert.Throws<InvalidOperationException>(() => MappingRuntimeSelection.Read(service.GetValueOrDefault, Runtime, Native));
        }
        var original = Selection("service");
        original[MappingRuntimeSelection.CompilerBuild] = "";
        Assert.Equal(CompileWorkerIdentity.Combine(Native, Toolchain), MappingRuntimeSelection.Read(original.GetValueOrDefault, Runtime, Native).CompilerIdentity);
    }

    [Fact]
    public async Task The_docker_selection_runs_only_the_compiled_profile_and_reports_its_closure()
    {
        var owned = MappingRuntimeSelection.Read(Selection("docker").GetValueOrDefault, Runtime, Native).CreateRunner(MappingEndpointRoutes.Settlement(MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline));
        var docker = Assert.IsType<DockerInspectionWorkerRunner>(owned.Runner);
        Assert.Equal(DockerWorkerProfile.Compiled, docker.Profile);
        Assert.Equal(Runtime, docker.BuildIdentity);
        Assert.Null(owned.Borrowed);
        Assert.True(owned.Available);
        await owned.DisposeAsync();
        Assert.False(owned.Available);
    }

    [MappingEndpointLinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task The_service_selection_loads_its_protected_client_certificate_and_releases_it_after_the_runner_stops()
    {
        await using var service = await ServiceCase.StartAsync(execute: true);
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            var values = ServiceValues(service, root);
            var owned = MappingRuntimeSelection.Read(values.GetValueOrDefault, ServiceCase.RuntimeIdentity, service.Runtime.BuildIdentity)
                .CreateRunner(MappingEndpointRoutes.Settlement(MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline));
            var runner = Assert.IsType<FerruleServiceRunner>(owned.Runner);
            var certificate = Assert.IsType<X509Certificate2>(owned.Borrowed);
            Assert.Equal(FerruleServiceProtocol.CompiledProfile, runner.ProfileName);
            Assert.Equal(ServiceCase.RuntimeIdentity, runner.BuildIdentity);
            Assert.Equal(service.ClientCertificate.Thumbprint, certificate.Thumbprint);
            Assert.True(certificate.HasPrivateKey);
            Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync(new byte[] { 5, 6 }, default)).Failure);
            Assert.Equal(new byte[] { 5, 6 }, Assert.Single(service.Runtime.Frames));
            Assert.True(owned.Available);
            await owned.DisposeAsync();
            Assert.False(owned.Available);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 7 }, default)).Failure);
            Assert.Equal(IntPtr.Zero, certificate.Handle);
            Assert.Single(service.Runtime.Frames);

            // An unprotected delivery is refused before any connection is made.
            File.SetUnixFileMode(values[FerruleServiceEndpoint.ClientPrivateKeyFile], UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.Throws<InvalidOperationException>(() => MappingRuntimeSelection.Read(values.GetValueOrDefault, ServiceCase.RuntimeIdentity, service.Runtime.BuildIdentity).CreateRunner(MappingEndpointRoutes.Settlement(MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task The_sessions_selection_owns_its_connector_until_the_runner_stops()
    {
        var selection = MappingRuntimeSelection.Read(Selection("azureContainerAppsSessions").GetValueOrDefault, Runtime, Native);
        // A canceled execution must settle and stop its session within what the deadline leaves it.
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.CreateRunner(TimeSpan.FromSeconds(2)));
        var owned = selection.CreateRunner(MappingEndpointRoutes.Settlement(MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline));
        var runner = Assert.IsType<FerruleServiceRunner>(owned.Runner);
        var connector = Assert.IsType<AzureSessionsConnector>(owned.Borrowed);
        Assert.Equal(FerruleServiceProtocol.CompiledProfile, runner.ProfileName);
        Assert.Equal(Runtime, runner.BuildIdentity);
        Assert.True(owned.Available);
        await owned.DisposeAsync();
        Assert.False(owned.Available);
        // The disposed connector sends nothing, not even a token request.
        await using var channel = connector.Open();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(FerruleServiceProtocol.ReadinessPath, UriKind.Relative));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(request, default));
    }

    [Fact]
    public void A_service_runner_for_another_profile_cannot_become_the_compiled_runtime()
    {
        using var connector = new AzureSessionsRunnerSettings(new Uri(PoolEndpoint), Native, null).CreateConnector();
        var backend = new Braid.Deployment.BuildBackendIdentity("ferrule", Braid.MappingBackends.FerruleInspectionBackend.EngineRevision, Native,
            Braid.MappingBackends.FerrulePreviewBackend.AdapterBuild);
        foreach (var (profile, toolchain) in new[]
        {
            (FerruleServiceProtocol.InspectionProfile, (string?)null), (FerruleServiceProtocol.PreviewProfile, null), (FerruleServiceProtocol.CompileProfile, Toolchain)
        })
            Assert.Throws<ArgumentException>(() => new Braid.MappingBackends.FerruleCompiledBuildRuntime(
                new FerruleServiceRunner(new(connector, profile, Native, toolchain)), backend));
        _ = new Braid.MappingBackends.FerruleCompiledBuildRuntime(
            new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompiledProfile, Native, compiledRuntimeIdentity: Runtime)), backend);
    }

    private static Dictionary<string, string> Selection(string runner) => runner switch
    {
        "docker" => new() { [FerruleRunnerSelection.Setting] = "docker" },
        "service" => new()
        {
            [FerruleRunnerSelection.Setting] = "service",
            [FerruleServiceEndpoint.Identity] = Native,
            [FerruleRunnerSelection.CompileToolchainSetting] = Toolchain,
            [FerruleServiceEndpoint.Url] = "https://127.0.0.1:8443",
            [FerruleServiceEndpoint.CertificatePin] = new string('a', 64),
            [FerruleServiceEndpoint.ClientCertificateFile] = "/private/certificate",
            [FerruleServiceEndpoint.ClientPrivateKeyFile] = "/private/key"
        },
        _ => new()
        {
            [FerruleRunnerSelection.Setting] = "azureContainerAppsSessions",
            [AzureSessionsRunnerSettings.Identity] = Native,
            [FerruleRunnerSelection.CompileToolchainSetting] = Toolchain,
            [AzureSessionsRunnerSettings.PoolEndpoint] = PoolEndpoint
        }
    };

    /// <summary>The service selection for an in-process service case, with its client certificate as protected files under <paramref name="root"/>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static MappingRuntimeSelection ServiceSelection(ServiceCase service, string root) =>
        MappingRuntimeSelection.Read(ServiceValues(service, root).GetValueOrDefault, ServiceCase.RuntimeIdentity, service.Runtime.BuildIdentity);

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static Dictionary<string, string> ServiceValues(ServiceCase service, string root)
    {
        var values = Selection("service");
        values[FerruleServiceEndpoint.Url] = "https://127.0.0.1:" + service.Server.Port;
        values[FerruleServiceEndpoint.CertificatePin] = service.ServerPin;
        values[FerruleServiceEndpoint.Identity] = service.Runtime.BuildIdentity;
        using var key = service.ClientCertificate.GetRSAPrivateKey()!;
        values[FerruleServiceEndpoint.ClientCertificateFile] = Protected(root, "client.crt", service.ClientCertificate.ExportCertificatePem());
        values[FerruleServiceEndpoint.ClientPrivateKeyFile] = Protected(root, "client.key", key.ExportPkcs8PrivateKeyPem());
        return values;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string Protected(string root, string name, string text)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}
