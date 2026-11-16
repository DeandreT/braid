using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Xunit.Abstractions;

namespace Braid.Tests;

public sealed class FerruleWorkerCleanupStabilityTests(ITestOutputHelper output)
{
    [FerruleWorkerIntegrationTests.WorkerFact(Timeout = 180000)]
    public async Task Repeated_inspection_and_validation_settle_under_the_original_cleanup_budget()
    {
        var image = Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")!;
        var diagnostics = new RuntimeDiagnosticLauncher();
        var launcher = new OwnedLauncher(diagnostics);
        var runner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image), launcher);
        var backend = new FerruleInspectionBackend(runner);
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "valid-basic.json"));
        var artifact = NativeArtifact.Admit(scope, bytes, InspectionPolicy.MaximumNativeBytes);
        Exception? primary = null, cleanup = null;
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(2), DockerInspectionWorkerOptions.DefaultCleanupTimeout);
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var inspected = await backend.InspectAsync(artifact);
                Assert.True(inspected.Status == MappingOperationStatus.Succeeded,
                    $"Inspection {iteration}: {inspected.Status}; {diagnostics.Summary}");
                var validated = await backend.ValidateAsync(artifact);
                Assert.True(validated.Status == MappingOperationStatus.Succeeded,
                    $"Validation {iteration}: {validated.Status}; {diagnostics.Summary}");
            }
            Assert.Equal(bytes, artifact.CopyBytes());
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { await runner.DisposeAsync(); }
            catch (Exception error) { cleanup = error; }
            output.WriteLine(diagnostics.Summary);
            // Only generated ownership labels, never input/output or inherited credentials.
            foreach (var worker in launcher.Created)
                output.WriteLine("Owned worker: " + worker);
        }
        if (primary is not null && cleanup is not null) throw new AggregateException(primary, cleanup);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
    }

    private sealed class OwnedLauncher(RuntimeDiagnosticLauncher diagnostics) : IWorkerProcessLauncher
    {
        internal List<string> Created { get; } = [];
        public IWorkerProcess Start(ProcessStartInfo info)
        {
            var arguments = info.ArgumentList.ToArray();
            if (arguments.Contains("create"))
            {
                var name = Array.IndexOf(arguments, "--name");
                var label = Array.IndexOf(arguments, "--label");
                if (name >= 0 && label >= 0) Created.Add(arguments[name + 1] + " " + arguments[label + 1]);
            }
            return diagnostics.Start(info);
        }
    }
}
