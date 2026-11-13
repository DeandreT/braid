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
public sealed class FerruleCompiledServiceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static string Assembly => Path.Combine(AppContext.BaseDirectory, "CompiledRuntime/weft-compiled-runtime.dll");
    internal static ProcessCompiledRuntimeOptions Options(string root, TimeSpan? wall = null) =>
        new(PackagedFerruleService.Identity, Assembly, Launcher!, root, Toolchain, wall);

    private static long FixedCleanupTicks()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ProcessWorkerOptions.KillTimeout);
        return checked(2L * System.Diagnostics.Stopwatch.Frequency);
    }

    [Fact]
    public async Task An_early_timer_timeout_retries_only_the_same_absolute_deadline()
    {
        var work = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + FixedCleanupTicks();
        var intervals = new List<TimeSpan>();
        var settled = await ProcessCompiledRuntimeRunner.WithinDeadlineAsync(work.Task, deadline, (actual, remaining) =>
        {
            Assert.Same(work.Task, actual);
            intervals.Add(remaining);
            if (intervals.Count == 1)
            {
                Assert.True(System.Diagnostics.Stopwatch.GetTimestamp() < deadline);
                return Task.FromException<bool>(new TimeoutException("Deterministic early timer wake."));
            }
            work.TrySetResult(true);
            return actual;
        });
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { phase = "forced-early-timeout", deadline, intervals, settled }));
        Assert.True(settled);
        Assert.Collection(intervals,
            first => Assert.True(first > TimeSpan.Zero && first <= TimeSpan.FromSeconds(2)),
            second => Assert.True(second >= TimeSpan.Zero && second <= intervals[0]));
    }

    [Fact]
    public async Task An_expired_absolute_deadline_does_not_start_another_wait()
    {
        var work = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() - 1;
        Assert.False(await ProcessCompiledRuntimeRunner.WithinDeadlineAsync(work.Task, deadline, (actual, _) =>
        {
            calls++; return actual;
        }));
        Assert.Equal(0, calls);
        Assert.False(work.Task.IsCompleted);
    }

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
            Assert.True(result.Failure == WorkerProcessFailure.None,
                "Compiled launcher admission: " + ChildAdmissionDiagnostics.Category(result.AdmissionFailure));
            Assert.Equal(WorkerProcessFailure.None, result.Failure);
            var text = System.Text.Encoding.UTF8.GetString(result.Output).Replace("\r", "", StringComparison.Ordinal);
            Assert.Equal("outside-read=denied\nprivate-write=denied\nmetadata=denied\nnetwork=denied\nprocess=denied\nsignal-owner=denied\nenvironment=clean\nprivacy=sealed\n", text);
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
            await using (var runtime = new ProcessCompiledRuntimeRunner(Options(root)))
            {
                var probe = await runtime.ProbeResultAsync(default);
                Assert.True(probe.Failure == WorkerProcessFailure.None,
                    "Compiled runtime probe admission: " + ChildAdmissionDiagnostics.Category(probe.AdmissionFailure));
            }
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
        await using var held = await HeldRuntime.StartAsync(wall: TimeSpan.FromSeconds(cancel ? 20 : 5));
        Assert.True(held.WitnessAt < held.Operation.WallTargetAt);
        if (cancel) await held.CancelCallerAsync();
        var returned = await held.RunningCall.Observed;
        RecordCleanup("observed-independent-normal-invocation-return", held);
        Assert.Null(returned.Error);
        var result = await held.Running;
        Assert.Equal(cancel ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
        Assert.Empty(result.Output);
        Assert.Equal(0, held.Runner.StopDeadline); // Stop is not involved in invocation cleanup.
        AssertIndependentCleanupAnchor(held, cancel);
        Assert.True(held.Operation.CleanupDecisionAt > 0 && held.Operation.CleanupDecisionAt <= held.Operation.Deadline);
        Assert.True(returned.At <= held.Operation.Deadline);
        Assert.True(held.Operation.CompletedAt > 0 && held.Operation.CompletedAt <= held.Operation.Deadline);
        Assert.False(Directory.Exists("/proc/" + held.Pid));
        AssertSettled(held);
        Assert.True(held.Runner.Available);
        Assert.Empty(Directory.EnumerateFileSystemEntries(held.Root));
        await AssertEveryAdmittedInvocationTimely(held);
        RecordCleanup(cancel ? "normal-caller-cancellation" : "normal-wall-expiry", held);
    }

    [ServiceCompileTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_and_Dispose_join_the_actual_inflight_runtime(bool dispose)
    {
        await using var held = await HeldRuntime.StartAsync();
        var completion = ObserveStop(held, dispose);
        Assert.False(held.Runner.Available);
        var returned = await completion.Observed;
        RecordCleanup("observed-normal-Stop-or-Dispose-return", held);
        Assert.Null(returned.Error);
        Assert.True(returned.At <= held.Runner.StopDeadline);
        Assert.True(held.Runner.StopDecisionAt <= held.Runner.StopDeadline);
        Assert.True(held.Running.IsCompleted);
        var result = await held.Running;
        Assert.Equal(WorkerProcessFailure.Canceled, result.Failure);
        Assert.Empty(result.Output);
        Assert.False(Directory.Exists("/proc/" + held.Pid));
        Assert.True(await held.Operation.Work!);
        Assert.True(held.Operation.CompletedAt <= held.Operation.Deadline);
        Assert.True(held.Runner.StopCompletedAt > 0 && held.Runner.StopCompletedAt <= held.Runner.StopDeadline);
        Assert.Equal(held.Runner.StopDeadline, held.Operation.Deadline);
        await AssertEveryAdmittedInvocationTimely(held);
        AssertSettled(held);
        Assert.Empty(Directory.EnumerateFileSystemEntries(held.Root));
        await AssertAdmissionClosed(held);
        await held.Runner.StopAsync();
        await held.Runner.DisposeAsync();
        Assert.True(CleanupTimely(held));
        RecordCleanup("timely-live-" + (dispose ? "dispose" : "stop"), held);
    }

    [ServiceCompileTheory]
    [InlineData((int)CompiledRuntimeCleanupStage.Handles)]
    [InlineData((int)CompiledRuntimeCleanupStage.Directory)]
    public async Task A_held_actual_runtime_cleanup_stage_expires_one_aggregate_budget(int stageValue)
    {
        var stage = (CompiledRuntimeCleanupStage)stageValue;
        var entered = Signal(); var release = Signal();
        var held = await HeldRuntime.StartAsync(before: current =>
        {
            if (current != stage) return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        }, releaseOnFailure: () => release.TrySetResult());
        held.ExpectFailure = true;
        try
        {
            var stopping = ObserveStop(held);
            await entered.Task;
            await AssertExpiredStop(stopping, held);
            var result = await held.Running;
            Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
            Assert.Empty(result.Output);
            Assert.False(held.Operation.Work!.IsCompleted);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            Assert.Equal(held.Runner.StopDeadline, held.Operation.Deadline);
            Assert.All(held.Operation.Steps, step => Assert.Equal(held.Operation.Deadline, step.Deadline));
            await AssertAdmissionClosed(held);
            RecordCleanup("expired-held-" + stage, held);
        }
        finally
        {
            release.TrySetResult();
            await held.DisposeAsync();
            RecordCleanup("separate-eventual-" + stage, held);
        }
        Assert.True(held.Operation.CompletedAt > held.Operation.Deadline);
        Assert.True(held.Runner.StopCompletedAt > held.Runner.StopDeadline);
        AssertSettled(held);
        await AssertAdmissionClosed(held);
        await Assert.ThrowsAsync<InvalidOperationException>(() => held.Runner.DisposeAsync().AsTask());
    }

    [ServiceCompileFact]
    public async Task Successive_accounted_stages_keep_the_original_deadline_without_a_new_wait()
    {
        var atDirectory = Signal(); var release = Signal();
        var held = await HeldRuntime.StartAsync(before: async stage =>
        {
            if (stage is CompiledRuntimeCleanupStage.Process or CompiledRuntimeCleanupStage.Pipes)
                await Task.Delay(ProcessWorkerOptions.KillTimeout / 3);
            if (stage == CompiledRuntimeCleanupStage.Directory)
            {
                atDirectory.TrySetResult(); await release.Task;
            }
        }, releaseOnFailure: () => release.TrySetResult());
        held.ExpectFailure = true;
        try
        {
            var stopping = ObserveStop(held);
            await atDirectory.Task;
            var steps = held.Operation.Steps;
            Assert.Equal(Enum.GetValues<CompiledRuntimeCleanupStage>(), steps.Select(step => step.Stage));
            Assert.All(steps, step => Assert.Equal(held.Runner.StopDeadline, step.Deadline));
            Assert.True(steps[^1].Remaining > TimeSpan.Zero);
            Assert.True(steps[^1].Remaining < ProcessWorkerOptions.KillTimeout / 2);
            Assert.True(steps[2].Remaining < steps[0].Remaining);
            await AssertExpiredStop(stopping, held);
            Assert.Equal(WorkerProcessFailure.CleanupFailed, (await held.Running).Failure);
            Assert.False(held.Operation.Work!.IsCompleted);
            Assert.True(held.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            RecordCleanup("expired-successive-stages", held);
        }
        finally
        {
            release.TrySetResult();
            await held.DisposeAsync();
            RecordCleanup("separate-eventual-successive-stages", held);
        }
        Assert.True(await held.Operation.Work!);
        Assert.True(held.Operation.CompletedAt > held.Operation.Deadline);
        await AssertAdmissionClosed(held);
    }

    [ServiceCompileFact]
    public async Task A_held_IO_cancellation_callback_does_not_delay_killing_the_actual_runtime()
    {
        var entered = Signal(); using var release = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        var held = await HeldRuntime.StartAsync(observe: operation =>
            registration = operation.Io.Token.Register(() =>
            {
                entered.TrySetResult(); release.Wait();
            }), releaseOnFailure: release.Set);
        held.ExpectFailure = true;
        try
        {
            var stopping = ObserveStop(held);
            await entered.Task;
            await AssertExpiredStop(stopping, held);
            Assert.False(Directory.Exists("/proc/" + held.Pid));
            Assert.True(held.Operation.Reaped!.IsCompletedSuccessfully);
            Assert.False(held.Operation.Cancellation!.IsCompleted);
            Assert.False(held.Operation.Work!.IsCompleted);
            Assert.False(held.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            Assert.Equal(WorkerProcessFailure.CleanupFailed, (await held.Running).Failure);
            RecordCleanup("expired-cancellation-callback", held);
        }
        finally
        {
            release.Set();
            await held.DisposeAsync();
            registration.Dispose();
            RecordCleanup("separate-eventual-cancellation-callback", held);
        }
        AssertSettled(held);
        await AssertAdmissionClosed(held);
    }

    [ServiceCompileFact]
    public async Task Stop_cancellation_callbacks_and_the_active_drain_have_one_original_deadline()
    {
        var entered = Signal(); using var release = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        var held = await HeldRuntime.StartAsync(shutdown: token => registration = token.Register(() =>
        {
            entered.TrySetResult(); release.Wait();
        }), releaseOnFailure: release.Set);
        held.ExpectFailure = true;
        try
        {
            var stopping = ObserveStop(held);
            await entered.Task;
            await AssertExpiredStop(stopping, held);
            Assert.Equal(held.Runner.StopDeadline, held.Operation.Deadline);
            Assert.False(Directory.Exists("/proc/" + held.Pid));
            Assert.Empty((await held.Running).Output);
            await AssertAdmissionClosed(held);
            RecordCleanup("expired-stop-callback-not-successful-stop", held);
        }
        finally
        {
            release.Set();
            await held.DisposeAsync();
            registration.Dispose();
            RecordCleanup("separate-eventual-stop-callback", held);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => held.Runner.StopAsync());
    }

    [ServiceCompileFact]
    public async Task Directory_failure_after_actual_runtime_settlement_quarantines_and_retains_the_exact_directory()
    {
        var held = await HeldRuntime.StartAsync(before: stage => stage == CompiledRuntimeCleanupStage.Directory
            ? Task.FromException(new IOException("Owned synthetic directory-removal failure.")) : Task.CompletedTask);
        held.ExpectFailure = true;
        held.AllowSyntheticDirectoryCleanup = true;
        try
        {
            var returned = await ObserveStop(held).Observed;
            RecordCleanup("observed-directory-failure-public-return", held);
            Assert.IsType<InvalidOperationException>(returned.Error);
            var result = await held.Running;
            Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
            Assert.Empty(result.Output);
            Assert.True(held.Operation.Work!.IsCompletedSuccessfully);
            Assert.False(await held.Operation.Work!);
            Assert.IsType<IOException>(held.Operation.Failure);
            Assert.True(held.Operation.HandlesDisposed);
            Assert.False(held.Operation.DirectoryRemoved);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            Assert.All(held.Operation.IoTasks, task => Assert.True(task.IsCompleted));
            Assert.False(Directory.Exists("/proc/" + held.Pid));
            await AssertAdmissionClosed(held);
            RecordCleanup("failed-directory-retained", held);
        }
        finally
        {
            // The failed task has joined the real child and pipes and disposed handles. Only this owned fixture root
            // is removed in teardown; doing so cannot rehabilitate the production result or reopen this runner.
            await held.DisposeAsync();
            RecordCleanup("separate-fixture-directory-removal", held);
        }
        Assert.False(Directory.Exists(held.Root));
        await AssertAdmissionClosed(held);
    }

    [ServiceCompileTheory]
    [InlineData((int)CompiledRuntimeIoKind.Handshake)]
    [InlineData((int)CompiledRuntimeIoKind.StandardError)]
    [InlineData((int)CompiledRuntimeIoKind.StandardOutput)]
    [InlineData((int)CompiledRuntimeIoKind.Writer)]
    [InlineData((int)CompiledRuntimeIoKind.ExitObserver)]
    [InlineData((int)CompiledRuntimeIoKind.ReapConfirmation)]
    public async Task Launch_held_original_operation_tasks_and_their_owners_prevent_disposal(int kindValue)
    {
        var kind = (CompiledRuntimeIoKind)kindValue;
        var gate = new TaskGate();
        var launch = new LaunchGate();
        var early = kind is CompiledRuntimeIoKind.Handshake or CompiledRuntimeIoKind.Writer;
        void ReleaseAll() { launch.Release(); gate.Release(); }
        var held = await HeldRuntime.StartAsync(wrap: (_, current, original) => current == kind ? gate.Hold(original) : original,
            beforeIoLaunch: (_, current) => current == kind ? launch.Hold() : Task.CompletedTask,
            beforeGo: early, releaseOnFailure: ReleaseAll);
        held.ExpectFailure = true;
        try
        {
            // Handshake and writer launch controls are before GO, not managed-Main witnesses. Other cases retain
            // their actual GO/frame witness while the selected original operation task is still launch-held.
            if (!early) Assert.True(held.Operation.FrameFlushed.Task.IsCompletedSuccessfully);
            var task = await held.Operation.TaskRegistered(kind);
            await launch.Entered.Task;
            Assert.Same(gate.Original, task.Original);
            Assert.NotSame(task.Original, task.Owned);
            Assert.False(task.Original.IsCompleted); // The original is pending BEFORE shutdown, not just its wrapper.
            Assert.False(task.Owned.IsCompleted);
            Assert.False(gate.Entered.Task.IsCompleted);
            Assert.True(Directory.Exists("/proc/" + held.Pid));
            Assert.False(held.Operation.NativeExit!.IsCompleted);
            Assert.False(held.Operation.HandlesDisposed);
            Assert.False(held.Operation.DirectoryRemoved);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            RecordCleanup("original-task-launch-held-before-shutdown-" + kind, held);
            var stopping = ObserveStop(held);
            await AssertExpiredStop(stopping, held);
            Assert.Equal(WorkerProcessFailure.CleanupFailed, (await held.Running).Failure);
            Assert.False(task.Original.IsCompleted); // It remains an unjoined original at the actual expiry result.
            Assert.False(task.Owned.IsCompleted);
            Assert.False(held.Operation.Work!.IsCompleted);
            Assert.False(held.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            if (kind == CompiledRuntimeIoKind.ReapConfirmation)
            {
                Assert.False(Directory.Exists("/proc/" + held.Pid));
                Assert.True(held.Operation.NativeExit!.IsCompletedSuccessfully);
                Assert.False(held.Operation.Reaped!.IsCompleted);
                // NativeExit is the actual kernel-exit wait. Its completion does not complete the separately
                // launch-held original confirmation task, and is not described as an unreaped kernel child.
            }
            if (early) Assert.False(held.Operation.FrameFlushed.Task.IsCompleted);
            RecordCleanup("expected-failure-original-task-still-pending-" + kind, held);
            launch.Release();
            await gate.Entered.Task;
            Assert.True(task.Original.IsCompleted);
            Assert.False(task.Owned.IsCompleted);
            Assert.False(held.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            RecordCleanup("separate-original-completion-owner-still-held-" + kind, held);
        }
        finally
        {
            ReleaseAll();
            await held.DisposeAsync();
            RecordCleanup("separate-eventual-task-owner-" + kind, held);
        }
        Assert.All(held.Operation.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
        Assert.True(held.Operation.HandlesDisposed);
        Assert.True(held.Operation.DirectoryRemoved);
        await AssertAdmissionClosed(held);
    }

    [ServiceCompileTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_and_wall_expiry_independently_quarantine_overdue_cleanup_without_Stop(bool cancel)
    {
        var gate = new TaskGate();
        var notificationEntered = Signal();
        using var notificationRelease = new ManualResetEventSlim();
        void ReleaseAll() { notificationRelease.Set(); gate.Release(); }
        var held = await HeldRuntime.StartAsync(wall: TimeSpan.FromSeconds(cancel ? 20 : 5),
            wrap: (_, kind, original) => kind == CompiledRuntimeIoKind.StandardOutput ? gate.Hold(original) : original,
            observeCleanup: (_, _) => { notificationEntered.TrySetResult(); notificationRelease.Wait(); },
            releaseOnFailure: ReleaseAll);
        held.ExpectFailure = true;
        long originalDeadline = 0;
        Task cancellation = Task.CompletedTask;
        try
        {
            Assert.True(held.WitnessAt < held.Operation.WallTargetAt);
            Assert.True(held.Operation.FrameFlushed.Task.IsCompletedSuccessfully);
            Assert.Equal(0, held.Runner.StopDeadline);
            if (cancel) cancellation = Task.Run(held.CancelCallerAsync);
            await notificationEntered.Task;
            RecordCleanup("independent-cleanup-entry-notification-held", held);
            // This is controlled budget consumption, not a tolerance or a fresh acceptance window. Even time
            // spent notifying the parent must be charged to the entry captured before that notification.
            await Task.Delay(ProcessWorkerOptions.KillTimeout / 4);
            notificationRelease.Set();
            originalDeadline = await held.Operation.CleanupStarted.Task;
            await cancellation;
            AssertIndependentCleanupAnchor(held, cancel);
            var anchor = held.Anchor(held.Operation);
            Assert.True(anchor.NotificationReturnedAt > anchor.ObservedAt);
            Assert.True(anchor.NotificationReturnedAt < originalDeadline);
            Assert.True(originalDeadline < checked(anchor.NotificationReturnedAt + FixedCleanupTicks()));
            await gate.Entered.Task;
            var returned = await held.RunningCall.Observed;
            RecordCleanup("observed-independent-negative-control-public-return", held);
            Assert.Null(returned.Error);
            var result = await held.Running;
            Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
            Assert.Empty(result.Output);
            Assert.Equal(0, held.Runner.StopDeadline); // No Stop/Dispose caused or bounded this outcome.
            Assert.Equal(originalDeadline, held.Operation.Deadline);
            Assert.True(held.Operation.CleanupDecisionAt >= originalDeadline);
            Assert.True(returned.At >= held.Operation.CleanupDecisionAt);
            Assert.All(held.Operation.Steps, step => Assert.Equal(originalDeadline, step.Deadline));
            Assert.False(held.Operation.Work!.IsCompleted);
            Assert.False(held.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            Assert.False(CleanupTimely(held));
            Assert.False(OperationTimely(held.Operation));
            await AssertAdmissionClosed(held);
            RecordCleanup(cancel ? "expected-failure-independent-caller-cleanup-overrun" : "expected-failure-independent-wall-cleanup-overrun", held);
        }
        finally
        {
            // Only after the independent public result is recorded may teardown call Stop/Dispose.
            ReleaseAll();
            await cancellation;
            await held.DisposeAsync();
            RecordCleanup("separate-eventual-independent-invocation-cleanup", held);
        }
        Assert.Equal(originalDeadline, held.Operation.Deadline);
        Assert.True(held.Operation.CompletedAt > originalDeadline);
        await AssertAdmissionClosed(held);
    }

    [ServiceCompileFact]
    public async Task Concurrent_Stop_Dispose_caller_cancel_and_admission_join_multiple_actual_owners()
    {
        var gates = new System.Collections.Concurrent.ConcurrentDictionary<ProcessCompiledRuntimeRunner.OwnedOperation, TaskGate>();
        var ordinal = 0;
        var admitted = Signal(); var launch = Signal();
        ProcessCompiledRuntimeRunner.OwnedOperation? admittedOwner = null;
        void ReleaseAll() { foreach (var gate in gates.Values) gate.Release(); launch.TrySetResult(); }
        var held = await HeldRuntime.StartAsync(observe: owner =>
        {
            var position = Interlocked.Increment(ref ordinal);
            if (position <= 2) Assert.True(gates.TryAdd(owner, new()));
            else if (position == 3) admittedOwner = owner;
        }, wrap: (owner, kind, original) => kind == CompiledRuntimeIoKind.StandardOutput && gates.TryGetValue(owner, out var gate)
            ? gate.Hold(original) : original, beforeLaunch: owner =>
            {
                if (!ReferenceEquals(owner, admittedOwner)) return Task.CompletedTask;
                admitted.TrySetResult(); return launch.Task;
            }, releaseOnFailure: ReleaseAll);
        var dispatched = new List<Task>();
        try
        {
            var second = await held.StartAdditionalAsync();
            Assert.NotEqual(held.Pid, second.Pid);
            Assert.True(Directory.Exists("/proc/" + held.Pid));
            Assert.True(Directory.Exists("/proc/" + second.Pid));
            var prelaunch = held.StartObservedInvocation("PrelaunchInvoke");
            var admissionRace = prelaunch.Running;
            dispatched.Add(admissionRace);
            await admitted.Task;
            Assert.NotNull(admittedOwner);
            var admittedOperation = admittedOwner!;
            Assert.False(admittedOperation.ChildStarted.Task.IsCompleted);
            Assert.False(admissionRace.IsCompleted);
            var start = Signal();
            var stopDispatch = Task.Run(async () => { await start.Task; return ObserveStop(held); });
            var disposeDispatch = Task.Run(async () => { await start.Task; return ObserveStop(held, dispose: true); });
            var cancelDispatch = Task.Run(async () => { await start.Task; await held.CancelCallerAsync(); });
            var racing = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
            {
                await start.Task;
                return await held.StartObservedInvocation("RacingInvoke" + index).Running;
            })).ToArray();
            dispatched.AddRange([stopDispatch, disposeDispatch, cancelDispatch, .. racing]);
            start.TrySetResult();
            var stopping = await stopDispatch; var disposing = await disposeDispatch;
            var deadline = held.Runner.StopDeadline;
            await gates[held.Operation].Entered.Task;
            await gates[second.Operation].Entered.Task;
            await held.Operation.CleanupStarted.Task;
            await second.Operation.CleanupStarted.Task;
            await cancelDispatch;
            Assert.True(held.Caller.IsCancellationRequested);
            Assert.Same(stopping.Original, held.Runner.StopAsync()); // Same in-flight stop, not only post-completion idempotence.
            Assert.False(stopping.Original.IsCompleted);
            Assert.False(disposing.Original.IsCompleted);
            Assert.False(held.Operation.HandlesDisposed);
            Assert.False(second.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(held.Operation.DirectoryPath));
            Assert.True(Directory.Exists(second.Operation.DirectoryPath));
            var beforeRefusal = held.ObservedOperations;
            var refused = held.StartObservedInvocation("RefusedDuringStopInvoke");
            Assert.Null((await refused.Call.Observed).Error);
            Assert.Null(refused.Owner);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await refused.Running).Failure);
            Assert.Equal(beforeRefusal, held.ObservedOperations);
            Assert.False(admissionRace.IsCompleted);
            launch.TrySetResult();
            Assert.Equal(WorkerProcessFailure.Canceled, (await admissionRace).Failure);
            Assert.False(admittedOperation.ChildStarted.Task.IsCompleted);
            Assert.Equal(0, admittedOperation.ChildPid); // Admitted before Stop, canceled before any child launch.
            gates[held.Operation].Release();
            Assert.True(await held.Operation.Work!);
            Assert.True(held.Operation.HandlesDisposed);
            Assert.True(held.Operation.DirectoryRemoved);
            Assert.False(second.Operation.Work!.IsCompleted);
            Assert.False(second.Operation.HandlesDisposed);
            Assert.True(Directory.Exists(second.Operation.DirectoryPath));
            Assert.False(stopping.Original.IsCompleted);
            Assert.False(disposing.Original.IsCompleted);
            gates[second.Operation].Release();
            var stopReturn = await stopping.Observed; var disposeReturn = await disposing.Observed;
            RecordCleanup("observed-concurrent-public-returns", held);
            Assert.Null(stopReturn.Error); Assert.Null(disposeReturn.Error);
            Assert.True(stopReturn.At <= deadline); Assert.True(disposeReturn.At <= deadline);
            Assert.True(held.Runner.StopDecisionAt <= deadline);
            Assert.True(held.Runner.StopCompletedAt <= deadline);
            await cancelDispatch;
            foreach (var invocation in new[] { held.Running, second.Running }.Concat(racing))
            {
                var result = await invocation;
                Assert.True(result.Failure is WorkerProcessFailure.Canceled or WorkerProcessFailure.SandboxUnavailable);
                Assert.Empty(result.Output);
            }
            Assert.InRange(held.ObservedOperations, 3, 5);
            await AssertEveryAdmittedInvocationTimely(held);
            foreach (var owner in held.Owners)
            {
                Assert.True(owner.Deadline > 0 && owner.Deadline <= deadline);
                Assert.True(owner.CompletedAt > 0 && owner.CompletedAt <= owner.Deadline);
                Assert.True(owner.CleanupDecisionAt <= owner.Deadline);
                Assert.True(owner.HandlesDisposed); Assert.True(owner.DirectoryRemoved);
                Assert.All(owner.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(held.Root));
            await AssertAdmissionClosed(held, held.ObservedOperations);
            Assert.True(CleanupTimely(held));
            RecordCleanup("normal-concurrent-multiple-owner-drain", held);
        }
        finally
        {
            ReleaseAll();
            if (dispatched.Count != 0) await Task.WhenAll(dispatched);
            await held.DisposeAsync();
        }
    }

    private sealed class TaskGate
    {
        private readonly TaskCompletionSource release = Signal();
        internal TaskCompletionSource<long> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Original { get; private set; }
        internal async Task Hold(Task original)
        {
            Assert.Null(Original); Original = original;
            try { await original; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            { /* Keep the real failure/cancellation for propagation after the parent releases ownership. */ }
            Entered.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp());
            await release.Task;
            await original;
        }
        internal void Release() => release.TrySetResult();
    }

    private sealed class LaunchGate
    {
        private readonly TaskCompletionSource release = Signal();
        internal TaskCompletionSource<long> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Hold()
        {
            Entered.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp());
            return release.Task;
        }
        internal void Release() => release.TrySetResult();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PublicReturn(long At, Exception? Error);

    private sealed class PublicCall
    {
        internal PublicCall(string name, Task original, long deadline = 0, Func<long>? deadlineProvider = null,
            Func<bool>? requiresDeadline = null)
        {
            Name = name; Original = original; fixedDeadline = deadline; this.deadlineProvider = deadlineProvider;
            this.requiresDeadline = requiresDeadline;
            // Timestamp the actual public task's completion, not the later test await or the retained owner's exit.
            // ExecuteSynchronously is an observation request, not a hard real-time scheduler guarantee.
            Observed = original.ContinueWith(completed =>
            {
                var at = System.Diagnostics.Stopwatch.GetTimestamp();
                Exception? error = completed.IsCanceled ? new TaskCanceledException(completed) : completed.Exception;
                if (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerExceptions[0];
                Error = error; Interlocked.Exchange(ref returnedAt, at);
                return new PublicReturn(at, error);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private long returnedAt;
        private readonly long fixedDeadline;
        private readonly Func<long>? deadlineProvider;
        private readonly Func<bool>? requiresDeadline;
        internal string Name { get; }
        internal Task Original { get; }
        internal long Deadline => deadlineProvider?.Invoke() ?? fixedDeadline;
        internal bool RequiresCleanupDeadline => requiresDeadline?.Invoke() ?? true;
        internal long ReturnedAt => Interlocked.Read(ref returnedAt);
        internal Exception? Error { get; private set; }
        internal Task<PublicReturn> Observed { get; }
    }

    private static PublicCall ObserveStop(HeldRuntime held, bool dispose = false)
    {
        var task = dispose ? held.Runner.DisposeAsync().AsTask() : held.Runner.StopAsync();
        var call = new PublicCall(dispose ? "Dispose" : "Stop", task, held.Runner.StopDeadline);
        held.AddCall(call);
        return call;
    }

    private async Task AssertExpiredStop(PublicCall call, HeldRuntime held)
    {
        var returned = await call.Observed; // No new relative two-second wait after a stage notification.
        RecordCleanup("observed-expiry-public-return-before-contract-assertions", held);
        Assert.IsType<InvalidOperationException>(returned.Error);
        Assert.Equal(call.Deadline, held.Runner.StopDeadline);
        Assert.Equal(call.Deadline, held.Operation.Deadline);
        Assert.True(held.Runner.StopDecisionAt >= call.Deadline);
        Assert.True(returned.At >= held.Runner.StopDecisionAt);
        Assert.All(held.Operation.Steps, step => Assert.Equal(call.Deadline, step.Deadline));
        // A throw observed after the deadline is an overrun, not timely cleanup. Intentional expiry proves only
        // fail-closed behavior and single-deadline ownership; it cannot certify real-time API return by that deadline.
    }

    private async Task AssertEveryAdmittedInvocationTimely(HeldRuntime held)
    {
        // Compare every admitted invocation with its own original cleanup deadline, not just the outer Stop.
        foreach (var owner in held.Owners)
        {
            var invocation = Assert.Single(held.Invocations, call => ReferenceEquals(call.Owner, owner));
            var returned = await invocation.Call.Observed;
            RecordCleanup("admitted-invocation-public-return-before-timeliness-assertion", held);
            Assert.Null(returned.Error);
            Assert.True(invocation.Call.RequiresCleanupDeadline);
            Assert.Equal(owner.Deadline, invocation.Call.Deadline);
            Assert.True(owner.Deadline > 0 && returned.At <= owner.Deadline);
            Assert.True(OperationTimely(owner));
        }
        foreach (var invocation in held.Invocations)
        {
            if (invocation.Owner is { } owner) Assert.Contains(owner, held.Owners);
            else
            {
                var returned = await invocation.Call.Observed;
                Assert.Null(returned.Error);
                var refused = await invocation.Running;
                Assert.Equal(WorkerProcessFailure.SandboxUnavailable, refused.Failure);
                Assert.Empty(refused.Output); // Unadmitted calls have no invented cleanup deadline.
            }
        }
        Assert.True(PublicCallsTimely(held));
        Assert.True(CleanupTimely(held));
    }

    private static void AssertIndependentCleanupAnchor(HeldRuntime held, bool callerCancellation)
    {
        var owner = held.Operation;
        var observation = held.Anchor(owner);
        Assert.Equal(0, held.Runner.StopDeadline);
        Assert.Equal(owner.CleanupStartedAt, observation.EntryAt);
        Assert.True(observation.ObservedAt >= observation.EntryAt);
        Assert.Equal(checked(observation.EntryAt + FixedCleanupTicks()), owner.Deadline);
        Assert.True(owner.Deadline <= checked(observation.ObservedAt + FixedCleanupTicks()));
        Assert.True(observation.NotificationReturnedAt >= observation.ObservedAt);
        var invocation = Assert.Single(held.Invocations, call => ReferenceEquals(call.Owner, owner));
        Assert.True(invocation.StartedAt > 0 && owner.WallStartedAt >= invocation.StartedAt);
        Assert.True(invocation.AdmittedAt >= owner.WallStartedAt);
        Assert.Equal((long)(held.ConfiguredWall.TotalSeconds * System.Diagnostics.Stopwatch.Frequency), owner.WallTargetAt - owner.WallStartedAt);
        Assert.True(owner.CleanupStartedAt >= owner.WallStartedAt);
        if (callerCancellation)
        {
            Assert.True(held.CallerCancellationInitiatedAt > 0);
            Assert.True(owner.CleanupStartedAt >= held.CallerCancellationInitiatedAt);
        }
        else Assert.Equal(0, held.CallerCancellationInitiatedAt);
        // Wall target, actual deadline-token notification and actual cleanup entry are separate observations.
        // Scheduling latency between them is recorded, not silently added to the fixed entry-anchored two seconds.
    }

    private static void AssertSettled(HeldRuntime held)
    {
        AssertFiveIoTasks(held.Operation);
        Assert.All(held.Operation.IoTasks, task => Assert.True(task.IsCompleted));
        Assert.All(held.Operation.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
        Assert.True(held.Operation.Cancellation!.IsCompletedSuccessfully);
        Assert.True(held.Operation.NativeExit!.IsCompletedSuccessfully);
        Assert.True(held.Operation.Reaped!.IsCompletedSuccessfully);
        Assert.True(held.Operation.HandlesDisposed);
        Assert.True(held.Operation.DirectoryRemoved);
    }

    private static void AssertFiveIoTasks(ProcessCompiledRuntimeRunner.OwnedOperation operation) => Assert.Collection(operation.IoTasks,
        stderr => Assert.NotNull(stderr), handshake => Assert.True(handshake.IsCompletedSuccessfully),
        stdout => Assert.NotNull(stdout), writer => Assert.NotNull(writer), exit => Assert.NotNull(exit));

    private static async Task AssertAdmissionClosed(HeldRuntime held, int expectedObserved = 1)
    {
        Assert.False(held.Runner.Available);
        Assert.False(await held.Runner.ProbeAsync(default));
        var refused = held.StartObservedInvocation("RefusedAfterClosureInvoke");
        Assert.Null((await refused.Call.Observed).Error);
        Assert.Null(refused.Owner);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await refused.Running).Failure);
        Assert.Equal(expectedObserved, held.ObservedOperations);
    }

    private static bool OperationTimely(ProcessCompiledRuntimeRunner.OwnedOperation owner) =>
        owner.HandlesDisposed && owner.DirectoryRemoved && owner.Failure is null &&
        owner.Work?.IsCompletedSuccessfully == true && owner.Cancellation?.IsCompletedSuccessfully == true &&
        owner.Reaped?.IsCompletedSuccessfully == true &&
        owner.CompletedAt > 0 && owner.CompletedAt <= owner.Deadline &&
        owner.CleanupDecisionAt > 0 && owner.CleanupDecisionAt <= owner.Deadline &&
        (owner.ChildPid == 0 || owner.NativeExit?.IsCompletedSuccessfully == true) &&
        owner.Tasks.All(task => task.Original.IsCompleted && task.Owned.IsCompleted);

    private static bool StopTimely(HeldRuntime held) => held.Runner.StopDeadline > 0 &&
        held.Runner.StopCompletedAt > 0 && held.Runner.StopCompletedAt <= held.Runner.StopDeadline &&
        held.Runner.StopDecisionAt > 0 && held.Runner.StopDecisionAt <= held.Runner.StopDeadline &&
        held.Calls.Where(call => call.Name is "Stop" or "Dispose").All(call =>
            call.ReturnedAt > 0 && call.ReturnedAt <= call.Deadline && call.Error is null);

    private static bool PublicCallsTimely(HeldRuntime held) => held.Calls.All(call =>
        call.ReturnedAt > 0 && call.Error is null &&
        (!call.RequiresCleanupDeadline || call.Deadline > 0 && call.ReturnedAt <= call.Deadline));

    private static bool CleanupTimely(HeldRuntime held) => held.Owners.All(OperationTimely) && PublicCallsTimely(held) &&
        (held.Runner.StopDeadline == 0 || StopTimely(held));

    private void RecordCleanup(string phase, HeldRuntime held) => output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        phase,
        held.Pid,
        held.StartTicks,
        held.Operation.Deadline,
        held.Operation.CompletedAt,
        held.Runner.StopDeadline,
        held.Runner.StopCompletedAt,
        held.Runner.StopDecisionAt,
        held.Operation.CleanupDecisionAt,
        held.Operation.HandlesDisposed,
        held.Operation.DirectoryRemoved,
        held.Operation.Steps,
        held.WitnessAt,
        beforeGoOwnershipOnly = held.BeforeGo,
        held.ConfiguredWall,
        held.Operation.WallStartedAt,
        held.Operation.WallTargetAt,
        held.Operation.CleanupStartedAt,
        held.CallerCancellationInitiatedAt,
        held.CallerCancellationCompletedAt,
        entryObservation = held.TryAnchor(held.Operation),
        deadlineTokenCancellationObservedAt = held.DeadlineSignalAt(held.Operation),
        cleanupEntryMinusWallTargetTicks = held.Operation.CleanupStartedAt == 0 ? (long?)null : held.Operation.CleanupStartedAt - held.Operation.WallTargetAt,
        cleanupEntryMinusCallerInitiationTicks = held.CallerCancellationInitiatedAt == 0 || held.Operation.CleanupStartedAt == 0
            ? (long?)null : held.Operation.CleanupStartedAt - held.CallerCancellationInitiatedAt,
        witnesses = held.Witnesses,
        expectedFailureControl = held.ExpectFailure,
        operationCleanupTimely = OperationTimely(held.Operation),
        stopEpisodePresent = held.Runner.StopDeadline > 0,
        stopCleanupTimely = StopTimely(held),
        publicCallsTimely = PublicCallsTimely(held),
        cleanupTimely = CleanupTimely(held),
        publicReturns = held.Calls.Select(call => new
        {
            call.Name,
            call.RequiresCleanupDeadline,
            call.Deadline,
            call.ReturnedAt,
            error = call.Error?.GetType().Name,
            publicReturnTimely = call.RequiresCleanupDeadline
                ? call.Deadline > 0 && call.ReturnedAt > 0 && call.ReturnedAt <= call.Deadline : (bool?)null,
            overrunTicks = call.Deadline > 0 && call.ReturnedAt > 0 ? Math.Max(0, call.ReturnedAt - call.Deadline) : (long?)null
        }),
        invocations = held.Invocations.Select(call => new
        {
            call.Name,
            call.StartedAt,
            call.AdmittedAt,
            call.ReturnedToCallerAt,
            admitted = call.Owner is not null,
            directory = call.Owner?.DirectoryPath,
            childPid = call.Owner?.ChildPid,
            originalDeadline = call.Owner?.Deadline,
            call.Call.ReturnedAt
        }),
        ownedTasks = held.Operation.Tasks.Select(task => new { task.Kind, originalCompleted = task.Original.IsCompleted, ownerCompleted = task.Owned.IsCompleted }),
        nativeKernelExitWaitCompleted = held.Operation.NativeExit?.IsCompleted,
        allOwners = held.Owners.Select(owner => new
        {
            owner.DirectoryPath,
            owner.ChildPid,
            owner.Deadline,
            owner.CompletedAt,
            owner.CleanupDecisionAt,
            owner.WallStartedAt,
            owner.WallTargetAt,
            owner.CleanupStartedAt,
            entryObservation = held.TryAnchor(owner),
            deadlineTokenCancellationObservedAt = held.DeadlineSignalAt(owner),
            owner.HandlesDisposed,
            owner.DirectoryRemoved,
            nativeKernelExitWaitCompleted = owner.NativeExit?.IsCompleted,
            tasks = owner.Tasks.Select(task => new { task.Kind, originalCompleted = task.Original.IsCompleted, ownerCompleted = task.Owned.IsCompleted })
        }),
        workCompleted = held.Operation.Work?.IsCompleted,
        ioCompleted = held.Operation.IoTasks.All(task => task.IsCompleted),
        available = held.Runner.Available
    }));

    private sealed class HeldRuntime : IAsyncDisposable
    {
        internal string Root { get; } = TemporaryRoot();
        internal ProcessCompiledRuntimeRunner Runner { get; private set; } = null!;
        internal ProcessCompiledRuntimeRunner.OwnedOperation Operation { get; private set; } = null!;
        internal Task<WorkerProcessResult> Running { get; private set; } = null!;
        internal PublicCall RunningCall { get; private set; } = null!;
        internal CancellationTokenSource Caller { get; } = new();
        internal int Pid { get; private set; }
        internal string StartTicks { get; private set; } = "";
        internal long WitnessAt { get; private set; }
        internal bool BeforeGo { get; private set; }
        internal TimeSpan ConfiguredWall { get; private set; }
        private long callerCancellationInitiatedAt, callerCancellationCompletedAt;
        internal long CallerCancellationInitiatedAt => Interlocked.Read(ref callerCancellationInitiatedAt);
        internal long CallerCancellationCompletedAt => Interlocked.Read(ref callerCancellationCompletedAt);
        internal int ObservedOperations;
        internal bool ExpectFailure { get; set; }
        internal bool AllowSyntheticDirectoryCleanup { get; set; }
        private readonly List<ProcessCompiledRuntimeRunner.OwnedOperation> owners = [];
        private readonly List<PublicCall> calls = [];
        private readonly List<RuntimeWitness> witnesses = [];
        private readonly List<InvocationRecord> invocations = [];
        private readonly AsyncLocal<InvocationSlot?> launching = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessCompiledRuntimeRunner.OwnedOperation, CleanupAnchor> anchors = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessCompiledRuntimeRunner.OwnedOperation, long> deadlineSignals = new();
        private readonly List<CancellationTokenRegistration> signalRegistrations = [];
        internal sealed record CleanupAnchor(long EntryAt, long ObservedAt)
        {
            private long notificationReturnedAt;
            public long NotificationReturnedAt => Interlocked.Read(ref notificationReturnedAt);
            internal void MarkReturned() => Interlocked.Exchange(ref notificationReturnedAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        private sealed class InvocationSlot
        {
            internal ProcessCompiledRuntimeRunner.OwnedOperation? Owner;
            internal long AdmittedAt;
        }
        internal sealed class InvocationRecord(string name, Task<WorkerProcessResult> running, PublicCall call,
            long startedAt, long returnedToCallerAt, Func<ProcessCompiledRuntimeRunner.OwnedOperation?> owner, Func<long> admittedAt)
        {
            internal string Name { get; } = name;
            internal Task<WorkerProcessResult> Running { get; } = running;
            internal PublicCall Call { get; } = call;
            internal long StartedAt { get; } = startedAt;
            internal long ReturnedToCallerAt { get; } = returnedToCallerAt;
            internal long AdmittedAt => admittedAt();
            internal ProcessCompiledRuntimeRunner.OwnedOperation? Owner => owner();
        }
        internal sealed record RuntimeWitness(int Pid, string StartTicks, long At, bool GoFrameFlushed);
        internal ProcessCompiledRuntimeRunner.OwnedOperation[] Owners { get { lock (owners) return [.. owners]; } }
        internal PublicCall[] Calls { get { lock (calls) return [.. calls]; } }
        internal RuntimeWitness[] Witnesses { get { lock (witnesses) return [.. witnesses]; } }
        internal InvocationRecord[] Invocations { get { lock (invocations) return [.. invocations]; } }
        internal void AddCall(PublicCall call) { lock (calls) calls.Add(call); }
        internal CleanupAnchor Anchor(ProcessCompiledRuntimeRunner.OwnedOperation owner) => anchors[owner];
        internal CleanupAnchor? TryAnchor(ProcessCompiledRuntimeRunner.OwnedOperation owner) => anchors.TryGetValue(owner, out var value) ? value : null;
        internal long DeadlineSignalAt(ProcessCompiledRuntimeRunner.OwnedOperation owner) => deadlineSignals.TryGetValue(owner, out var value) ? value : 0;
        internal async Task CancelCallerAsync()
        {
            Interlocked.CompareExchange(ref callerCancellationInitiatedAt, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
            try { await Caller.CancelAsync(); }
            finally { Interlocked.Exchange(ref callerCancellationCompletedAt, System.Diagnostics.Stopwatch.GetTimestamp()); }
        }

        internal InvocationRecord StartObservedInvocation(string name, CancellationToken token = default)
        {
            var slot = new InvocationSlot();
            var previous = launching.Value;
            Task<WorkerProcessResult> running;
            launching.Value = slot;
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try { running = Runner.RunAsync(new byte[] { 0 }, token); }
            finally { launching.Value = previous; }
            var returnedToCallerAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var call = new PublicCall(name, running, deadlineProvider: () => slot.Owner?.Deadline ?? 0,
                requiresDeadline: () => slot.Owner is not null);
            var invocation = new InvocationRecord(name, running, call, startedAt, returnedToCallerAt, () => slot.Owner, () => slot.AdmittedAt);
            lock (invocations) invocations.Add(invocation);
            AddCall(call);
            return invocation;
        }

        internal sealed record LiveInvocation(ProcessCompiledRuntimeRunner.OwnedOperation Operation,
            Task<WorkerProcessResult> Running, PublicCall Call, int Pid, string StartTicks, long WitnessAt);

        internal async Task<LiveInvocation> StartAdditionalAsync()
        {
            Assert.True(Runner.Available);
            var invocation = StartObservedInvocation("AdditionalInvoke", Caller.Token);
            var owner = Assert.IsType<ProcessCompiledRuntimeRunner.OwnedOperation>(invocation.Owner);
            var running = invocation.Running; var call = invocation.Call;
            var pid = await owner.FrameFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stat = File.ReadAllText("/proc/" + pid + "/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[1]);
            Assert.NotEqual("Z", fields[0]);
            Assert.NotEmpty(fields[19]);
            Assert.False(running.IsCompleted);
            Assert.True(Directory.Exists(owner.DirectoryPath));
            AssertFiveIoTasks(owner);
            var at = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (witnesses) witnesses.Add(new(pid, fields[19], at, true));
            return new(owner, running, call, pid, fields[19], at);
        }

        internal static async Task<HeldRuntime> StartAsync(Func<CompiledRuntimeCleanupStage, Task>? before = null,
            Action<ProcessCompiledRuntimeRunner.OwnedOperation>? observe = null, Action<CancellationToken>? shutdown = null,
            Action? releaseOnFailure = null, Func<ProcessCompiledRuntimeRunner.OwnedOperation, CompiledRuntimeIoKind, Task, Task>? wrap = null,
            bool beforeGo = false, TimeSpan? wall = null, Func<ProcessCompiledRuntimeRunner.OwnedOperation, Task>? beforeLaunch = null,
            Func<ProcessCompiledRuntimeRunner.OwnedOperation, CompiledRuntimeIoKind, Task>? beforeIoLaunch = null,
            Action<ProcessCompiledRuntimeRunner.OwnedOperation, long>? observeCleanup = null)
        {
            Assert.True(OperatingSystem.IsLinux());
            Assert.True(CompileConfigured, "Actual-runtime lifecycle qualification requires the pinned installed launcher/worker/SDK.");
            Assert.True(File.Exists(Assembly));
            var held = new HeldRuntime();
            var captured = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
            held.BeforeGo = beforeGo;
            held.ConfiguredWall = wall ?? ProcessCompiledRuntimeOptions.Profile.MaximumWallTimeout;
            held.Runner = new(new(PackagedFerruleService.Identity, Assembly, Launcher!, held.Root, Toolchain, wall)
            {
                WithholdEndOfInput = true,
                BeforeCleanupStage = before,
                ObserveShutdown = shutdown,
                WrapOwnedTask = wrap,
                BeforeLaunch = beforeLaunch,
                BeforeIoLaunch = beforeIoLaunch,
                ObserveCleanupStart = (owner, entryAt) =>
                {
                    var anchor = new CleanupAnchor(entryAt, System.Diagnostics.Stopwatch.GetTimestamp());
                    Assert.True(held.anchors.TryAdd(owner, anchor));
                    try { observeCleanup?.Invoke(owner, entryAt); }
                    finally { anchor.MarkReturned(); }
                },
                ObserveOperation = operation =>
                {
                    Interlocked.Increment(ref held.ObservedOperations);
                    lock (held.owners) held.owners.Add(operation);
                    if (held.launching.Value is { } slot)
                    {
                        slot.Owner = operation;
                        slot.AdmittedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    var registration = operation.WallDeadline.Token.Register(() =>
                        held.deadlineSignals.TryAdd(operation, System.Diagnostics.Stopwatch.GetTimestamp()));
                    lock (held.signalRegistrations) held.signalRegistrations.Add(registration);
                    observe?.Invoke(operation); captured.TrySetResult(operation);
                }
            });
            var invocation = held.StartObservedInvocation("Invoke", held.Caller.Token);
            held.Running = invocation.Running;
            held.RunningCall = invocation.Call;
            try
            {
                held.Operation = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                held.Pid = await (beforeGo ? held.Operation.ChildStarted.Task : held.Operation.FrameFlushed.Task).WaitAsync(TimeSpan.FromSeconds(5));
                var stat = File.ReadAllText("/proc/" + held.Pid + "/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[1]);
                Assert.NotEqual("Z", fields[0]);
                held.StartTicks = fields[19];
                held.WitnessAt = System.Diagnostics.Stopwatch.GetTimestamp();
                Assert.NotEmpty(held.StartTicks);
                Assert.False(held.Running.IsCompleted);
                Assert.True(Directory.Exists(held.Operation.DirectoryPath));
                if (beforeGo) Assert.False(held.Operation.FrameFlushed.Task.IsCompleted);
                else AssertFiveIoTasks(held.Operation); // includes the real verified native handshake, not only later pipes
                lock (held.witnesses) held.witnesses.Add(new(held.Pid, held.StartTicks, held.WitnessAt, !beforeGo));
                return held;
            }
            catch (Exception primary) when (Recoverable(primary))
            {
                releaseOnFailure?.Invoke();
                // No caller can acquire this fixture until its actual child is witnessed. Preserve an unjoined root.
                try { await held.DisposeAsync(); }
                catch (Exception cleanup) when (Recoverable(cleanup))
                { throw new AggregateException("Runtime witness and owned teardown failed; retain " + held.Root, primary, cleanup); }
                throw;
            }
        }

        private static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

        public async ValueTask DisposeAsync()
        {
            Exception? stopError = null;
            try { await Runner.DisposeAsync().AsTask().WaitAsync(ProcessWorkerOptions.KillTimeout); }
            catch (Exception error) when (Recoverable(error)) { stopError = error; }
            // This is a separate teardown join after the test has released its own barrier, not more product budget.
            try
            {
                await Runner.JoinRetainedCleanupAsync().WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var owner in Owners)
                {
                    Assert.True(owner.Work!.IsCompleted);
                    Assert.All(owner.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
                    Assert.True(owner.HandlesDisposed);
                    if (owner.ChildPid != 0)
                    {
                        Assert.True(owner.NativeExit!.IsCompletedSuccessfully);
                        Assert.False(Directory.Exists("/proc/" + owner.ChildPid));
                    }
                }
                lock (signalRegistrations) foreach (var registration in signalRegistrations) registration.Dispose();
                Caller.Dispose();
                if (Directory.Exists(Root))
                {
                    if (AllowSyntheticDirectoryCleanup) Directory.Delete(Root, recursive: true);
                    else { Assert.Empty(Directory.EnumerateFileSystemEntries(Root)); Directory.Delete(Root); }
                }
            }
            catch (Exception cleanup) when (Recoverable(cleanup))
            {
                if (stopError is not null) throw new AggregateException("Owned runtime teardown failed; retain " + Root, stopError, cleanup);
                throw;
            }
            if (stopError is not null && !(ExpectFailure && stopError is InvalidOperationException))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopError).Throw();
        }
    }

    [ServiceCompileFact]
    public async Task Extra_frameworks_and_external_probing_are_refused_before_managed_execution()
    {
        var root = TemporaryRoot();
        try
        {
            var program = Path.Combine(root, "program"); Directory.CreateDirectory(program);
            var operations = Path.Combine(root, "operations"); Directory.CreateDirectory(operations);
            var probing = Path.Combine(root, "unmeasured-probing"); Directory.CreateDirectory(probing);
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Assembly)!)) File.Copy(file, Path.Combine(program, Path.GetFileName(file)));
            var entry = Path.Combine(program, Path.GetFileName(Assembly));
            var configuration = Path.ChangeExtension(entry, ".runtimeconfig.json");
            var original = File.ReadAllText(configuration);
            foreach (var extraFramework in new[] { false, true })
            {
                var document = System.Text.Json.Nodes.JsonNode.Parse(original)!;
                var settings = document["runtimeOptions"]!.AsObject();
                if (extraFramework)
                {
                    settings.Remove("framework");
                    settings["frameworks"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = BuildWorkerProtocol.RuntimeVersion },
                        new System.Text.Json.Nodes.JsonObject { ["name"] = "Microsoft.AspNetCore.App", ["version"] = BuildWorkerProtocol.RuntimeVersion });
                }
                else settings["additionalProbingPaths"] = new System.Text.Json.Nodes.JsonArray(probing);
                File.WriteAllText(configuration, document.ToJsonString());
                await using var runner = new ProcessCompiledRuntimeRunner(new(PackagedFerruleService.Identity, entry, Launcher!, operations, Toolchain));
                Assert.False(await runner.ProbeAsync(default));
                Assert.True(runner.Available);
                Assert.Empty(Directory.EnumerateFileSystemEntries(operations));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceCompileFact]
    public async Task The_exact_hostfxr_is_required_without_a_discovery_or_export_fallback()
    {
        var root = TemporaryRoot();
        using var installation = new FerruleServiceCompileTests.FakeInstallation();
        try
        {
            var options = new ProcessCompiledRuntimeOptions(PackagedFerruleService.Identity, Assembly, Launcher!, root, installation.Toolchain);
            // The installed hostfxr is an invalid object, then a valid native object without the hosting exports.
            await using (var malformed = new ProcessCompiledRuntimeRunner(options)) Assert.False(await malformed.ProbeAsync(default));
            // Any real shared object without the hosting exports; Debian and Arch place libc differently.
            var libc = new[] { "/usr/lib/x86_64-linux-gnu/libc.so.6", "/usr/lib64/libc.so.6", "/usr/lib/libc.so.6" }.First(File.Exists);
            File.Copy(libc, BuildCompilation.HostFxr(installation.Toolchain), overwrite: true);
            await using (var wrongExports = new ProcessCompiledRuntimeRunner(options)) Assert.False(await wrongExports.ProbeAsync(default));
            File.Delete(BuildCompilation.HostFxr(installation.Toolchain));
            Assert.Throws<ArgumentException>(() => new ProcessCompiledRuntimeRunner(options));
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
            // The native worker identity is a fixed input here; only the runtime's own files vary.
            var options = new ProcessCompiledRuntimeOptions("sha256:" + new string('a', 64), Path.Combine(runtime, "weft-compiled-runtime.dll"),
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
