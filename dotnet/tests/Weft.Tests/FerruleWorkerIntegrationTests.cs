using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class FerruleWorkerIntegrationTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [WorkerFact]
    public async Task ActualWorkerInspectsAndValidatesExactLegacyAndVersionedBytes()
    {
        var backend = new FerruleInspectionBackend(new DockerInspectionWorkerRunner(Options()));
        foreach (var fixture in new[] { "valid-basic", "versioned-exact" })
        {
            var artifact = Artifact(fixture);
            var before = artifact.CopyBytes();
            var inspection = await backend.InspectAsync(artifact);
            Assert.Equal(MappingOperationStatus.Succeeded, inspection.Status);
            Assert.True(inspection.Provenance.Confirmed);
            Assert.Equal(Scope, inspection.Provenance.Scope);
            Assert.Equal(artifact.Digest, inspection.Provenance.NativeDigest);
            Assert.Equal(FerruleInspectionBackend.EngineRevision, inspection.Provenance.EngineRevision);
            Assert.Equal(Options().ImageId, inspection.Provenance.BackendBuild);
            Assert.NotNull(inspection.Inspection);
            Assert.Equal(3, inspection.Inspection.Counts.GraphNodes);
            Assert.Equal(new[] { BoundaryRole.PrimaryInput, BoundaryRole.PrimaryOutput }, inspection.Inspection.Boundaries.Select(boundary => boundary.Role));
            Assert.All(inspection.Inspection.Capabilities.Where(capability => capability.Operation is MappingOperation.Preview or MappingOperation.Build),
                capability => Assert.Equal(CapabilityStatus.NotQualified, capability.Status));
            var validation = await backend.ValidateAsync(artifact);
            Assert.Equal(MappingOperationStatus.Succeeded, validation.Status);
            Assert.True(validation.IsValid);
            Assert.Equal(0, validation.TotalIssueCount);
            Assert.Equal(before, artifact.CopyBytes());
            Assert.Equal(Sha256Digest.FromBytes(before), artifact.Digest);
            if (fixture == "versioned-exact")
            {
                Assert.All(inspection.Inspection.Boundaries, boundary =>
                    Assert.StartsWith("/document/", Assert.IsType<JsonPointerNativeLocator>(boundary.NativeLocator).Pointer, StringComparison.Ordinal));
            }
        }
    }

    [WorkerFact]
    public async Task ActualValidationRetainsFunctionIdentityBeyondJavaScriptPrecision()
    {
        var backend = new FerruleInspectionBackend(new DockerInspectionWorkerRunner(Options()));
        var result = await backend.ValidateAsync(Artifact("invalid-function"));
        Assert.Equal(MappingOperationStatus.InvalidMapping, result.Status);
        Assert.False(result.IsValid);
        Assert.True(result.Provenance.Confirmed);
        Assert.Equal(result.TotalIssueCount, result.EmittedIssueCount);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Owner is GraphNodeDiagnosticOwner
        {
            FunctionId: "18446744073709551615",
            NodeId: "7"
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.NativeLocator is GraphNodeNativeLocator
        {
            FunctionId: "18446744073709551615",
            NodeId: "7"
        });
    }

    [WorkerFact]
    public async Task ResourceMetadataDoesNotConnectToTheHostOrDiscloseResourceValues()
    {
        using var sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var address = (IPEndPoint)sentinel.LocalEndpoint;
        var native = Encoding.UTF8.GetString(Artifact("resource-metadata").CopyBytes())
            .Replace("http://127.0.0.1:9/weft-fixture-do-not-fetch.json", $"http://127.0.0.1:{address.Port}/private?token=fixture-secret", StringComparison.Ordinal);
        var artifact = NativeArtifact.Admit(Scope, Encoding.UTF8.GetBytes(native), InspectionPolicy.MaximumNativeBytes);
        var backend = new FerruleInspectionBackend(new DockerInspectionWorkerRunner(Options()));
        var result = await backend.InspectAsync(artifact);
        Assert.Equal(MappingOperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Inspection);
        Assert.Contains(result.Inspection.UnresolvedResources, resource => resource.Kind == UnresolvedResourceKind.DesignTimeInput);
        Assert.Contains(result.Inspection.UnresolvedResources, resource => resource.Kind == UnresolvedResourceKind.EdiConfiguration);
        Assert.False(sentinel.Pending());
        var json = JsonSerializer.Serialize(result, ProjectionJson.CreateOptions());
        Assert.DoesNotContain("fixture-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("weft-fixture.invalid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/weft-fixture-do-not-read/", json, StringComparison.Ordinal);
    }

    [WorkerFact]
    public Task CancellationRemovesAnActualRunningWorkerAndAdmitsTheNextRequest() => TerminationRemovesWorker(Termination.Cancellation);

    [WorkerFact]
    public Task DeadlineRemovesAnActualRunningWorkerAndAdmitsTheNextRequest() => TerminationRemovesWorker(Termination.Deadline);

    [WorkerFact]
    public Task AbruptWorkerDeathRemovesTheContainerAndAdmitsTheNextRequest() => TerminationRemovesWorker(Termination.WorkerDeath);

    private enum Termination { Cancellation, Deadline, WorkerDeath }

    private static async Task TerminationRemovesWorker(Termination termination)
    {
        var launcher = new HoldingInputLauncher();
        var backend = new FerruleInspectionBackend(new DockerInspectionWorkerRunner(Options(TimeSpan.FromSeconds(5)), launcher));
        using var cancellation = new CancellationTokenSource();
        var operation = backend.InspectAsync(Artifact("valid-basic"), cancellation.Token);
        try
        {
            await launcher.InputWritten.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.NotNull(launcher.ContainerName);
            var state = await WaitForRunningContainer(launcher.ContainerName);
            Assert.Equal(0, state.ExitCode);
            var ownedId = DockerWorkerOwnershipTestSupport.VerifiedId(state.Output, launcher.ContainerName, launcher.ContainerOwner!);
            using var container = JsonDocument.Parse(state.Output);
            var root = container.RootElement;
            Assert.True(root.GetProperty("State").GetProperty("Running").GetBoolean());
            Assert.Empty(root.GetProperty("Mounts").EnumerateArray());
            var configuration = root.GetProperty("HostConfig");
            Assert.Equal("none", configuration.GetProperty("NetworkMode").GetString());
            Assert.True(configuration.GetProperty("ReadonlyRootfs").GetBoolean());
            Assert.Equal(268435456, configuration.GetProperty("Memory").GetInt64());
            Assert.Equal(268435456, configuration.GetProperty("MemorySwap").GetInt64());
            Assert.Equal(1_000_000_000, configuration.GetProperty("NanoCpus").GetInt64());
            Assert.Equal(16, configuration.GetProperty("PidsLimit").GetInt64());
            Assert.Equal(new[] { "ALL" }, configuration.GetProperty("CapDrop").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(new[] { "no-new-privileges=true" }, configuration.GetProperty("SecurityOpt").EnumerateArray().Select(value => value.GetString()));
            var workerConfiguration = root.GetProperty("Config");
            Assert.Equal("65532:65532", workerConfiguration.GetProperty("User").GetString());
            var hostname = workerConfiguration.GetProperty("Hostname").GetString();
            Assert.All(workerConfiguration.GetProperty("Env").EnumerateArray(), value =>
                Assert.True(value.GetString() == "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
                    || value.GetString() == $"HOSTNAME={hostname}"));
            if (termination == Termination.Cancellation)
                cancellation.Cancel();
            else if (termination == Termination.WorkerDeath)
                Assert.Equal(0, (await ContainerCommand("kill", "--signal", "KILL", ownedId)).ExitCode);
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(12));
            var expected = termination switch
            {
                Termination.Cancellation => MappingOperationStatus.Canceled,
                Termination.Deadline => MappingOperationStatus.TimedOut,
                _ => MappingOperationStatus.BackendFailure
            };
            Assert.Equal(expected, result.Status);
            Assert.False(result.Provenance.Confirmed);
            var removed = await ContainerCommand("inspect", launcher.ContainerName);
            Assert.NotEqual(0, removed.ExitCode);
            var retry = await backend.InspectAsync(Artifact("valid-basic"));
            Assert.Equal(MappingOperationStatus.Succeeded, retry.Status);
        }
        finally
        {
            cancellation.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(12)); }
            finally
            {
                foreach (var worker in launcher.CreatedWorkers)
                {
                    var state = await ContainerCommand("inspect", "--format", DockerWorkerOwnershipTestSupport.InspectFormat, worker.Name);
                    if (state.ExitCode != 0)
                    {
                        Assert.Equal("", state.Output.Trim());
                        Assert.True(DockerWorkerOwnershipTestSupport.IsMissing(state.Error, worker.Name));
                        continue;
                    }
                    var id = DockerWorkerOwnershipTestSupport.VerifiedId(state.Output, worker.Name, worker.Owner);
                    Assert.Equal(0, (await ContainerCommand("rm", "--force", "--volumes", id)).ExitCode);
                    var absent = await ContainerCommand("inspect", id);
                    Assert.Equal("", absent.Output.Trim());
                    Assert.True(DockerWorkerOwnershipTestSupport.IsMissing(absent.Error, id));
                }
            }
        }
    }

    private static NativeArtifact Artifact(string fixture) => NativeArtifact.Admit(Scope,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json")), InspectionPolicy.MaximumNativeBytes);

    private static DockerInspectionWorkerOptions Options(TimeSpan? deadline = null) => new("/usr/bin/docker",
        Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!, deadline);

    private static async Task<(int ExitCode, string Output, string Error)> WaitForRunningContainer(string name)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var state = await ContainerCommand("inspect", "--format", "{{json .}}", name);
            if (state.ExitCode == 0)
            {
                using var parsed = JsonDocument.Parse(state.Output);
                if (parsed.RootElement.GetProperty("State").GetProperty("Running").GetBoolean())
                    return state;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The owned test worker did not reach its running state.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> ContainerCommand(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        process.StartInfo.ArgumentList.Add("--host");
        process.StartInfo.ArgumentList.Add("unix:///var/run/docker.sock");
        process.StartInfo.ArgumentList.Add("container");
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
        }
    }

    // Withhold EOF once so the real worker remains blocked on the protocol's
    // completion check. Daemon, image, container, pipes, and cleanup stay real.
    private sealed class HoldingInputLauncher : IWorkerProcessLauncher
    {
        private int held;
        public TaskCompletionSource InputWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ContainerName { get; private set; }
        public string? ContainerOwner { get; private set; }
        public List<(string Name, string Owner)> CreatedWorkers { get; } = [];

        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var name = Array.IndexOf(arguments, "--name");
            if (name >= 0)
            {
                ContainerName = arguments[name + 1];
                var label = arguments.Single(argument => argument.StartsWith(DockerWorkerOwnershipTestSupport.OperationLabel + "=", StringComparison.Ordinal));
                ContainerOwner = label[(DockerWorkerOwnershipTestSupport.OperationLabel.Length + 1)..];
                CreatedWorkers.Add((ContainerName, ContainerOwner));
            }
            var hold = arguments.Contains("--attach", StringComparer.Ordinal) && Interlocked.Exchange(ref held, 1) == 0;
            var process = new Process { StartInfo = startInfo };
            process.Start();
            return new TestProcess(process, hold ? new HeldInput(process.StandardInput.BaseStream, InputWritten) : process.StandardInput.BaseStream, hold);
        }
    }

    private sealed class TestProcess(Process process, Stream input, bool holdInputOpen) : IWorkerProcess
    {
        public Stream StandardInput => input;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public void CloseStandardInput() { if (!holdInputOpen) process.StandardInput.Dispose(); }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose() { process.StandardInput.Dispose(); process.Dispose(); }
    }

    private sealed class HeldInput(Stream inner, TaskCompletionSource written) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken) { await inner.FlushAsync(cancellationToken); written.TrySetResult(); }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public sealed class WorkerFactAttribute : FactAttribute
    {
        public WorkerFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")))
                Skip = "Build the restricted worker image and set WEFT_FERRULE_WORKER_IMAGE for live Docker qualification.";
        }
    }
}
