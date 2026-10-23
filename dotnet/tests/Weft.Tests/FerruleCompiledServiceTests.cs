using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests;

[Collection(FerruleServiceProcessCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class FerruleCompiledServiceTests
{
    internal static string Assembly => Path.Combine(AppContext.BaseDirectory, "CompiledRuntime/weft-compiled-runtime.dll");
    internal static ProcessCompiledRuntimeOptions Options(string root, TimeSpan? wall = null) =>
        new(PackagedFerruleService.Identity, Assembly, Launcher!, root, Toolchain, wall);

    [ServiceCompileFact]
    public async Task The_runtime_identity_command_proves_the_installed_runtime_without_a_listener()
    {
        var root = TemporaryRoot();
        try
        {
            await using var expected = new ProcessCompiledRuntimeRunner(Options(root));
            using var process = PackagedFerruleService.Launch(new Dictionary<string, string>
            {
                [Weft.FerruleService.FerruleServiceSettings.WorkerExecutableSetting] = Executable!,
                [Weft.FerruleService.FerruleServiceSettings.TemporaryRootSetting] = root,
                [Weft.FerruleService.FerruleServiceSettings.RuntimeAssemblySetting] = Assembly,
                [Weft.FerruleService.FerruleServiceSettings.RuntimeLauncherSetting] = Launcher!,
                [Weft.FerruleService.FerruleServiceSettings.RuntimeRootSetting] = Toolchain
            }, "runtime-identity");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(70)); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
            using var document = System.Text.Json.JsonDocument.Parse(await stdout);
            Assert.Equal(expected.RuntimeIdentity, document.RootElement.GetProperty("runtime").GetString());
            Assert.Equal(expected.BuildIdentity, document.RootElement.GetProperty("native").GetString());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileFact]
    public async Task Managed_code_cannot_escape_the_read_only_networkless_process_boundary()
    {
        var root = TemporaryRoot();
        try
        {
            var program = Path.Combine(root, "program"); Directory.CreateDirectory(program);
            var operations = Path.Combine(root, "operations"); Directory.CreateDirectory(operations);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "CompiledSandboxProbe")))
                File.Copy(file, Path.Combine(program, Path.GetFileName(file)));
            var assembly = Path.Combine(program, "Weft.CompiledSandboxProbe.dll");
            var mode = File.GetUnixFileMode(assembly);
            var secret = Path.Combine(root, "not-granted.txt"); File.WriteAllText(secret, "synthetic private fixture");
            await using var runner = new ProcessCompiledRuntimeRunner(new(PackagedFerruleService.Identity, assembly, Launcher!, operations, Toolchain));
            var previous = Environment.GetEnvironmentVariable("WEFT_RUNTIME_PROBE_SECRET");
            Environment.SetEnvironmentVariable("WEFT_RUNTIME_PROBE_SECRET", "must-not-be-inherited");
            WorkerProcessResult result;
            try { result = await runner.RunAsync(System.Text.Encoding.UTF8.GetBytes(secret), default); }
            finally { Environment.SetEnvironmentVariable("WEFT_RUNTIME_PROBE_SECRET", previous); }
            Assert.Equal(WorkerProcessFailure.None, result.Failure);
            var text = System.Text.Encoding.UTF8.GetString(result.Output).Replace("\r", "", StringComparison.Ordinal);
            Assert.Equal("outside-read=denied\nprivate-write=denied\nmetadata=denied\nnetwork=denied\nprocess=denied\nsignal-owner=denied\nenvironment=clean\n", text);
            Assert.Equal(mode, File.GetUnixFileMode(assembly));
            Assert.Empty(Directory.EnumerateFileSystemEntries(operations));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileFact]
    public async Task The_read_only_child_executes_real_compiled_bytes_and_recovers_after_invalid_input()
    {
        var root = TemporaryRoot();
        try
        {
            await using var compilerRunner = new ProcessCompileRunner(CompileOptions(root));
            var (compiler, intent, native) = FerruleServiceCompileTests.Compiler(compilerRunner, "valid-basic");
            var capability = await compiler.InspectAsync(intent);
            var bytes = await compiler.BuildAsync(intent, new(native, native.Length));
            var artifact = BuildArtifactReference.Create(intent, capability, bytes.Digest, bytes.Length);
            await using var runner = new ProcessCompiledRuntimeRunner(Options(root));
            using var runtimeConfig = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(Assembly, ".runtimeconfig.json")));
            Assert.Equal(BuildWorkerProtocol.RuntimeVersion, runtimeConfig.RootElement.GetProperty("runtimeOptions").GetProperty("framework").GetProperty("version").GetString());
            Assert.True(await runner.ProbeAsync(default));
            var runtime = new FerruleCompiledBuildRuntime(new RuntimeIdentityRunner(runner), compiler.Backend);
            var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-22T00:00:00Z", PreviewLimits.Default,
                new(null, "{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}"u8), [], []);
            var result = await runtime.ExecuteAsync(artifact, bytes, replay);
            Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
            Assert.Equal("{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n"u8.ToArray(), Assert.Single(result.Outputs!.Outputs).CopyBytes());
            using var certificate = InspectionTransportTests.TransportCase.Certificate("CN=Compiled service");
            using var client = InspectionTransportTests.TransportCase.Certificate("CN=Compiled caller");
            await using var server = new WorkerTransportServer(new(new(System.Net.IPAddress.Loopback, 0), certificate,
                [InspectionTransportTests.TransportCase.Pin(client)])
            {
                ServiceRoutes = true,
                CompiledRuntimeIdentity = runner.RuntimeIdentity
            }, new Dictionary<string, IInspectionWorkerRunner> { [FerruleServiceProtocol.CompiledProfile] = runner });
            await server.StartAsync(default);
            await using var remote = new FerruleServiceRunner(new(new PinnedServiceConnector(new(
                new Uri("https://127.0.0.1:" + server.Port), InspectionTransportTests.TransportCase.Pin(certificate), client)),
                FerruleServiceProtocol.CompiledProfile, runner.BuildIdentity, compiledRuntimeIdentity: runner.RuntimeIdentity));
            var throughService = await new FerruleCompiledBuildRuntime(remote, compiler.Backend).ExecuteAsync(artifact, bytes, replay);
            Assert.Equal(CompiledBuildOutcome.Completed, throughService.Outcome);
            Assert.Equal(result.Outputs.Outputs[0].CopyBytes(), throughService.Outputs!.Outputs[0].CopyBytes());
            await server.StopAsync(default);
            Assert.Equal(WorkerProcessFailure.WorkerFailed, (await runner.RunAsync(new byte[] { 0 }, default)).Failure);
            Assert.Equal(CompiledBuildOutcome.Completed, (await runtime.ExecuteAsync(artifact, bytes, replay)).Outcome);
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileFact]
    public async Task The_startup_probe_requires_the_runtime_host_itself_to_start_and_refuse_an_empty_frame()
    {
        var root = TemporaryRoot();
        try
        {
            await using (var runtime = new ProcessCompiledRuntimeRunner(Options(root))) Assert.True(await runtime.ProbeAsync(default));
            // Another program behind the same launcher and controls starts, but it is not the runtime host.
            var other = Path.Combine(AppContext.BaseDirectory, "CompiledSandboxProbe", "Weft.CompiledSandboxProbe.dll");
            await using (var probe = new ProcessCompiledRuntimeRunner(new(PackagedFerruleService.Identity, other, Launcher!, root, Toolchain)))
            {
                Assert.False(await probe.ProbeAsync(default));
                Assert.True(probe.Available);
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_and_cancellation_kill_and_reap_the_actual_runtime(bool cancel)
    {
        var root = TemporaryRoot();
        try
        {
            var options = new ProcessCompiledRuntimeOptions(PackagedFerruleService.Identity, Assembly, Launcher!, root, Toolchain,
                TimeSpan.FromSeconds(cancel ? 20 : 5))
            { WithholdEndOfInput = true };
            await using var runner = new ProcessCompiledRuntimeRunner(options);
            using var cancellation = new CancellationTokenSource();
            var pending = runner.RunAsync(new byte[] { 0 }, cancellation.Token);
            int child = 0;
            for (var attempt = 0; attempt < 200 && child == 0; attempt++)
            {
                child = Children("dotnet").FirstOrDefault();
                if (child == 0) await Task.Delay(10);
            }
            Assert.NotEqual(0, child);
            if (cancel) cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(cancel ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
            Assert.Empty(result.Output);
            Assert.False(Directory.Exists("/proc/" + child));
            Assert.True(runner.Available);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileFact]
    public void Runtime_identity_binds_the_entry_point_host_and_launcher_independently_of_the_native_emitter()
    {
        var root = TemporaryRoot();
        try
        {
            var runtime = Path.Combine(root, "app"); var dotnet = Path.Combine(root, "sdk");
            Directory.CreateDirectory(runtime);
            Directory.CreateDirectory(Path.Combine(dotnet, "host/fxr/pinned"));
            Directory.CreateDirectory(Path.Combine(dotnet, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion));
            var files = new[] { Path.Combine(runtime, "first.dll"), Path.Combine(runtime, "second.dll"), Path.Combine(dotnet, "dotnet"),
                Path.Combine(dotnet, "host/fxr/pinned/host"), Path.Combine(dotnet, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion, "runtime"),
                Path.Combine(root, "launcher") };
            foreach (var file in files) File.WriteAllBytes(file, [1]);
            string Identity(string entry, string native) => ProcessCompiledRuntimeRunner.ComputeIdentity(new(native, entry, files[^1], root, dotnet), [], []);
            var first = Identity(files[0], PackagedFerruleService.Identity);
            Assert.Equal(first, Identity(files[0], "sha256:" + new string('a', 64)));
            Assert.NotEqual(first, Identity(files[1], PackagedFerruleService.Identity));
            foreach (var file in files)
            {
                File.WriteAllBytes(file, [2]);
                Assert.NotEqual(first, Identity(files[0], PackagedFerruleService.Identity));
                File.WriteAllBytes(file, [1]);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Every_granted_interpreter_and_system_library_the_runtime_can_load_affects_its_identity()
    {
        var root = Directory.CreateTempSubdirectory("weft-runtime-libraries-").FullName;
        try
        {
            var runtime = Path.Combine(root, "runtime");
            var dotnet = Path.Combine(root, "dotnet");
            var framework = Path.Combine(dotnet, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion);
            var system = Path.Combine(root, "lib");
            var multiarch = Path.Combine(system, "x86_64-linux-gnu");
            var launcher = Path.Combine(root, "launcher");
            var interpreter = Path.Combine(system, "ld-linux-x86-64.so.2");
            void Write(string path, byte[] content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }

            Write(Path.Combine(runtime, "weft-compiled-runtime.dll"), "entry"u8.ToArray());
            Write(Path.Combine(dotnet, "dotnet"), CompileWorkerIdentityTests.Elf(["libdl.so.2"]));
            Write(Path.Combine(dotnet, "host/fxr/version/libhostfxr.so"), CompileWorkerIdentityTests.Elf(["libstdc++.so.6"]));
            Write(Path.Combine(framework, "libcoreclr.so"), CompileWorkerIdentityTests.Elf(["libc.so.6"]));
            // Math resolves to libm, so outputs can depend on its bytes.
            Write(Path.Combine(framework, "libSystem.Native.so"), CompileWorkerIdentityTests.Elf(["libm.so.6"]));
            Write(launcher, CompileWorkerIdentityTests.Elf(["libgcc_s.so.1"]));
            Write(interpreter, "interpreter"u8.ToArray());
            Write(Path.Combine(multiarch, "libstdc++.so.6"), CompileWorkerIdentityTests.Elf(["libm.so.6"]));
            Write(Path.Combine(multiarch, "libdl.so.2"), "dl"u8.ToArray());
            Write(Path.Combine(multiarch, "libc.so.6"), "c"u8.ToArray());
            Write(Path.Combine(system, "libm.so.6"), "m"u8.ToArray());
            Write(Path.Combine(system, "libgcc_s.so.1"), "gcc"u8.ToArray());
            Write(Path.Combine(multiarch, "libssl.so.3"), CompileWorkerIdentityTests.Elf(["libcrypto.so.3"]));
            Write(Path.Combine(multiarch, "libcrypto.so.3"), "crypto"u8.ToArray());
            Write(Path.Combine(multiarch, "libunrelated.so.1"), "unrelated"u8.ToArray());
            var options = new ProcessCompiledRuntimeOptions(PackagedFerruleService.Identity, Path.Combine(runtime, "weft-compiled-runtime.dll"),
                launcher, root, dotnet);
            string Identity() => ProcessCompiledRuntimeRunner.ComputeIdentity(options, [interpreter], [system]);

            var original = Identity();
            Assert.Equal(original, Identity());
            foreach (var library in new[] { "ld-linux-x86-64.so.2", "x86_64-linux-gnu/libstdc++.so.6", "x86_64-linux-gnu/libdl.so.2",
                         "x86_64-linux-gnu/libc.so.6", "libm.so.6", "libgcc_s.so.1", "x86_64-linux-gnu/libssl.so.3", "x86_64-linux-gnu/libcrypto.so.3" })
            {
                var path = Path.Combine(system, library);
                var content = File.ReadAllBytes(path);
                File.WriteAllBytes(path, [.. content, 0]);
                Assert.NotEqual(original, Identity());
                File.WriteAllBytes(path, content);
                Assert.Equal(original, Identity());
            }

            // An unrelated library does not count; another copy the loader may pick does.
            File.WriteAllText(Path.Combine(multiarch, "libunrelated.so.1"), "changed");
            Assert.Equal(original, Identity());
            var shadow = Path.Combine(multiarch, "libm.so.6");
            Write(shadow, "shadow"u8.ToArray());
            Assert.NotEqual(original, Identity());
            File.Delete(shadow);
            Assert.Equal(original, Identity());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RuntimeIdentityRunner(ProcessCompiledRuntimeRunner inner) : IInspectionWorkerRunner
    {
        public string BuildIdentity => inner.RuntimeIdentity;
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken) => inner.RunAsync(requestFrame, cancellationToken);
    }
}

public sealed class ServiceCompileTheoryAttribute : TheoryAttribute
{
    public ServiceCompileTheoryAttribute()
    {
        var fact = new ServiceCompileFactAttribute();
        Skip = fact.Skip;
    }
}
