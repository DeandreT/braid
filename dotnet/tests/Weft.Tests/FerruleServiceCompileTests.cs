using System.Security.Cryptography;
using System.Text;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests;

/// <summary>
/// The Ferrule service's compile runner against a real worker, the real compile-sandbox launcher and the pinned
/// SDK. The launcher's Rust qualification proves each control inside the compiler; these tests prove that the
/// runner verifies them before the compiler starts, produces the Docker compiler's exact replies, and settles every
/// child it starts at the deadline, on cancellation and on stop.
/// </summary>
[Collection(FerruleServiceProcessCollection.Name)]
public sealed class FerruleServiceCompileTests
{
    [ServiceCompileFact]
    public async Task CompileEmitsAVerifiedAssemblyUnderTheServiceIdentityAndLeavesNothing()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompileRunner(CompileOptions(root));
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Executable!))), runner.BuildIdentity);
            Assert.Matches("^sha256:[0-9a-f]{64}$", runner.ToolchainIdentity);
            Assert.True(await runner.ProbeAsync(CancellationToken.None));
            var (compiler, intent, native) = Compiler(runner, "valid-basic");
            var capability = await compiler.InspectAsync(intent);
            Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
            var artifact = (await compiler.BuildAsync(intent, new(native, native.Length))).Copy();
            Assert.True(artifact.Length > 1024);
            Assert.Equal("MZ", Encoding.ASCII.GetString(artifact, 0, 2));
            // The compiler is deterministic: a second operation in fresh children returns the same bytes.
            Assert.Equal(artifact, (await compiler.BuildAsync(intent, new(native, native.Length))).Copy());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
            Assert.True(runner.Available);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task InspectionsAndRefusalsAnswerWithoutCompiling()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompileRunner(CompileOptions(root));
            var inspected = await runner.RunAsync(BuildFrame("valid-basic", "inspect"), CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, inspected.Failure);
            var request = BuildWorkerProtocol.DecodeRequest(BuildFrame("valid-basic", "inspect")).Header;
            var reply = BuildWorkerProtocol.DecodeReply(inspected.Output, request);
            Assert.Equal("supported", reply.Header.Status);
            Assert.Empty(reply.Header.Files);
            var refused = await runner.RunAsync(BuildFrame("preview-csv-source", "compile"), CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, refused.Failure);
            Assert.Equal("unsupported", BuildWorkerProtocol.DecodeReply(refused.Output,
                BuildWorkerProtocol.DecodeRequest(BuildFrame("preview-csv-source", "compile")).Header).Header.Status);
            // An inadmissible request is a failed compile, as in the Docker compiler; no child starts.
            Assert.Equal(WorkerProcessFailure.WorkerFailed, (await runner.RunAsync(new byte[] { 0, 0, 0, 1, 7 }, CancellationToken.None)).Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [CompileEquivalenceFact]
    public async Task CompileRepliesEqualTheDockerCompilerByteForByte()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = new ProcessCompileRunner(CompileOptions(root));
            var compiled = 0;
            foreach (var fixture in Fixtures())
            {
                foreach (var operation in new[] { "compile", "inspect" })
                {
                    var frame = BuildFrame(fixture, operation);
                    var expected = await DockerCompilerAsync(frame);
                    var actual = await service.RunAsync(frame, CancellationToken.None);
                    Assert.Equal(WorkerProcessFailure.None, expected.Failure);
                    Assert.Equal(expected.Failure, actual.Failure);
                    Assert.Equal(expected.Output, actual.Output);
                    var reply = BuildWorkerProtocol.DecodeReply(actual.Output, BuildWorkerProtocol.DecodeRequest(frame).Header);
                    if (reply.Header.Files is [{ Path: BuildCompilation.Output }]) compiled++;
                }
            }

            Assert.True(compiled >= 2, "At least two fixtures must compile to artifacts.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public Task WallDeadlineKillsTheCompilerAndAdmitsTheNextOperation() => TerminationKillsCompiler(cancel: false);

    [ServiceCompileFact]
    public Task CallerCancellationKillsTheCompilerAndAdmitsTheNextOperation() => TerminationKillsCompiler(cancel: true);

    /// <summary>A held program in the compiler's place, under the same launcher and controls, runs until it is killed.</summary>
    private static async Task TerminationKillsCompiler(bool cancel)
    {
        var root = TemporaryRoot();
        try
        {
            await using var held = new ProcessCompileRunner(CompileOptions(root, TimeSpan.FromSeconds(cancel ? 60 : 8), ["/usr/bin/sleep", "300"]));
            using var cancellation = new CancellationTokenSource();
            var running = held.RunAsync(BuildFrame("valid-basic", "compile"), cancellation.Token);
            var child = await WaitForHeldCompiler();
            if (cancel) await cancellation.CancelAsync();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(cancel ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
            Assert.Empty(result.Output);
            Assert.False(Directory.Exists($"/proc/{child}"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
            Assert.True(held.Available);
            Assert.Equal(WorkerProcessFailure.None, (await held.RunAsync(BuildFrame("valid-basic", "inspect"), CancellationToken.None)).Failure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task CancellationDuringGenerationKillsTheWorkerBeforeAnyCompilation()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompileRunner(CompileOptions(root, withholdGeneration: true));
            using var cancellation = new CancellationTokenSource();
            var running = runner.RunAsync(BuildFrame("valid-basic", "compile"), cancellation.Token);
            var worker = Assert.Single(await WaitForWorkerChildren(1, sandboxReady: true));
            await cancellation.CancelAsync();
            Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(30))).Failure);
            Assert.False(Directory.Exists($"/proc/{worker}"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
            Assert.True(runner.Available);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task StoppingTheRunnerCancelsAndJoinsALiveCompiler()
    {
        var root = TemporaryRoot();
        try
        {
            var runner = new ProcessCompileRunner(CompileOptions(root, command: ["/usr/bin/sleep", "300"]));
            var running = runner.RunAsync(BuildFrame("valid-basic", "compile"), CancellationToken.None);
            var child = await WaitForHeldCompiler();
            await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WorkerProcessFailure.Canceled, (await running).Failure);
            Assert.False(Directory.Exists($"/proc/{child}"));
            Assert.False(runner.Available);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(BuildFrame("valid-basic", "compile"), CancellationToken.None)).Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            await runner.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LauncherWithoutVerifiedControlsNeverStartsTheCompiler()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        using var installation = new FakeInstallation();
        try
        {
            // Announces the marker without applying any control and records whatever it would be sent.
            var launcher = installation.Script("launcher", $"""
                #!/bin/sh
                env > '{installation.Directory}/environment'
                printf WFC1
                cat > '{installation.Directory}/go'
                """);
            Environment.SetEnvironmentVariable("WEFT_SERVICE_TEST_SECRET", "must-not-reach-a-child");
            await using var runner = new ProcessCompileRunner(new ProcessCompileOptions(launcher, launcher, root, installation.Toolchain));
            Assert.False(await runner.ProbeAsync(CancellationToken.None));
            // The launcher was killed without the start byte, before or after it began to wait for it.
            var go = new FileInfo(Path.Combine(installation.Directory, "go"));
            Assert.True(!go.Exists || go.Length == 0);
            var environment = await File.ReadAllTextAsync(Path.Combine(installation.Directory, "environment"));
            Assert.DoesNotContain("WEFT_SERVICE_TEST_SECRET", environment, StringComparison.Ordinal);
            Assert.Contains("DOTNET_GCHeapHardLimit=0x80000000", environment, StringComparison.Ordinal);
            // An inadmissible frame is refused before any child starts.
            Assert.Equal(WorkerProcessFailure.WorkerFailed, (await runner.RunAsync(new byte[] { 1, 2, 3 }, CancellationToken.None)).Failure);
            Assert.Throws<ArgumentException>(() =>
                runner.RunAsync(new byte[DockerWorkerProfile.Build.MaximumRequestBytes + 1], CancellationToken.None).GetAwaiter().GetResult());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.True(runner.Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEFT_SERVICE_TEST_SECRET", null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OptionsPinTheLauncherLimitsGrantsAndCompilerEnvironment()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var installation = new FakeInstallation();
        var options = new ProcessCompileOptions("/ferrule-host", "/weft-compile-sandbox", "/tmp", installation.Toolchain);
        Assert.Equal(DockerWorkerProfile.Build, options.Generation.Profile);
        Assert.Equal(TimeSpan.FromSeconds(120), options.WallTimeout);
        Assert.Equal(["--service-sandbox", "--cpu-seconds=120", "--memory-bytes=2147483648", "--open-files=256"], options.Generation.Arguments);
        var grants = ProcessCompileOptions.ToolchainGrants.Resolve(options);
        var arguments = options.LauncherArguments("/tmp/weft-compile-1", grants, output: true);
        Assert.Equal(["--cpu-seconds=120", "--memory-bytes=6442450944", "--open-files=256", "--file-bytes=67108864", "--output=Ferrule.Generated.dll",
            "--read=" + installation.Toolchain], arguments.Take(6));
        Assert.Equal(["--", installation.Toolchain + "/dotnet", "exec", installation.Toolchain + "/sdk/10.0.112/Roslyn/bincore/csc.dll", "-noconfig",
            "@/tmp/weft-compile-1/compiler.rsp"], arguments.TakeLast(6));
        Assert.Single(arguments, argument => argument.StartsWith("--execute=/", StringComparison.Ordinal) && argument.Contains("ld-linux", StringComparison.Ordinal));
        var toolchain = installation.Toolchain;
        Assert.All(arguments.Where(argument => argument.StartsWith("--read=", StringComparison.Ordinal)), argument =>
            Assert.True(argument == "--read=" + toolchain || argument == "--read=/dev/urandom" ||
                argument is "--read=/lib" or "--read=/lib64" or "--read=/usr/lib" or "--read=/usr/lib64", argument));
        Assert.DoesNotContain("--output=Ferrule.Generated.dll", options.LauncherArguments("/tmp/weft-compile-1", grants, output: false));
        var environment = options.Environment("/tmp/weft-compile-1");
        Assert.Equal("0x80000000", environment["DOTNET_GCHeapHardLimit"]);
        Assert.Equal("0x80000000", environment["DOTNET_GCRegionRange"]);
        Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal("Disable", environment["DOTNET_ROLL_FORWARD"]);
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal("/tmp/weft-compile-1", environment["TMPDIR"]);
        Assert.Equal(installation.Toolchain, environment["DOTNET_ROOT"]);
        Assert.Throws<ArgumentException>(() => new ProcessCompileOptions("/ferrule-host", "weft-compile-sandbox", "/tmp"));
        Assert.Throws<ArgumentException>(() => new ProcessCompileOptions("/ferrule-host", "/weft-compile-sandbox", "/tmp", "/usr/share/../dotnet"));
        Assert.Throws<ArgumentException>(() => new ProcessCompileOptions("/ferrule-host", "/weft-compile-sandbox", "/tmp")
        {
            Generation = new ProcessWorkerOptions("/ferrule-host", DockerWorkerProfile.Preview, "/tmp")
        });
        Assert.Equal("WFC1", Encoding.ASCII.GetString(ProcessCompileOptions.ReadyMarker));
    }

    /// <summary>
    /// The Docker compiler's reply to one frame from a fresh runner. Under heavy host load a container's removal can
    /// exceed the runner's fixed cleanup bound; that is the reference runner's own failure, not an answer, so the
    /// frame is repeated with another fresh runner, at most three times.
    /// </summary>
    internal static async Task<WorkerProcessResult> DockerCompilerAsync(byte[] frame)
    {
        for (var attempt = 1; ; attempt++)
        {
            var docker = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker",
                Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE")!, profile: DockerWorkerProfile.Build));
            var result = await docker.RunAsync(frame, CancellationToken.None);
            try { await docker.DisposeAsync(); }
            catch (InvalidOperationException) when (result.Failure == WorkerProcessFailure.CleanupFailed) { }
            if (result.Failure != WorkerProcessFailure.CleanupFailed || attempt == 3) return result;
        }
    }

    /// <summary>The first held program (the compiler's stand-in) running under every launcher control.</summary>
    private static async Task<int> WaitForHeldCompiler()
    {
        for (var attempt = 0; attempt < 1200; attempt++)
        {
            foreach (var pid in Children("sleep"))
            {
                try
                {
                    var status = await File.ReadAllLinesAsync($"/proc/{pid}/status");
                    if (status.Contains("Seccomp:\t2", StringComparer.Ordinal) && status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal)) return pid;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The held compiler did not start.");
    }

    internal static (FerruleBuildCompiler Compiler, BuildIntent Intent, byte[] Native) Compiler(IInspectionWorkerRunner runner, string fixture)
    {
        var native = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json"));
        var actor = new ActorContext(new(Guid.NewGuid()));
        var reference = new PrivateObjectReference(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(native), native.Length);
        var source = new NativeSource(actor, reference, native);
        var compiler = new FerruleBuildCompiler(runner, new(actor, source, source));
        var intent = new BuildIntent(Guid.NewGuid(), Scope, reference.Digest, compiler.Backend, FerruleBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
            new(new(Scope.OrganizationId, Scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        return (compiler, intent, native);
    }

    private sealed class NativeSource(ActorContext actor, PrivateObjectReference reference, byte[] bytes) : IBuildAdmissionReader, IPrivateObjectStore
    {
        public Task<BuildAdmission?> ReadAsync(ActorContext expected, BuildIntent intent, CancellationToken token = default) =>
            Task.FromResult<BuildAdmission?>(new(actor.PrincipalId, intent.Fingerprint, reference));

        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference value, CancellationToken token = default) => Task.FromResult(bytes.ToArray());

        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference value, Stream source, CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A disposable toolchain layout whose host is a real ELF executable; nothing in it compiles.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal sealed class FakeInstallation : IDisposable
    {
        internal FakeInstallation()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("weft-compile-installation-").FullName;
            Toolchain = Path.Combine(Directory, "dotnet-root");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(BuildCompilation.Compiler(Toolchain))!);
            System.IO.Directory.CreateDirectory(BuildCompilation.References(Toolchain));
            var host = Path.Combine(Toolchain, "host/fxr/pinned");
            var runtime = Path.Combine(Toolchain, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion);
            System.IO.Directory.CreateDirectory(host);
            System.IO.Directory.CreateDirectory(runtime);
            File.WriteAllBytes(Path.Combine(host, "libhostfxr.so"), [1]);
            File.WriteAllBytes(Path.Combine(runtime, "System.Private.CoreLib.dll"), [1]);
            File.WriteAllBytes(BuildCompilation.Compiler(Toolchain), [1]);
            File.Copy("/usr/bin/true", BuildCompilation.Host(Toolchain));
        }

        internal string Directory { get; }
        internal string Toolchain { get; }

        internal string Script(string name, string text)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
