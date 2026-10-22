using System.Security.Cryptography;
using System.Text;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests;

/// <summary>
/// The Ferrule service's child-process runner against a real worker executable. The Rust sandbox qualification
/// independently proves that each control is enforced inside the child; these tests prove that the runner
/// verifies them before sending input, keeps exact bytes, and settles every child it starts.
/// </summary>
[Collection(FerruleServiceProcessCollection.Name)]
public sealed class FerruleServiceProcessRunnerTests
{
    [ServiceEquivalenceFact]
    public async Task ChildOutputsEqualTheDockerRunnerByteForByte()
    {
        var root = TemporaryRoot();
        try
        {
            await using var docker = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker",
                Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!));
            await using var dockerPreview = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker",
                Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!, profile: DockerWorkerProfile.Preview));
            await using var child = new ProcessWorkerRunner(Options(root));
            await using var childPreview = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Preview));
            var inspection = new ComparingRunner(docker, child);
            var preview = new ComparingRunner(dockerPreview, childPreview);
            var backend = new FerruleInspectionBackend(inspection);
            foreach (var fixture in Fixtures())
            {
                await backend.InspectAsync(Artifact(fixture));
                await backend.ValidateAsync(Artifact(fixture));
                await backend.ProjectAsync(Artifact(fixture));
            }

            var previews = new FerrulePreviewBackend(preview);
            var succeeded = await previews.PreviewAsync(Preview());
            Assert.Equal(MappingPreviewStatus.Succeeded, succeeded.Status);
            foreach (var request in new[]
            {
                Preview() with { NamedInputs = [] },
                Preview() with { Primary = new(null, "{\"id\":"u8) },
                Preview() with { Parameters = [new("region", PreviewParameterKind.String, "west")] },
                Preview() with { Limits = PreviewLimits.Default with { MaximumOutputBytes = 32 } },
                Preview("preview-csv-source", Order) with { NamedInputs = [], Parameters = [] },
                Preview("invalid-graph", Order) with { NamedInputs = [], Parameters = [] }
            })
                Assert.NotEqual(MappingPreviewStatus.Succeeded, (await previews.PreviewAsync(request)).Status);

            Assert.True(inspection.Compared >= 3 * Fixtures().Count());
            Assert.Equal(7, preview.Compared);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceWorkerFact]
    public async Task IdentityIsTheExactExecutableAndOperationsLeaveNoDirectoryOrChild()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessWorkerRunner(Options(root));
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Executable!))), runner.BuildIdentity);
            var backend = new FerruleInspectionBackend(runner);
            var validation = await backend.ValidateAsync(Artifact("invalid-function"));
            Assert.Equal(MappingOperationStatus.InvalidMapping, validation.Status);
            Assert.True(validation.Provenance.Confirmed);
            Assert.Equal(runner.BuildIdentity, validation.Provenance.BackendBuild);
            Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Owner is GraphNodeDiagnosticOwner
            {
                FunctionId: "18446744073709551615",
                NodeId: "7"
            });
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(WorkerChildren());
            Assert.True(runner.Available);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceWorkerFact]
    public async Task ConcurrentOperationsEachRunInTheirOwnChild()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessWorkerRunner(Options(root));
            var backend = new FerruleInspectionBackend(runner);
            var fixtures = Fixtures().ToArray();
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fixture in fixtures)
                expected[fixture] = Describe(await backend.ValidateAsync(Artifact(fixture)));
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 3).SelectMany(_ => fixtures).Select(async fixture =>
                (fixture, Describe(await backend.ValidateAsync(Artifact(fixture))))));
            Assert.All(concurrent, item => Assert.Equal(expected[item.fixture], item.Item2));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(WorkerChildren());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static string Describe(MappingValidationResult result) =>
            $"{result.Status}|{result.IsValid}|{result.TotalIssueCount}|{string.Join(';', result.Diagnostics.Select(d => d.Code + "@" + d.Location))}";
    }

    [ServiceWorkerFact]
    public Task WallDeadlineKillsALiveChildAndAdmitsTheNextOperation() => TerminationKillsChild(cancel: false);

    [ServiceWorkerFact]
    public Task CallerCancellationKillsALiveChildAndAdmitsTheNextOperation() => TerminationKillsChild(cancel: true);

    private static async Task TerminationKillsChild(bool cancel)
    {
        var root = TemporaryRoot();
        try
        {
            var frame = await InspectionFrameAsync();
            await using var held = new ProcessWorkerRunner(Options(root, deadline: TimeSpan.FromSeconds(cancel ? 10 : 2), withhold: true));
            using var cancellation = new CancellationTokenSource();
            var running = held.RunAsync(frame, cancellation.Token);
            var child = Assert.Single(await WaitForWorkerChildren(1, sandboxReady: true));
            // The worker is alive and blocked on the protocol's end-of-input check inside its sandbox.
            Assert.Contains("Seccomp:\t2", await File.ReadAllTextAsync($"/proc/{child}/status"), StringComparison.Ordinal);
            if (cancel) await cancellation.CancelAsync();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(cancel ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
            Assert.Empty(result.Output);
            Assert.False(Directory.Exists($"/proc/{child}"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.True(held.Available);

            await using var next = new ProcessWorkerRunner(Options(root));
            var answered = await next.RunAsync(frame, CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, answered.Failure);
            Assert.NotEmpty(answered.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceWorkerFact]
    public async Task StoppingTheRunnerCancelsAndJoinsLiveChildren()
    {
        var root = TemporaryRoot();
        try
        {
            var runner = new ProcessWorkerRunner(Options(root, withhold: true));
            var first = runner.RunAsync(await InspectionFrameAsync(), CancellationToken.None);
            var second = runner.RunAsync(await InspectionFrameAsync(), CancellationToken.None);
            var children = await WaitForWorkerChildren(2);
            await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkerProcessFailure.Canceled, (await first).Failure);
            Assert.Equal(WorkerProcessFailure.Canceled, (await second).Failure);
            Assert.All(children, child => Assert.False(Directory.Exists($"/proc/{child}")));
            Assert.False(runner.Available);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(await InspectionFrameAsync(), CancellationToken.None)).Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            await runner.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChildWithoutVerifiedControlsReceivesNothingAndSeesNoServiceState()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        var evidence = Directory.CreateTempSubdirectory("weft-service-evidence-").FullName;
        var script = Path.Combine(evidence, "unhardened-worker");
        try
        {
            // Announces the marker without applying any control; it records what a child would observe.
            await File.WriteAllTextAsync(script, $"""
                #!/bin/sh
                env > '{evidence}/environment'
                pwd > '{evidence}/directory'
                ls -A > '{evidence}/listing'
                printf '%s' "$*" > '{evidence}/arguments'
                printf WFS1
                cat > '{evidence}/input'
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("WEFT_SERVICE_TEST_SECRET", "must-not-reach-a-child");
            await using var runner = new ProcessWorkerRunner(new ProcessWorkerOptions(script, DockerWorkerProfile.Preview, root));
            var result = await runner.RunAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
            Assert.Empty(result.Output);
            Assert.DoesNotContain("WEFT_SERVICE_TEST_SECRET", await File.ReadAllTextAsync(Path.Combine(evidence, "environment")), StringComparison.Ordinal);
            var directory = (await File.ReadAllTextAsync(Path.Combine(evidence, "directory"))).TrimEnd('\n');
            Assert.Equal(root, Path.GetDirectoryName(directory));
            Assert.StartsWith("weft-ferrule-", Path.GetFileName(directory), StringComparison.Ordinal);
            Assert.Equal("", await File.ReadAllTextAsync(Path.Combine(evidence, "listing")));
            Assert.Equal("--service-sandbox --cpu-seconds=30 --memory-bytes=536870912 --open-files=64",
                await File.ReadAllTextAsync(Path.Combine(evidence, "arguments")));
            // The child was killed before any request byte was written.
            Assert.False(File.Exists(Path.Combine(evidence, "input")) && new FileInfo(Path.Combine(evidence, "input")).Length > 0);
            Assert.False(Directory.Exists(directory));
            Assert.True(runner.Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEFT_SERVICE_TEST_SECRET", null);
            Directory.Delete(root, recursive: true);
            Directory.Delete(evidence, recursive: true);
        }
    }

    [Fact]
    public async Task MissingWorkerIsAStartFailureAndDoesNotCloseAdmission()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        var evidence = Directory.CreateTempSubdirectory("weft-service-evidence-").FullName;
        var script = Path.Combine(evidence, "worker");
        try
        {
            await File.WriteAllTextAsync(script, "#!/bin/sh\nexit 3\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await using var runner = new ProcessWorkerRunner(new ProcessWorkerOptions(script, DockerWorkerProfile.Inspection, root));
            // A child that exits before its marker never received input and proved no sandbox.
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            File.Delete(script);
            Assert.Equal(WorkerProcessFailure.StartFailed, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Throws<ArgumentException>(() =>
                runner.RunAsync(new byte[DockerWorkerProfile.Inspection.MaximumRequestBytes + 1], CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(evidence, recursive: true);
        }
    }

    [Fact]
    public void OptionsAdmitOnlyServiceProfilesNormalizedPathsAndProfileDeadlines()
    {
        // The build profile is the generation stage of a compile operation; compilation runs in its own launcher.
        Assert.Equal(["--service-sandbox", "--cpu-seconds=120", "--memory-bytes=2147483648", "--open-files=256"],
            new ProcessWorkerOptions("/ferrule-host", DockerWorkerProfile.Build, "/tmp").Arguments);
        Assert.Throws<ArgumentException>(() => new ProcessWorkerOptions("/ferrule-host", DockerWorkerProfile.Compiled, "/tmp"));
        foreach (var path in new[] { "ferrule-host", "/a/../ferrule-host", "/a//b", "/a\\b", "/tmp/" })
            Assert.Throws<ArgumentException>(() => new ProcessWorkerOptions(path, DockerWorkerProfile.Inspection, "/tmp"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProcessWorkerOptions("/ferrule-host", DockerWorkerProfile.Inspection, "/tmp", TimeSpan.FromSeconds(11)));
        var options = new ProcessWorkerOptions("/ferrule-host", DockerWorkerProfile.Inspection, "/tmp");
        Assert.Equal(TimeSpan.FromSeconds(10), options.WallTimeout);
        Assert.Equal(["--service-sandbox", "--cpu-seconds=10", "--memory-bytes=268435456", "--open-files=64"], options.Arguments);
        Assert.Equal("WFS1", Encoding.ASCII.GetString(ProcessWorkerOptions.ReadyMarker));
    }

    /// <summary>A complete validation frame exactly as the inspection backend builds it.</summary>
    internal static async Task<byte[]> InspectionFrameAsync()
    {
        var capture = new CapturingRunner();
        await new FerruleInspectionBackend(capture).ValidateAsync(Artifact("valid-basic"));
        return capture.Frame!;
    }

    private sealed class CapturingRunner : IInspectionWorkerRunner
    {
        internal byte[]? Frame { get; private set; }
        public string BuildIdentity => "sha256:" + new string('1', 64);

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            Frame = requestFrame.ToArray();
            return Task.FromResult(new WorkerProcessResult([], WorkerProcessFailure.WorkerFailed));
        }
    }

    /// <summary>Runs every frame through both runners and requires identical outcomes and bytes.</summary>
    private sealed class ComparingRunner(IInspectionWorkerRunner expected, IInspectionWorkerRunner actual) : IInspectionWorkerRunner
    {
        internal int Compared { get; private set; }
        public string BuildIdentity => expected.BuildIdentity;

        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            var reference = await expected.RunAsync(requestFrame, cancellationToken);
            var candidate = await actual.RunAsync(requestFrame, cancellationToken);
            Assert.Equal(WorkerProcessFailure.None, reference.Failure);
            Assert.Equal(reference.Failure, candidate.Failure);
            Assert.Equal(reference.Output, candidate.Output);
            Compared++;
            return reference;
        }
    }
}
