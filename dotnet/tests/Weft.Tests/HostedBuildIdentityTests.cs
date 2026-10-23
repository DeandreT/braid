using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.Jobs;
using Weft.MappingBackends.AzureSessions;
using Weft.MappingBackends.Workers;
using Weft.Server;
using Weft.Worker;

namespace Weft.Tests;

public sealed class HostedBuildIdentityTests
{
    private static readonly string Native = "sha256:" + new string('a', 64);
    private static readonly string Toolchain = "sha256:" + new string('b', 64);
    private static string Combined => CompileWorkerIdentity.Combine(Native, Toolchain);

    [Theory]
    [InlineData(FerruleRunnerKind.Docker)]
    [InlineData(FerruleRunnerKind.InspectionWorker)]
    public void A_toolchain_pin_is_not_valid_for_a_non_service_runner(FerruleRunnerKind kind) =>
        Assert.Throws<InvalidOperationException>(() => FerruleRunnerSelection.ReadCompileToolchain(_ => Toolchain, kind));

    [Theory]
    [InlineData(FerruleRunnerKind.Service)]
    [InlineData(FerruleRunnerKind.AzureContainerAppsSessions)]
    public void Compiling_through_a_service_requires_an_explicit_pin(FerruleRunnerKind kind)
    {
        Assert.Null(FerruleRunnerSelection.ReadCompileToolchain(_ => null, kind));
        Assert.Throws<InvalidOperationException>(() => FerruleRunnerSelection.ReadCompileToolchain(_ => null, kind, required: true));
        Assert.Equal(Toolchain, FerruleRunnerSelection.ReadCompileToolchain(_ => Toolchain, kind, required: true));
        Assert.Throws<ArgumentException>(() => FerruleRunnerSelection.ReadCompileToolchain(_ => "latest", kind));
    }

    [Fact]
    public void The_build_API_and_report_registry_share_the_combined_identity_without_resolving_a_worker()
    {
        var values = new Dictionary<string, string>
        {
            ["WEFT_BUILDS_ENABLED"] = "true",
            ["WEFT_FERRULE_RUNNER"] = "service",
            ["WEFT_FERRULE_WORKER_IMAGE"] = Native,
            [FerruleRunnerSelection.CompileToolchainSetting] = Toolchain,
            ["WEFT_PRIVATE_STORAGE_KIND"] = "azure-blob",
            ["WEFT_PRIVATE_STORAGE_BLOB_ENDPOINT"] = "http://127.0.0.1:10000/devstoreaccount1",
            ["WEFT_PRIVATE_STORAGE_BLOB_CONTAINER"] = "private-objects",
            ["WEFT_PRIVATE_STORAGE_BLOB_CREDENTIAL"] = "emulator"
        };
        var runtime = CatalogBuildHostConfiguration.ReadRuntime(values.GetValueOrDefault, true);
        Assert.Equal(Combined, runtime.Image);
        var registry = CatalogRevisionReportHostConfiguration.CreateRegistry(Native, new RevisionReportPolicy(),
            () => throw new InvalidOperationException("No worker is resolved by identity admission."), Toolchain);
        Assert.Equal(Combined, registry.FindCurrent("ferrule", RevisionReportOperation.Projection)!.Profile.BackendBuild);
        Assert.Equal(Combined, registry.FindCurrent("ferrule", RevisionReportOperation.Validation)!.Profile.BackendBuild);
    }

    [Fact]
    public async Task Sessions_preview_and_compilation_use_one_identity_and_preserve_compiler_admission()
    {
        var values = new Dictionary<string, string>
        {
            ["WEFT_FERRULE_RUNNER"] = "azureContainerAppsSessions",
            [AzureSessionsRunnerSettings.PoolEndpoint] = "https://weft-ferrule.example-env.westus2.azurecontainerapps.io",
            ["WEFT_FERRULE_WORKER_IMAGE"] = Native,
            [FerruleRunnerSelection.CompileToolchainSetting] = Toolchain,
            ["WEFT_BUILD_WORKER_ENABLED"] = "true"
        };
        var settings = new PreviewWorkerSettings(true, "unused", null, Native, "/usr/bin/docker", TimeSpan.FromMinutes(2));
        var selected = PreviewRunnerSettings.Read(values.GetValueOrDefault, settings);
        Assert.Equal(Combined, selected.ProfileIdentity(settings));
        var preview = selected.CreateRunner(settings);
        var compile = selected.CreateBuildRunner(settings);
        await using var previewOwner = (IAsyncDisposable)preview;
        await using var compileOwner = (IAsyncDisposable)compile;
        Assert.Equal(Combined, preview.BuildIdentity);
        Assert.Null(Assert.IsAssignableFrom<ICompilationIdentitySource>(preview).CompilationBuildIdentity);
        Assert.Equal(Combined, compile.BuildIdentity);
        Assert.Equal(Combined, Assert.IsAssignableFrom<ICompilationIdentitySource>(compile).CompilationBuildIdentity);
        values.Remove(FerruleRunnerSelection.CompileToolchainSetting);
        Assert.Throws<InvalidOperationException>(() => PreviewRunnerSettings.Read(values.GetValueOrDefault, settings));
    }

    [Fact]
    public async Task Service_build_registration_defers_creation_and_owns_the_selected_runner()
    {
        var runner = new Runner();
        var services = new ServiceCollection();
        var calls = 0;
        services.AddWeftBuildWorkerWithRunner(key => key == "WEFT_BUILD_WORKER_ENABLED" ? "true" : "Host=127.0.0.1;Database=unused",
            () => { calls++; return runner; });
        Assert.Equal(0, calls);
        await using (var provider = services.BuildServiceProvider())
            Assert.Same(runner, provider.GetRequiredKeyedService<IInspectionWorkerRunner>("build-worker"));
        Assert.Equal(1, calls);
        Assert.True(runner.Disposed);
        services = new();
        services.AddWeftBuildWorkerWithRunner(key => key == "WEFT_BUILD_WORKER_ENABLED" ? "false" : throw new InvalidOperationException(),
            () => throw new InvalidOperationException());
        Assert.Empty(services);
    }

    private sealed class Runner : IInspectionWorkerRunner, IAsyncDisposable
    {
        public string BuildIdentity => Combined;
        internal bool Disposed;
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
