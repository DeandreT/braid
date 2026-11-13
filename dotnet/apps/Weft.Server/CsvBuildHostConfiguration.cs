using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.MappingBackends.AzureSessions;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;

namespace Weft.Server;

/// <summary>Snapshot only non-secret CSV compiler selection before protected deliveries are opened.</summary>
internal sealed record CsvBuildHostSettings(FerruleRunnerKind Kind, string NativeIdentity, string ProfileIdentity,
    string? Toolchain, string? Docker, FerruleServiceEndpoint? Service, AzureSessionsRunnerSettings? Sessions,
    string? CertificateFile, string? KeyFile)
{
    internal const string EnabledSetting = "WEFT_BUILDS_CSV_ENABLED";

    internal static CsvBuildHostSettings Read(Func<string, string?> read, bool session, string expectedProfileIdentity)
    {
        var kind = session
            ? FerruleRunnerSelection.Read(read, FerruleRunnerKind.InspectionWorker, FerruleRunnerKind.Service, FerruleRunnerKind.AzureContainerAppsSessions)
            : FerruleRunnerSelection.Read(read, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service, FerruleRunnerKind.AzureContainerAppsSessions);
        if ((kind != FerruleRunnerKind.Service && (FerruleServiceEndpoint.Configured(read) ||
                read(FerruleServiceEndpoint.ClientCertificateFile) is not null || read(FerruleServiceEndpoint.ClientPrivateKeyFile) is not null)) ||
            (kind != FerruleRunnerKind.AzureContainerAppsSessions && AzureSessionsRunnerSettings.Configured(read)) ||
            read("WEFT_INSPECTION_WORKER_URL") is not null || read("WEFT_INSPECTION_WORKER_CERTIFICATE_SHA256") is not null)
            throw new ArgumentException("CSV build inspection requires one unambiguous compiler transport.");
        var native = InspectionTransport.RequireImage(read("WEFT_FERRULE_WORKER_IMAGE") ?? throw Invalid());
        var toolchain = FerruleRunnerSelection.ReadCompileToolchain(read, kind, required: true);
        var identity = toolchain is null ? native : CompileWorkerIdentity.Combine(native, toolchain);
        if (identity != expectedProfileIdentity) throw Invalid();
        if (kind == FerruleRunnerKind.Docker)
        {
            var docker = read("WEFT_DOCKER_EXECUTABLE") ?? "/usr/bin/docker";
            if (!docker.StartsWith('/') || docker.Contains("..", StringComparison.Ordinal) || docker.Any(c => c is '\0' or '\r' or '\n' or '\\')) throw Invalid();
            return new(kind, native, identity, null, docker, null, null, null, null);
        }
        if (kind == FerruleRunnerKind.AzureContainerAppsSessions)
            return new(kind, native, identity, toolchain, null, null, AzureSessionsRunnerSettings.Read(read), null, null);
        string Required(string key) => read(key) is { Length: > 0 } value ? value : throw Invalid();
        return new(kind, native, identity, toolchain, null, FerruleServiceEndpoint.Read(read), null,
            Required(FerruleServiceEndpoint.ClientCertificateFile), Required(FerruleServiceEndpoint.ClientPrivateKeyFile));
    }

    private static ArgumentException Invalid() => new("CSV build compiler selection is invalid.");
}

internal static class CsvBuildHostConfiguration
{
    internal static void Register(IServiceCollection services, CsvBuildHostSettings settings)
    {
        services.AddSingleton(provider => new CsvBuildHostRunner(settings, provider));
        services.AddSingleton<IHostedService>(provider => new InspectionWorkerHostLifetime(
            () => provider.GetRequiredService<CsvBuildHostRunner>().StopAsync()));
    }
}

