using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Process-seam recovery after unconfirmed cleanup; not actual Docker qualification.</summary>
public sealed partial class FerruleDockerRunnerTests
{
    [Fact]
    public async Task SlowRemovalClosesAdmissionUntilTheReaperConfirmsTheExactIdThenReopens()
    {
        // The default two-second cleanup budget expires on the original removal and bounds each reaper attempt.
        var launcher = new FakeLauncher { Remove = FakeProcess.Hanging(), Worker = TextProcess("private-result") };
        var runner = RecoveringRunner(launcher);

        var failed = await runner.RunAsync("native"u8.ToArray(), CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, failed.Failure);
        Assert.Empty(failed.Output);
        var reaped = FakeProcess.GatedControlOutput(ContainerId + "\n");
        launcher.Remove = reaped;
        Assert.False(runner.Available);
        Assert.False(runner.CanExecute);
        await reaped.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var count = launcher.Commands.Count;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runner.RunAsync("queued"u8.ToArray(), CancellationToken.None)));
        Assert.All(concurrent, result => Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure));
        Assert.Equal(count, launcher.Commands.Count);
        Assert.False(runner.Available);

        reaped.Settle();
        await WaitUntilAsync(() => runner.Available);

        var reaperCommands = launcher.Commands.Skip(7).ToArray();
        Assert.All(reaperCommands, command => Assert.True(Operation(command) is "ownership-inspect" or "rm"));
        Assert.All(reaperCommands, command => Assert.Equal(ContainerId, command.ArgumentList.Last()));
        Assert.All(reaperCommands, command => Assert.False(Directory.Exists(command.WorkingDirectory)));
        Assert.DoesNotContain(reaperCommands, command => Operation(command) == "create");
        Assert.Equal(["container", "rm", "--force", "--volumes", ContainerId], launcher.Commands.Last().ArgumentList.Skip(4));
        launcher.Remove = null;
        launcher.Worker = new FakeProcess([]);
        Assert.Equal(WorkerProcessFailure.None, (await runner.RunAsync("next"u8.ToArray(), CancellationToken.None)).Failure);
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task ReaperNeverRemovesAContainerItDoesNotOwnAndShutdownStillFaults()
    {
        var observations = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher
        {
            // The acknowledged ID now resolves to another operation's container.
            OwnershipInspection = (_, name, owner) =>
            {
                if (Interlocked.Increment(ref observations) >= 4) observed.TrySetResult();
                return TextProcess(OwnedMetadata(name, OtherOwner(owner)));
            }
        };
        var runner = RecoveringRunner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(runner.Available);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DisposeAsync().AsTask());
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) == "rm");
        Assert.All(launcher.Commands.Skip(6), command => Assert.Equal("ownership-inspect", Operation(command)));
    }

    [Fact]
    public async Task LostCreateAcknowledgementWaitsForTheOriginalContainerWithoutCreatingAReplacement()
    {
        var published = false;
        var observations = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher
        {
            Create = TextProcess(""),
            OwnershipInspection = (target, name, owner) =>
            {
                if (Interlocked.Increment(ref observations) >= 4) observed.TrySetResult();
                return Volatile.Read(ref published) ? TextProcess(OwnedMetadata(name, owner)) : MissingProcess(target);
            }
        };
        var runner = RecoveringRunner(launcher);

        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync("native"u8.ToArray(), CancellationToken.None)).Failure);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runner.Available);
        Assert.Single(launcher.CreatedNames);
        Volatile.Write(ref published, true);
        await WaitUntilAsync(() => runner.Available);

        Assert.Single(launcher.CreatedNames);
        Assert.All(launcher.Commands.Skip(3).SkipLast(1), command =>
        {
            Assert.Equal("ownership-inspect", Operation(command));
            Assert.Equal(launcher.CreatedNames[0], command.ArgumentList.Last());
        });
        Assert.Equal("rm", Operation(launcher.Commands.Last()));
        Assert.Equal(ContainerId, launcher.Commands.Last().ArgumentList.Last());
        Assert.Empty(launcher.Worker.Input.ToArray());
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task MissingNameAfterLostAcknowledgementKeepsRecoveryClosedAndShutdownFaults()
    {
        var observations = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher
        {
            Create = TextProcess(""),
            OwnershipInspection = (target, _, _) =>
            {
                if (Interlocked.Increment(ref observations) >= 4) observed.TrySetResult();
                return MissingProcess(target);
            }
        };
        var runner = RecoveringRunner(launcher);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync("native"u8.ToArray(), CancellationToken.None)).Failure);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runner.CanExecute);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync("next"u8.ToArray(), CancellationToken.None)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DisposeAsync().AsTask());
        Assert.Single(launcher.CreatedNames);
        Assert.DoesNotContain(launcher.Commands, command => Operation(command) is "start" or "rm");
        Assert.Empty(launcher.Worker.Input.ToArray());
    }

    [Fact]
    public async Task ConfiguredCreateBudgetSettlesSlowCreationOutsideTheWallBudget()
    {
        var creation = FakeProcess.GatedCreation();
        var launcher = new FakeLauncher { Create = creation };
        var runner = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId, TimeSpan.FromMilliseconds(500),
            budgets: new(Create: TimeSpan.FromSeconds(5))), launcher);
        var work = runner.RunAsync("native"u8.ToArray(), CancellationToken.None);
        await creation.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(1000);
        Assert.False(creation.Killed);
        creation.Settle();

        Assert.Equal(WorkerProcessFailure.None, (await work.WaitAsync(TimeSpan.FromSeconds(2))).Failure);
        Assert.Equal("native"u8.ToArray(), launcher.Worker.Input.ToArray());
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task ConfiguredCleanupBudgetAllowsSlowRemovalWithoutStartingRecovery()
    {
        var removal = FakeProcess.GatedControlOutput(ContainerId + "\n");
        var launcher = new FakeLauncher { Remove = removal };
        var runner = RecoveringRunner(launcher, cleanup: TimeSpan.FromSeconds(5));
        var work = runner.RunAsync("native"u8.ToArray(), CancellationToken.None);
        await removal.ExitWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(DockerInspectionWorkerOptions.DefaultCleanupTimeout + TimeSpan.FromMilliseconds(200));
        Assert.False(work.IsCompleted);
        Assert.False(removal.Killed);
        removal.Settle();

        Assert.Equal(WorkerProcessFailure.None, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        Assert.True(runner.Available);
        Assert.Equal(["info", "image", "create", "inspect", "start", "ownership-inspect", "rm"], launcher.Commands.Select(Operation));
        await runner.DisposeAsync();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("1", "1", 1)]
    [InlineData("120", "120", 120)]
    public void DockerBudgetsReadWholeSecondsWithinTheirBound(string? create, string? cleanup, int? seconds)
    {
        var budgets = DockerRunnerBudgets.Read(name => name == DockerRunnerBudgets.CreateSetting ? create : cleanup);
        var expected = seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null;
        Assert.Equal(expected, budgets.Create);
        Assert.Equal(expected, budgets.Cleanup);
        Assert.Equal(expected ?? DockerInspectionWorkerOptions.DefaultCleanupTimeout,
            new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId, budgets: budgets).CleanupTimeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("030")]
    [InlineData("121")]
    [InlineData("1000")]
    [InlineData("-1")]
    [InlineData("2s")]
    public void DockerBudgetsRefuseValuesOutsideTheirBound(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DockerRunnerBudgets.Read(name => name == DockerRunnerBudgets.CleanupSetting ? value : null));
        Assert.Throws<InvalidOperationException>(() => DockerRunnerBudgets.Read(name => name == DockerRunnerBudgets.CreateSetting ? value : null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId,
            budgets: new(Cleanup: TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId,
            budgets: new(Create: DockerRunnerBudgets.Maximum + TimeSpan.FromSeconds(1))));
    }

    private static DockerInspectionWorkerRunner RecoveringRunner(FakeLauncher launcher, TimeSpan? cleanup = null, TimeSpan? initialDelay = null) =>
        new(new DockerInspectionWorkerOptions("/usr/bin/docker", ImageId, budgets: new(Cleanup: cleanup))
        {
            ReaperInitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(10),
            ReaperMaximumDelay = TimeSpan.FromMilliseconds(50)
        }, launcher);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }
}
