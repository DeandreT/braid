using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.MappingBackends.Workers;
using Weft.Server;

namespace Weft.Tests;

public sealed partial class FerruleDockerRunnerTests
{
    [Fact]
    public async Task ShutdownClosesAdmissionButJoinsTheOriginalCreateAndExactRemoval()
    {
        var creation = FakeProcess.GatedCreation();
        var removal = FakeProcess.GatedControlOutput(OtherContainerId + "\n");
        var launcher = new FakeLauncher { Create = creation, Remove = removal };
        var runner = Runner(launcher);
        var work = runner.RunAsync("native"u8.ToArray(), CancellationToken.None);
        await creation.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stop = runner.StopAsync();
        Assert.Same(stop, runner.StopAsync());
        try
        {
            Assert.False(creation.FirstExitWaitToken.IsCancellationRequested);
            Assert.False(creation.Killed);
            Assert.False(stop.IsCompleted);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
                (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
            Assert.Equal(["info", "image", "create"], launcher.Commands.Select(Operation));
            creation.Settle();
            await removal.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(stop.IsCompleted);
            Assert.False(work.IsCompleted);
            Assert.Empty(launcher.Worker.Input.ToArray());
            Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "inspect" or "start");
        }
        finally
        {
            creation.Settle();
            removal.Settle();
            // This intentionally synthetic removal returns the wrong immutable ID:
            // settlement must be joined but must NOT be reported as safe cleanup.
            await work.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await work).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stop);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ShutdownDuringCreatePreservesTheIssue34CancellationPolicyAndConfirmsRemoval()
    {
        var creation = FakeProcess.GatedCreation();
        var launcher = new FakeLauncher { Create = creation };
        var runner = Runner(launcher);
        var first = runner.RunAsync("native"u8.ToArray(), CancellationToken.None);
        await creation.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var queued = runner.RunAsync("queued"u8.ToArray(), CancellationToken.None);
        var stop = runner.StopAsync();
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.False(creation.FirstExitWaitToken.IsCancellationRequested);
            Assert.False(creation.Killed);
            Assert.Equal(WorkerProcessFailure.Canceled, (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Failure);
            Assert.Equal(["info", "image", "create"], launcher.Commands.Select(Operation));
        }
        finally { creation.Settle(); }
        Assert.Equal(WorkerProcessFailure.Canceled, (await first.WaitAsync(TimeSpan.FromSeconds(1))).Failure);
        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(["info", "image", "create", "ownership-inspect", "rm"], launcher.Commands.Select(Operation));
        Assert.Empty(launcher.Worker.Input.ToArray());
        await runner.DisposeAsync();
        await runner.DisposeAsync();
        Assert.Same(stop, runner.StopAsync());
    }

    [Fact]
    public async Task ShutdownOfActiveWorkerKillsOnlyItsCliAndJoinsIndependentCleanup()
    {
        var worker = FakeProcess.Hanging();
        var launcher = new FakeLauncher { Worker = worker };
        var runner = Runner(launcher);
        var work = runner.RunAsync("native"u8.ToArray(), CancellationToken.None);
        await launcher.WorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkerProcessFailure.Canceled, (await work).Failure);
        Assert.True(worker.Killed);
        Assert.True(worker.Disposed);
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task CanceledHostWaiterCannotReplaceOrCancelTheOriginalDrain()
    {
        var creation = FakeProcess.GatedCreation();
        var launcher = new FakeLauncher { Create = creation };
        var runner = Runner(launcher);
        var lifetime = new InspectionWorkerHostLifetime(runner);
        var work = runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await creation.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var hostWaiter = lifetime.StoppingAsync(cancellation.Token);
        var original = runner.StopAsync();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostWaiter);
            Assert.False(original.IsCompleted);
            Assert.False(creation.FirstExitWaitToken.IsCancellationRequested);
            Assert.Same(original, runner.StopAsync());
        }
        finally { creation.Settle(); }
        await lifetime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkerProcessFailure.Canceled, (await work).Failure);
        await runner.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProviderDisposalCannotReleaseTheRunnerBeforeItsOriginalCreateEnds(bool interchange, bool deferDisposal)
    {
        var creation = FakeProcess.GatedCreation();
        var launcher = new FakeLauncher { Create = creation };
        var services = new ServiceCollection();
        InspectionWorkerHostConfiguration.Configure(services, new("/usr/bin/docker", ImageId), launcher);
        services.AddSingleton(_ => new ProviderDisposalGate());
        var provider = services.BuildServiceProvider();
        var inspection = provider.GetRequiredService<DockerInspectionWorkerRunner>();
        Assert.Same(inspection, provider.GetRequiredService<IInspectionWorkerRunner>());
        var conversion = provider.GetRequiredKeyedService<IInspectionWorkerRunner>("interchange");
        Assert.NotSame(inspection, conversion);
        Assert.Equal(2, provider.GetServices<IHostedService>().OfType<InspectionWorkerHostLifetime>().Count());
        var runner = interchange ? conversion : inspection;
        var work = runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await creation.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        // A later singleton can defer the provider before it reaches either runner.
        var gate = deferDisposal ? provider.GetRequiredService<ProviderDisposalGate>() : null;
        var disposal = provider.DisposeAsync().AsTask();
        try
        {
            if (gate is not null)
            {
                await gate.Entered.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.True(((DockerInspectionWorkerRunner)runner).Available);
                gate.Release();
            }
            // DisposeAsync walks singletons asynchronously in reverse creation order.
            // Do not release create until disposal has actually stopped this runner.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (((DockerInspectionWorkerRunner)runner).Available) await Task.Delay(1, deadline.Token);
            Assert.False(disposal.IsCompleted);
            Assert.False(creation.Disposed);
            Assert.False(creation.FirstExitWaitToken.IsCancellationRequested);
        }
        finally
        {
            gate?.Release();
            creation.Settle();
        }
        await disposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkerProcessFailure.Canceled, (await work).Failure);
        Assert.Empty(launcher.Worker.Input.ToArray());
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "inspect" or "start");
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
    }

    [Fact]
    public async Task ShutdownBeforeFirstUseIsTerminalWithoutAnyProcessOrNewBudget()
    {
        var launcher = new FakeLauncher();
        var runner = Runner(launcher);
        var original = runner.StopAsync();
        await original;
        await runner.DisposeAsync();
        Assert.Same(original, runner.StopAsync());
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
            (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        Assert.Empty(launcher.Commands);
    }
}