/// <summary>Creates a compiler-profile reader lazily; metadata capabilities and retained recovery open no runner or private delivery.</summary>
internal sealed class CsvBuildHostRunner : IInspectionWorkerRunner, ICompilationIdentitySource, IExecutionAdmission, IAsyncDisposable
{
    private readonly CsvBuildHostSettings settings;
    private readonly Lazy<Owned> owned;
    private readonly object gate = new();
    private Task? stopping;

    internal CsvBuildHostRunner(CsvBuildHostSettings settings, IServiceProvider services)
    {
        this.settings = settings;
        owned = new(() => Create(settings, services));
    }

    public string BuildIdentity => settings.NativeIdentity;
    public string CompilationBuildIdentity => settings.ProfileIdentity;
    public bool CanExecute
    {
        get { lock (gate) return stopping is null && (!owned.IsValueCreated || owned.Value.Runner is not IExecutionAdmission { CanExecute: false }); }
    }

    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
    {
        IInspectionWorkerRunner runner;
        lock (gate)
        {
            if (stopping is not null) throw new InvalidOperationException("CSV build inspection is stopped.");
            runner = owned.Value.Runner;
        }
        var compiler = runner is ICompilationIdentitySource source ? source.CompilationBuildIdentity : runner.BuildIdentity;
        // Remote compile runners expose the native/toolchain combination as
        // BuildIdentity; the Docker runner exposes its single immutable image.
        // The facade keeps the native identity and separate compilation pin.
        var expectedRunnerIdentity = settings.Kind switch
        {
            FerruleRunnerKind.Docker => settings.NativeIdentity,
            FerruleRunnerKind.Service or FerruleRunnerKind.AzureContainerAppsSessions => settings.ProfileIdentity,
            _ => throw new InvalidOperationException("CSV compiler transport is unsupported.")
        };
        if (runner.BuildIdentity != expectedRunnerIdentity || compiler != CompilationBuildIdentity)
            throw new InvalidOperationException("CSV compiler identity differs from configured build provenance.");
        return await runner.RunAsync(requestFrame, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        lock (gate) return stopping ??= owned.IsValueCreated ? owned.Value.DisposeAsync().AsTask() : Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => new(StopAsync());

    private static Owned Create(CsvBuildHostSettings settings, IServiceProvider services)
    {
        if (settings.Kind == FerruleRunnerKind.Docker)
            return new(new DockerInspectionWorkerRunner(new(settings.Docker!, settings.NativeIdentity, profile: DockerWorkerProfile.Build)), null, null);
        // A native inspection companion may already own this exact configured connector.
        // Borrow it, but do not dispose it before its other profile runners stop.
        if (services.GetKeyedService<IFerruleServiceConnector>(nameof(IFerruleServiceConnector)) is { } shared)
            return new(new FerruleServiceRunner(new(shared, FerruleServiceProtocol.CompileProfile, settings.NativeIdentity, settings.Toolchain)), null, null);
        if (settings.Sessions is { } sessions)
        {
            var connector = sessions.CreateConnector();
            try { return new(new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompileProfile, settings.NativeIdentity, settings.Toolchain)), connector, null); }
            catch { connector.Dispose(); throw; }
        }
        var endpoint = settings.Service!;
        var certificate = PinnedCertificates.FromPem(HostPrivateFiles.ReadText(settings.CertificateFile!, 65_536), HostPrivateFiles.ReadText(settings.KeyFile!, 65_536));
        try
        {
            var connector = new PinnedServiceConnector(new(endpoint.Endpoint, endpoint.ServiceCertificateSha256, certificate));
            return new(new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompileProfile, settings.NativeIdentity, settings.Toolchain)), null, certificate);
        }
        catch { certificate.Dispose(); throw; }
    }

    private sealed record Owned(IInspectionWorkerRunner Runner, IDisposable? Connector, X509Certificate2? Certificate) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await ((IAsyncDisposable)Runner).DisposeAsync().ConfigureAwait(false); }
            finally { Connector?.Dispose(); Certificate?.Dispose(); }
        }
    }
}
