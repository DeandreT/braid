using System.Security.Cryptography.X509Certificates;
using Braid.MappingBackends.AzureSessions;
using Braid.MappingBackends.Workers;
using Braid.Storage.FileSystem;
using Braid.WorkerTransport;

namespace Braid.MachineEndpoint;

/// <summary>One explicit runtime transport with independently pinned compiler and execution identities.</summary>
internal sealed record MappingRuntimeSelection(FerruleRunnerKind Kind, string RuntimeIdentity, string CompilerIdentity,
    string Docker, FerruleServiceEndpoint? Service = null, AzureSessionsRunnerSettings? Sessions = null,
    string? CertificateFile = null, string? KeyFile = null)
{
    internal const string CompilerBuild = "BRAID_ENDPOINT_COMPILER_BUILD";

    /// <summary>Every setting of the service runner, including its client certificate deliveries.</summary>
    internal static readonly string[] ServiceSettings =
    [
        FerruleServiceEndpoint.Url, FerruleServiceEndpoint.CertificatePin,
        FerruleServiceEndpoint.ClientCertificateFile, FerruleServiceEndpoint.ClientPrivateKeyFile
    ];

    internal static MappingRuntimeSelection Read(Func<string, string?> environment, string runtime, string native)
    {
        ArgumentNullException.ThrowIfNull(environment);
        // An empty value is unset, as for every other endpoint setting.
        string? Optional(string name) => environment(name) is { Length: > 0 } value ? value : null;
        var kind = FerruleRunnerSelection.Read(Optional, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker,
            FerruleRunnerKind.Service, FerruleRunnerKind.AzureContainerAppsSessions);
        // Another runner's settings beside the selection are ambiguous, never ignored.
        if ((kind != FerruleRunnerKind.Docker && Optional(MappingEndpointSettings.DockerSetting) is not null) ||
            (kind != FerruleRunnerKind.Service && ServiceSettings.Any(name => Optional(name) is not null)) ||
            (kind != FerruleRunnerKind.AzureContainerAppsSessions && AzureSessionsRunnerSettings.Configured(Optional)))
            throw MappingEndpointSettings.Failure();
        var originalCompiler = Optional(CompilerBuild);
        if (originalCompiler is not null && (kind == FerruleRunnerKind.Docker || Optional(FerruleRunnerSelection.CompileToolchainSetting) is not null))
            throw MappingEndpointSettings.Failure();
        var pin = FerruleRunnerSelection.ReadCompileToolchain(Optional, kind, required: originalCompiler is null);
        var compiler = originalCompiler is not null ? InspectionTransport.RequireImage(originalCompiler)
            : pin is null ? native : CompileWorkerIdentity.Combine(native, pin);
        var docker = Optional(MappingEndpointSettings.DockerSetting) ?? "/usr/bin/docker";
        if (kind == FerruleRunnerKind.Docker)
        {
            if (!Path.IsPathFullyQualified(docker) || docker != Path.GetFullPath(docker)) throw MappingEndpointSettings.Failure();
            return new(kind, runtime, compiler, docker);
        }
        if (kind == FerruleRunnerKind.AzureContainerAppsSessions)
            return new(kind, runtime, compiler, docker, Sessions: AzureSessionsRunnerSettings.Read(Optional));
        var certificate = Optional(FerruleServiceEndpoint.ClientCertificateFile) ?? throw MappingEndpointSettings.Failure();
        var key = Optional(FerruleServiceEndpoint.ClientPrivateKeyFile) ?? throw MappingEndpointSettings.Failure();
        return new(kind, runtime, compiler, docker, Service: FerruleServiceEndpoint.Read(Optional), CertificateFile: certificate, KeyFile: key);
    }

    /// <param name="settlement">
    /// What the endpoint's deadline leaves a canceled execution after its work bound, the attempt's release and the
    /// response. A service execution must settle and close its channel within it, so the call keeps its deadline; one
    /// that does not settle has an unknown outcome, which closes the runner. Docker's own cleanup bounds are shorter.
    /// </param>
    internal Owned CreateRunner(TimeSpan settlement)
    {
        if (Kind == FerruleRunnerKind.Docker)
            return new(new DockerInspectionWorkerRunner(new(Docker, RuntimeIdentity, profile: DockerWorkerProfile.Compiled)));
        if (Sessions is { } sessions)
        {
            var connector = sessions.CreateConnector();
            try
            {
                var options = new FerruleServiceRunnerOptions(connector, FerruleServiceProtocol.CompiledProfile, sessions.BuildIdentity,
                    compiledRuntimeIdentity: RuntimeIdentity)
                {
                    CancellationSettlement = Positive(settlement - connector.StopBound)
                };
                return new(new FerruleServiceRunner(options), connector);
            }
            catch { connector.Dispose(); throw; }
        }
        var certificate = PinnedCertificates.FromPem(ProtectedHostFiles.ReadText(CertificateFile!, 65536), ProtectedHostFiles.ReadText(KeyFile!, 65536));
        try
        {
            var endpoint = Service!;
            var connector = new PinnedServiceConnector(new(endpoint.Endpoint, endpoint.ServiceCertificateSha256, certificate));
            var options = new FerruleServiceRunnerOptions(connector, FerruleServiceProtocol.CompiledProfile, endpoint.BuildIdentity,
                compiledRuntimeIdentity: RuntimeIdentity)
            {
                CancellationSettlement = Positive(settlement)
            };
            return new(new FerruleServiceRunner(options), certificate);
        }
        catch { certificate.Dispose(); throw; }
    }

    private static TimeSpan Positive(TimeSpan settlement) => settlement > TimeSpan.Zero
        ? settlement : throw new ArgumentOutOfRangeException(nameof(settlement), "The endpoint deadline leaves no time to settle a canceled execution.");

    /// <summary>
    /// The selected runner, exposed as itself so the compiled runtime checks its profile, and what it borrows: the
    /// connector or client certificate is released only after the runner has joined its work.
    /// </summary>
    internal sealed class Owned(IInspectionWorkerRunner runner, IDisposable? borrowed = null) : IAsyncDisposable
    {
        internal IInspectionWorkerRunner Runner => runner;
        internal IDisposable? Borrowed => borrowed;

        /// <summary>False once the runner closed after unconfirmed cleanup or an uncertain answer; it stays closed until restart.</summary>
        internal bool Available => runner switch
        {
            DockerInspectionWorkerRunner docker => docker.Available,
            FerruleServiceRunner service => service.Available,
            _ => false
        };

        public async ValueTask DisposeAsync()
        {
            try { await ((IAsyncDisposable)runner).DisposeAsync().ConfigureAwait(false); }
            finally { borrowed?.Dispose(); }
        }
    }
}
