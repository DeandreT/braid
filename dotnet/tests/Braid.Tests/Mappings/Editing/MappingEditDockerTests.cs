using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Xunit.Abstractions;

namespace Braid.Tests;

public sealed class MappingEditDockerTests(ITestOutputHelper output)
{
    [EditDockerFact]
    public async Task DockerAndServiceReplayTheSameExactBaseAndRefuseTheSameFailingOperation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "engines/ferrule-host"))) root = root.Parent;
        var native = await File.ReadAllBytesAsync(Path.Combine(root!.FullName, "engines/ferrule-host/tests/fixtures/valid-basic.json"));
        var artifact = NativeArtifact.Admit(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), native, MappingEditPolicy.MaximumNativeBytes);
        var launcher = new RecordingLauncher(output);
        var scratch = Path.Combine(Path.GetTempPath(), "braid-edit-docker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await using var docker = new DockerInspectionWorkerRunner(new("/usr/bin/docker", Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")!, TimeSpan.FromSeconds(10), profile: DockerWorkerProfile.Edit), launcher);
            await using var service = new ProcessWorkerRunner(new(Environment.GetEnvironmentVariable("BRAID_EDIT_TEST_EXECUTABLE")!, DockerWorkerProfile.Edit, scratch));
            var scripts = new[] { MappingEditScript.Empty, MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)),
                MappingEditScript.Parse("{\"format\":\"ferrule.edit.v1\",\"operations\":[{\"op\":\"edit_constant\",\"node\":\"4294967295\",\"value\":{\"type\":\"int\",\"text\":\"1\"}}]}"u8) };
            for (var index = 0; index < scripts.Length; index++)
            {
                var script = scripts[index];
                var expected = await new FerruleInspectionBackend(service).ReplayAsync(artifact, script);
                var actual = await new FerruleInspectionBackend(docker).ReplayAsync(artifact, script);
                if (index < 2) { Assert.Null(expected.Failure); Assert.NotNull(expected.Checkpoint); }
                else Assert.Equal("operationRejected", expected.Failure?.Code);
                Assert.Equal(expected.Failure?.Code, actual.Failure?.Code);
                Assert.Equal(expected.Failure?.OperationIndex, actual.Failure?.OperationIndex);
                if (expected.Checkpoint is { } baseline)
                {
                    var checkpoint = Assert.IsType<MappingEditCheckpoint>(actual.Checkpoint);
                    Assert.Equal(baseline.Artifact.CopyBytes(), checkpoint.Artifact.CopyBytes());
                    var json = ProjectionJson.CreateOptions();
                    Assert.Equal(JsonSerializer.Serialize(baseline.Validation with { Provenance = null }, json), JsonSerializer.Serialize(checkpoint.Validation with { Provenance = null }, json));
                    Assert.Equal(JsonSerializer.Serialize(baseline.Projection.Document! with { Provenance = checkpoint.Projection.Document!.Provenance }, json), JsonSerializer.Serialize(checkpoint.Projection.Document, json));
                }
                else Assert.Null(actual.Checkpoint);
            }
            await docker.StopAsync();
            await service.StopAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            Assert.Equal(3, launcher.Created);
        }
        finally { Directory.Delete(scratch); }
    }

    private sealed class RecordingLauncher(ITestOutputHelper output) : IWorkerProcessLauncher
    {
        public int Created;
        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var args = startInfo.ArgumentList.ToArray(); var name = Array.IndexOf(args, "--name");
            if (name >= 0)
            {
                var label = args.Single(arg => arg.StartsWith(DockerWorkerOwnershipTestSupport.OperationLabel + "=", StringComparison.Ordinal));
                output.WriteLine("Edit owned worker: " + args[name + 1] + " " + label); Interlocked.Increment(ref Created);
            }
            var process = new Process { StartInfo = startInfo }; process.Start(); return new Child(process);
        }
    }
    private sealed class Child(Process process) : IWorkerProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public void CloseStandardInput() => process.StandardInput.Dispose();
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose() { process.StandardInput.Dispose(); process.Dispose(); }
    }
    public sealed class EditDockerFactAttribute : FactAttribute
    {
        public EditDockerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE") is null || Environment.GetEnvironmentVariable("BRAID_EDIT_TEST_EXECUTABLE") is null)
                Skip = "Set the exact edit-capable image and its extracted native worker for owned Docker parity.";
        }
    }
}
