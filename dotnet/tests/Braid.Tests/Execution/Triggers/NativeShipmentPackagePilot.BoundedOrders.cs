using System.Diagnostics;
using System.Text.Json;
using Braid.Application;
using Braid.Deployment;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTestSupport;

namespace Braid.Tests;

public sealed partial class NativeShipmentPackagePilot
{
    private async Task BoundedOrders(string root, bool memory, Func<IInspectionWorkerRunner, MachineInvocationService> service,
        ReleaseIntent a, ReleaseIntent b, MachineCaller callerA, MachineCaller callerB)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var token = deadline.Token;
        var budget = TimeSpan.FromSeconds(30);
        var options = FerruleCompiledServiceTests.Options(root);
        var observedA = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedB = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var program = memory ? Path.Combine(AppContext.BaseDirectory, "CompiledSandboxProbe/Braid.CompiledSandboxProbe.dll") : options.RuntimeAssembly;
        await using var runnerA = new ProcessCompiledRuntimeRunner(new(options.NativeIdentity, program, Launcher!, root, Toolchain, memory ? budget : TimeSpan.FromSeconds(8))
        { WithholdEndOfInput = true, ObserveOperation = op => observedA.SetResult(op) });
        await using var runnerB = new ProcessCompiledRuntimeRunner(new(options.NativeIdentity, options.RuntimeAssembly, Launcher!, root, Toolchain)
        { ObserveOperation = op => observedB.SetResult(op) });
        var workerA = new OrderWorker(runnerA, memory); var workerB = new OrderWorker(runnerB, false);
        var healthy = service(workerB);
        var request = new MachineInvocationRequest(new(null, Shipment945Fixture.Input), [], [], [new(null, "application/json")]);
        var failed = service(workerA).InvokeAsync(a.Target, callerA, Guid.NewGuid(), request, token);
        var owner = await observedA.Task.WaitAsync(token);
        var pid = await owner.FrameFlushed.Task.WaitAsync(token); // verified limits, GO and input delivered
        var order = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        var sibling = healthy.InvokeAsync(b.Target, callerB, order, request, token);
        var peer = await observedB.Task.WaitAsync(token);
        Assert.NotEqual(pid, await peer.FrameFlushed.Task.WaitAsync(token));
        Assert.False(owner.Child!.HasExited); // both verified dispatches actually overlapped
        if (memory) owner.Child.StandardInput.Close(); // release the probe only after B dispatch
        var result = await sibling;
        clock.Stop();
        Assert.True(clock.Elapsed <= budget, $"Sibling order exceeded {budget}: {clock.Elapsed}");
        Assert.Equal(MachineInvocationStatus.Completed, result.Status);
        Assert.False(result.Replayed);
        Assert.Equal(b.ReleaseId, result.ReleaseId);
        using var response = JsonDocument.Parse(result.Response!);
        Assert.Equal(Shipment945Fixture.NativeExpected, Assert.Single(response.RootElement.GetProperty("outputs").EnumerateArray()).GetProperty("content").GetBytesFromBase64());
        var refusal = await failed;
        if (!runnerA.Available || !runnerB.Available)
            output.WriteLine($"Cleanup unavailable: A={workerA.Result?.Failure}, B={workerB.Result?.Failure}; " +
                $"A stages={string.Join(',', owner.Steps.Select(step => step.Stage))}, B stages={string.Join(',', peer.Steps.Select(step => step.Stage))}");
        Assert.Equal(MachineInvocationOutcome.Unavailable, refusal.Outcome);
        Assert.Null(refusal.Response);
        Assert.Equal(memory ? WorkerProcessFailure.WorkerFailed : WorkerProcessFailure.TimedOut, workerA.Result!.Failure);
        Assert.Empty(workerA.Result.Output);
        if (memory) Assert.Equal(73, owner.ExitCode); // probe observed ENOMEM under the fixed address-space cap
        Assert.True(owner.HandlesDisposed && owner.DirectoryRemoved);
        Assert.False(Directory.Exists($"/proc/{pid}"));
        var replay = await healthy.InvokeAsync(b.Target, callerB, order, request, token);
        Assert.True(replay.Replayed);
        Assert.Equal(result.Response, replay.Response);
        Assert.Equal(1, workerB.Dispatches);
        output.WriteLine($"A {(memory ? "child RLIMIT_AS ENOMEM" : "wall timeout")}; B {clock.Elapsed.TotalMilliseconds:F0} ms; budget {budget.TotalSeconds} s; one native dispatch.");
    }

    private sealed class OrderWorker(ProcessCompiledRuntimeRunner runner, bool memory) : IInspectionWorkerRunner
    {
        public string BuildIdentity => runner.BuildIdentity;
        internal int Dispatches { get; private set; }
        internal WorkerProcessResult? Result { get; private set; }
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            Dispatches++;
            return Result = await runner.RunAsync(memory ? "bounded-address-space"u8.ToArray() : frame, token);
        }
    }
}
