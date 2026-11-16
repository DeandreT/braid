using System.Text;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;

namespace Braid.Tests;

public sealed class BuildWorkerLiveFactAttribute : FactAttribute
{
    public BuildWorkerLiveFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("BRAID_BUILD_WORKER_IMAGE") is null)
            Skip = "Set the exact BRAID_BUILD_WORKER_IMAGE on Linux to qualify actual isolated compilation.";
    }
}
public sealed class FerruleBuildLiveTests
{
    [BuildWorkerLiveFact]
    public async Task Canceling_actual_compiler_start_removes_its_attributed_container()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "engines/ferrule-host/tests/fixtures"))) root = root.Parent;
        Assert.NotNull(root);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "engines/ferrule-host/tests/fixtures/valid-basic.json"));
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var header = new BuildWorkerProtocol.Request(BuildWorkerProtocol.Protocol, Guid.NewGuid().ToString("D"), scope,
            Sha256Digest.FromBytes(bytes).Value, new string('a', 64), "compile", BuildWorkerProtocol.Profile);
        var launcher = new ObservedLauncher();
        await using var runner = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker",
            Environment.GetEnvironmentVariable("BRAID_BUILD_WORKER_IMAGE")!, profile: DockerWorkerProfile.Build), launcher);
        using var cancellation = new CancellationTokenSource();
        var pending = runner.RunAsync(BuildWorkerProtocol.EncodeRequest(header, bytes), cancellation.Token);
        try
        {
            await launcher.Starting.Task.WaitAsync(TimeSpan.FromSeconds(60));
            cancellation.Cancel();
            Assert.Equal(WorkerProcessFailure.Canceled, (await pending).Failure);
            Assert.NotNull(launcher.ContainerName);
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--host", "unix:///var/run/docker.sock", "container", "inspect", launcher.ContainerName! }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(output, error, process.WaitForExitAsync());
            Assert.NotEqual(0, process.ExitCode);
        }
        finally { cancellation.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }
    [BuildWorkerLiveFact]
    public async Task Exact_native_mapping_is_emitted_and_compiled_by_the_actual_isolated_image()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "engines/ferrule-host/tests/fixtures"))) root = root.Parent;
        Assert.NotNull(root);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root.FullName, "engines/ferrule-host/tests/fixtures/valid-basic.json"));
        var actor = new ActorContext(new(Guid.NewGuid()));
        var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var image = Environment.GetEnvironmentVariable("BRAID_BUILD_WORKER_IMAGE")!;
        var native = new PrivateObjectReference(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(bytes), bytes.Length);
        var source = new Source(actor, native, bytes);
        await using var runner = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker", image, profile: DockerWorkerProfile.Build));
        var compiler = new FerruleBuildCompiler(runner, new(actor, source, source));
        var intent = new BuildIntent(Guid.NewGuid(), scope, native.Digest, compiler.Backend, FerruleBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
            new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        var capability = await compiler.InspectAsync(intent);
        Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
        var artifact = await compiler.BuildAsync(intent, new(bytes, bytes.Length));
        Assert.True(artifact.Length > 1024);
        Assert.Equal("MZ", Encoding.ASCII.GetString(artifact.Copy(), 0, 2));
    }
    private sealed class Source(ActorContext actor, PrivateObjectReference reference, byte[] bytes) : IBuildAdmissionReader, IPrivateObjectStore
    {
        public Task<BuildAdmission?> ReadAsync(ActorContext expected, BuildIntent intent, CancellationToken token = default) =>
            Task.FromResult<BuildAdmission?>(new(actor.PrincipalId, intent.Fingerprint, reference));
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference value, CancellationToken token = default) => Task.FromResult(bytes.ToArray());
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference value, Stream source, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class ObservedLauncher : IWorkerProcessLauncher
    {
        private readonly WorkerProcessLauncher actual = new();
        internal string? ContainerName;
        internal readonly TaskCompletionSource Starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWorkerProcess Start(System.Diagnostics.ProcessStartInfo start)
        {
            var args = start.ArgumentList.ToArray();
            var index = Array.IndexOf(args, "--name");
            if (index >= 0) ContainerName = args[index + 1];
            var process = actual.Start(start);
            if (args.Contains("start") && args.Contains("--attach")) Starting.TrySetResult();
            return process;
        }
    }
}
