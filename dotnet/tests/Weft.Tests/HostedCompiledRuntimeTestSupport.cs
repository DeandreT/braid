using System.Security.Cryptography.X509Certificates;
using Weft.MachineEndpoint;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class HostedCompiledRuntimeTestSupport : IAsyncDisposable
{
    private readonly ServerHostFiles files;
    private readonly string root;
    private readonly ProcessCompiledRuntimeRunner identity;
    private readonly PackagedFerruleService service;
    private readonly string certificate, key, serverPin;

    private HostedCompiledRuntimeTestSupport(ServerHostFiles files, string root, ProcessCompiledRuntimeRunner identity,
        PackagedFerruleService service, string certificate, string key, string serverPin)
    { this.files = files; this.root = root; this.identity = identity; this.service = service; this.certificate = certificate; this.key = key; this.serverPin = serverPin; }

    internal string RuntimeIdentity => identity.RuntimeIdentity;

    internal static async Task<HostedCompiledRuntimeTestSupport> StartAsync()
    {
        var files = new ServerHostFiles();
        var root = FerruleServiceTestSupport.TemporaryRoot();
        ProcessCompiledRuntimeRunner? identity = null;
        try
        {
            var server = files.HistoricalPair("execution-server", expired: false);
            var client = files.HistoricalPair("execution-client", expired: false);
            using var serverCertificate = X509CertificateLoader.LoadCertificateFromFile(server.CertificateFile);
            using var clientCertificate = X509CertificateLoader.LoadCertificateFromFile(client.CertificateFile);
            identity = new(FerruleCompiledServiceTests.Options(root));
            var service = await PackagedFerruleService.StartAsync(File.ReadAllText(server.CertificateFile) + "\n" + File.ReadAllText(server.PrivateKeyFile),
                [InspectionTransportTests.TransportCase.Pin(clientCertificate)], root, extra: new Dictionary<string, string>
                {
                    [Weft.FerruleService.FerruleServiceSettings.ExecutionEnabled] = "true",
                    [Weft.FerruleService.FerruleServiceSettings.RuntimeAssemblySetting] = FerruleCompiledServiceTests.Assembly,
                    [Weft.FerruleService.FerruleServiceSettings.RuntimeLauncherSetting] = FerruleServiceTestSupport.Launcher!,
                    [Weft.FerruleService.FerruleServiceSettings.RuntimeRootSetting] = FerruleServiceTestSupport.Toolchain
                });
            return new(files, root, identity, service, client.CertificateFile, client.PrivateKeyFile, InspectionTransportTests.TransportCase.Pin(serverCertificate));
        }
        catch
        {
            if (identity is not null) await identity.DisposeAsync();
            files.Dispose(); Directory.Delete(root, recursive: true); throw;
        }
    }

    internal void Configure(IDictionary<string, string> values, string compilerIdentity)
    {
        values[FerruleRunnerSelection.Setting] = "service";
        values[FerruleServiceEndpoint.Identity] = identity.BuildIdentity;
        values[MappingEndpointSettings.RuntimeImageSetting] = RuntimeIdentity;
        values[MappingRuntimeSelection.CompilerBuild] = compilerIdentity;
        values[FerruleServiceEndpoint.Url] = "https://127.0.0.1:" + service.Port;
        values[FerruleServiceEndpoint.CertificatePin] = serverPin;
        values[FerruleServiceEndpoint.ClientCertificateFile] = certificate;
        values[FerruleServiceEndpoint.ClientPrivateKeyFile] = key;
        values.Remove(MappingEndpointSettings.DockerSetting);
    }

    internal MappingRuntimeSelection.Owned CreateRunner(string compilerIdentity)
    {
        var values = new Dictionary<string, string>(); Configure(values, compilerIdentity);
        return MappingRuntimeSelection.Read(values.GetValueOrDefault, RuntimeIdentity, identity.BuildIdentity).CreateRunner(MappingEndpointRoutes.Settlement(MappingEndpointRoutes.Work, MappingEndpointRoutes.Deadline));
    }

    public async ValueTask DisposeAsync()
    {
        try { await service.DisposeAsync(); }
        finally { await identity.DisposeAsync(); files.Dispose(); Directory.Delete(root, recursive: true); }
    }
}

/// <summary>The machine endpoint through the packaged service: live service children, PostgreSQL and the Docker compiler image.</summary>
public sealed class HostedMachineEndpointFactAttribute : FactAttribute
{
    public HostedMachineEndpointFactAttribute()
    {
        Skip = new ServiceCompileFactAttribute().Skip;
        if (!PostgresFixture.IsConfigured || Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE") is null ||
            Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE") is null)
            Skip ??= "Provide actual PostgreSQL and the qualified compiler and native images.";
    }
}
