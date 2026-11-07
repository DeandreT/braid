using System.Net;
using System.Net.Sockets;
using Weft.MappingBackends.Workers;
using static Weft.Tests.FerruleServiceTransportTests;
using static Weft.Tests.InspectionTransportTests;

namespace Weft.Tests;

/// <summary>
/// The Weft-side Ferrule service runner against in-process service replicas with scripted runners, alone and behind a
/// round-robin layer-4 balancer like a platform's replica load balancing.
/// </summary>
public sealed class FerruleServiceRunnerTests
{
    private const string Identity = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task ExactFramesAndAnswersCrossTheServiceUnchanged()
    {
        await using var service = await ServiceCase.StartAsync();
        service.Preview.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult([.. frame.ToArray(), 42], WorkerProcessFailure.None));
        await using var runner = Runner(service, FerruleServiceProtocol.PreviewProfile);
        var frame = new byte[DockerWorkerProfile.Inspection.MaximumRequestBytes + 7];
        Random.Shared.NextBytes(frame);
        var result = await runner.RunAsync(frame, CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.Equal([.. frame, 42], result.Output);
        Assert.Equal(frame, Assert.Single(service.Preview.Frames));
        Assert.Empty(service.Inspection.Frames);
        Assert.Equal(Identity, runner.BuildIdentity);

        foreach (var failure in Enum.GetValues<WorkerProcessFailure>().Where(value => value is not (WorkerProcessFailure.None or WorkerProcessFailure.CleanupFailed)))
        {
            service.Preview.Behavior = (_, _) => Task.FromResult(new WorkerProcessResult([1], failure));
            var failed = await runner.RunAsync(new byte[] { 1 }, CancellationToken.None);
            Assert.Equal(failure, failed.Failure);
            Assert.Empty(failed.Output);
        }
        Assert.Throws<ArgumentException>(() =>
            runner.RunAsync(new byte[DockerWorkerProfile.Preview.MaximumRequestBytes + 1], CancellationToken.None).GetAwaiter().GetResult());
        await runner.StopAsync();
    }

    [Fact]
    public async Task OnlyThePinnedServiceWithTheExpectedIdentityReceivesFrames()
    {
        await using var service = await ServiceCase.StartAsync();
        using var stranger = TransportCase.Certificate("CN=Unpinned client");
        foreach (var options in new[]
        {
            new PinnedServiceConnectorOptions(service.Endpoint, service.ServerPin, stranger),
            new PinnedServiceConnectorOptions(service.Endpoint, TransportCase.Pin(stranger), service.ClientCertificate)
        })
        {
            await using var refused = new FerruleServiceRunner(new(new PinnedServiceConnector(options), FerruleServiceProtocol.InspectionProfile, Identity)
            {
                RetryDelay = TimeSpan.FromMilliseconds(10)
            });
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await refused.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            await refused.StopAsync();
        }

        await using var otherBuild = new FerruleServiceRunner(new(Connector(service), FerruleServiceProtocol.InspectionProfile, "sha256:" + new string('2', 64)));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await otherBuild.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Empty(service.Inspection.Frames);

        // Refusals before execution keep admission open.
        await using var runner = Runner(service, FerruleServiceProtocol.InspectionProfile);
        Assert.Equal(new byte[] { 3 }, (await runner.RunAsync(new byte[] { 3 }, CancellationToken.None)).Output);
        await otherBuild.StopAsync();
        await runner.StopAsync();
    }

    [Fact]
    public async Task BusyRefusalsRetryWithFreshOperationsUntilAReplicaAdmitsThem()
    {
        await using var service = await ServiceCase.StartAsync(concurrency: 1, capacity: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Inspection.Behavior = async (frame, _) =>
        {
            if (frame.Span[0] == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
            return new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None);
        };
        await using var runner = Runner(service, FerruleServiceProtocol.InspectionProfile, retryDelay: TimeSpan.FromMilliseconds(300));
        var held = runner.RunAsync(new byte[] { 1 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Every attempt is refused while the only slot is held: nothing else ran and admission stays open.
        await using (var exhausted = Runner(service, FerruleServiceProtocol.InspectionProfile, retryDelay: TimeSpan.FromMilliseconds(10)))
        {
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await exhausted.RunAsync(new byte[] { 2 }, CancellationToken.None)).Failure);
            await exhausted.StopAsync();
        }
        Assert.Single(service.Inspection.Frames);

        // A later attempt is admitted once the slot frees.
        var waiting = runner.RunAsync(new byte[] { 3 }, CancellationToken.None);
        await Task.Delay(150);
        release.TrySetResult();
        Assert.Equal(new byte[] { 1 }, (await held.WaitAsync(TimeSpan.FromSeconds(10))).Output);
        Assert.Equal(new byte[] { 3 }, (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Output);
        Assert.Equal([1, 3], service.Inspection.Frames.Select(frame => frame[0]));
        await runner.StopAsync();
    }

    [Fact]
    public async Task BehindABalancerRefusedOperationsReachAFreeReplicaAndCancellationReachesItsOwn()
    {
        await using var first = await ServiceCase.StartAsync(concurrency: 1, capacity: 1);
        await using var second = await ServiceCase.StartAsync(concurrency: 1, capacity: 1, serverCertificate: first.ServerCertificate,
            clientCertificate: first.ClientCertificate);
        await using var balancer = RoundRobinBalancer.Start(first.Server.Port, second.Server.Port);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldStarted = new TaskCompletionSource<ScriptedRunner>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = 0;
        foreach (var replica in new[] { first, second })
        {
            var self = replica.Inspection;
            replica.Inspection.Behavior = async (frame, token) =>
            {
                if (frame.Span[0] != 1) return new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None);
                heldStarted.TrySetResult(self);
                try { await held.Task.WaitAsync(token); }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceled);
                    return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
                }
                return new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None);
            };
        }

        var connector = new PinnedServiceConnector(new(new Uri("https://127.0.0.1:" + balancer.Port), first.ServerPin, first.ClientCertificate));
        await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, Identity)
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
            Attempts = 4
        });
        using var cancellation = new CancellationTokenSource();
        var holding = runner.RunAsync(new byte[] { 1 }, cancellation.Token);
        var busyReplica = await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Whichever connection lands on the busy replica is refused; a fresh operation reaches the free one.
        for (var index = 0; index < 4; index++)
            Assert.Equal(new byte[] { (byte)(10 + index) }, (await runner.RunAsync(new byte[] { (byte)(10 + index) }, CancellationToken.None)).Output);
        var freeReplica = busyReplica == first.Inspection ? second.Inspection : first.Inspection;
        Assert.Equal(4, freeReplica.Frames.Count);
        Assert.Single(busyReplica.Frames);

        // The cancellation travels on the held operation's own connection, so it reaches the replica running it.
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await holding.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Equal(1, Volatile.Read(ref canceled));
        Assert.True(balancer.Connections >= 6);
        await runner.StopAsync();
    }

    [Fact]
    public async Task CallerCancellationReturnsOnlyAfterTheServiceSettled()
    {
        await using var service = await ServiceCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = 0;
        service.Inspection.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            await Task.Delay(300, CancellationToken.None);
            Interlocked.Exchange(ref settled, 1);
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        await using var runner = Runner(service, FerruleServiceProtocol.InspectionProfile);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(new byte[] { 1 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Equal(1, Volatile.Read(ref settled));

        using var before = new CancellationTokenSource();
        await before.CancelAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await runner.RunAsync(new byte[] { 2 }, before.Token)).Failure);
        service.Inspection.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None));
        Assert.Equal(new byte[] { 3 }, (await runner.RunAsync(new byte[] { 3 }, CancellationToken.None)).Output);
        Assert.Equal(2, service.Inspection.Frames.Count);
        await runner.StopAsync();
    }

    [Fact]
    public async Task ExpiredResponseBoundCancelsRemotelyAndReportsTimeout()
    {
        await using var service = await ServiceCase.StartAsync();
        service.Inspection.Behavior = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        await using var runner = new FerruleServiceRunner(new(Connector(service), FerruleServiceProtocol.InspectionProfile, Identity)
        {
            ResponseBound = TimeSpan.FromMilliseconds(500)
        });
        Assert.Equal(WorkerProcessFailure.TimedOut, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await runner.StopAsync();
    }

    [Fact]
    public async Task TheCallersCancellationSettlementBoundsTheRequestAndTheWaitTogether()
    {
        await using var service = await ServiceCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Inspection.Behavior = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        // The default settlement would wait out a full execution wall; the caller's margin is shorter.
        var options = new FerruleServiceRunnerOptions(Connector(service), FerruleServiceProtocol.InspectionProfile, Identity)
        {
            CancellationSettlement = TimeSpan.FromMilliseconds(600)
        };
        Assert.True(options.SettlementBound > TimeSpan.FromSeconds(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FerruleServiceRunnerOptions(Connector(service), FerruleServiceProtocol.InspectionProfile, Identity)
        {
            CancellationSettlement = TimeSpan.Zero
        });
        var runner = new FerruleServiceRunner(options);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(new byte[] { 1 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Settlement took {timer.Elapsed}.");
        Assert.False(runner.Available);
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
    }

    [Fact]
    public async Task UnconfirmedSettlementIsCleanupFailureThatClosesAdmissionAndFaultsStop()
    {
        await using var service = await ServiceCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Inspection.Behavior = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        var runner = new FerruleServiceRunner(new(Connector(service), FerruleServiceProtocol.InspectionProfile, Identity)
        {
            SettlementBound = TimeSpan.FromMilliseconds(400)
        });
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(new byte[] { 1 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 2 }, CancellationToken.None)).Failure);
        Assert.Single(service.Inspection.Frames);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
        release.TrySetResult();

        // A cleanup failure reported by the service closes admission too.
        service.Inspection.Behavior = (_, _) => Task.FromResult(new WorkerProcessResult([], WorkerProcessFailure.CleanupFailed));
        var reported = Runner(service, FerruleServiceProtocol.InspectionProfile);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await reported.RunAsync(new byte[] { 3 }, CancellationToken.None)).Failure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await reported.RunAsync(new byte[] { 4 }, CancellationToken.None)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(reported.StopAsync);
    }

    [Fact]
    public async Task LostConnectionAfterDeliveryIsUncertainAndTheServiceCancelsTheOperation()
    {
        await using var service = await ServiceCase.StartAsync();
        await using var balancer = RoundRobinBalancer.Start(service.Server.Port);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Inspection.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { observed.TrySetResult(); }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        var connector = new PinnedServiceConnector(new(new Uri("https://127.0.0.1:" + balancer.Port), service.ServerPin, service.ClientCertificate));
        var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, Identity)
        {
            SettlementBound = TimeSpan.FromMilliseconds(500),
            CancelRequestBound = TimeSpan.FromMilliseconds(500)
        });
        var running = runner.RunAsync(new byte[] { 4, 5, 6 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        balancer.Sever();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
    }

    [Fact]
    public async Task StoppingTheRunnerCancelsAndJoinsOutstandingOperations()
    {
        await using var service = await ServiceCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Preview.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        var runner = Runner(service, FerruleServiceProtocol.PreviewProfile);
        var running = runner.RunAsync(new byte[] { 1 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = runner.StopAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task StalledSettlementStaysUncertainEvenWhenTheServiceEventuallyAnswersAndTheRunnerReopens()
    {
        await using var service = await ServiceCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Inspection.Behavior = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new WorkerProcessResult([99], WorkerProcessFailure.None);
        };
        await using var runner = new FerruleServiceRunner(new(Connector(service), "inspection", Identity)
        {
            CancellationSettlement = TimeSpan.FromMilliseconds(100),
            QuarantineBound = TimeSpan.FromMilliseconds(500)
        });
        using var cancellation = new CancellationTokenSource();
        var pending = runner.RunAsync(new byte[] { 1 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var uncertain = await pending;
        Assert.Equal(WorkerProcessFailure.CleanupFailed, uncertain.Failure);
        Assert.Empty(uncertain.Output);
        release.TrySetResult();
        service.Inspection.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None));
        await FerruleServiceRecoveryTests.Reopened(runner);
        Assert.Same(uncertain, await pending);
        Assert.Equal(new byte[] { 2 }, (await runner.RunAsync(new byte[] { 2 }, default)).Output);
        Assert.Equal(2, service.Inspection.Frames.Count);
    }

    [Fact]
    public void SelectionAndEndpointSettingsAreExplicitAndCanonical()
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            [FerruleServiceEndpoint.Url] = "https://ferrule.internal:8443",
            [FerruleServiceEndpoint.CertificatePin] = new string('a', 64),
            [FerruleServiceEndpoint.Identity] = Identity
        };
        string? Read(string name) => values.GetValueOrDefault(name);
        var endpoint = FerruleServiceEndpoint.Read(Read);
        Assert.Equal(new Uri("https://ferrule.internal:8443"), endpoint.Endpoint);
        Assert.Equal(Identity, endpoint.BuildIdentity);
        Assert.True(FerruleServiceEndpoint.Configured(Read));
        Assert.Equal("FerruleServiceEndpoint", endpoint.ToString());
        foreach (var (name, value) in new (string, string?)[]
        {
            (FerruleServiceEndpoint.Url, null), (FerruleServiceEndpoint.Url, "http://ferrule:8443"), (FerruleServiceEndpoint.Url, "https://ferrule:8443/v1"),
            (FerruleServiceEndpoint.CertificatePin, null), (FerruleServiceEndpoint.CertificatePin, new string('A', 64)),
            (FerruleServiceEndpoint.Identity, null), (FerruleServiceEndpoint.Identity, Identity.ToUpperInvariant()), (FerruleServiceEndpoint.Identity, "ferrule:latest")
        })
        {
            var copy = new Dictionary<string, string?>(values) { [name] = value };
            Assert.Throws<InvalidOperationException>(() => FerruleServiceEndpoint.Read(key => copy.GetValueOrDefault(key)));
        }

        Assert.Equal(FerruleRunnerKind.Docker, FerruleRunnerSelection.Read(_ => null, FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service));
        Assert.Equal(FerruleRunnerKind.Service, FerruleRunnerSelection.Read(_ => "service", FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service));
        Assert.Throws<InvalidOperationException>(() => FerruleRunnerSelection.Read(_ => "worker", FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service));
        Assert.Throws<InvalidOperationException>(() => FerruleRunnerSelection.Read(_ => "Service", FerruleRunnerKind.Docker, FerruleRunnerKind.Docker, FerruleRunnerKind.Service));
        Assert.Throws<ArgumentException>(() => new FerruleServiceRunnerOptions(new PinnedServiceConnector(new(new Uri("https://f:1"), new string('a', 64),
            TransportCase.Certificate("CN=x"))), "build", Identity));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task UnexpectedStatusAfterFrameDeliveryIsUncertainEvenWithAServiceHeader(HttpStatusCode status)
    {
        var connector = new StatusConnector(status);
        var runner = new FerruleServiceRunner(new(connector, "inspection", Identity));
        var result = await runner.RunAsync(new byte[] { 1 }, CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
        Assert.Empty(result.Output);
        Assert.Equal(1, connector.Deliveries);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 2 }, CancellationToken.None)).Failure);
        Assert.Equal(1, connector.Deliveries);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.DisposeAsync());
    }

    [Fact]
    public void ResponseBudgetCoversBothBoundedBodyReadAndWorkerExecution()
    {
        var options = new FerruleServiceRunnerOptions(new StatusConnector(HttpStatusCode.BadRequest), "preview", Identity);
        Assert.Equal(FerruleServiceRunnerOptions.MaximumServiceAdmissionWait + DockerWorkerProfile.Preview.MaximumWallTimeout * 2 +
            ProcessWorkerOptions.KillTimeout + TimeSpan.FromSeconds(5), options.ResponseBound);
        var compile = new FerruleServiceRunnerOptions(new StatusConnector(HttpStatusCode.BadRequest), "compile", Identity, ServiceCase.Toolchain);
        Assert.Equal(TimeSpan.FromSeconds(20 + 240 + 2 + 5), compile.ResponseBound);
        Assert.Equal(TimeSpan.FromSeconds(120 + 2 + 3), compile.SettlementBound);
    }

    [Fact]
    public async Task CompileFramesTravelOnTheCompileProfileAndOnlyItBuilds()
    {
        await using var service = await ServiceCase.StartAsync(compile: true);
        service.Compile.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult([.. frame.ToArray(), 8], WorkerProcessFailure.None));
        await using var runner = Runner(service, FerruleServiceProtocol.CompileProfile);
        Assert.Equal(FerruleServiceProtocol.CompileProfile, runner.ProfileName);
        var frame = new byte[DockerWorkerProfile.Build.MaximumRequestBytes];
        Random.Shared.NextBytes(frame);
        var result = await runner.RunAsync(frame, CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.Equal([.. frame, 8], result.Output);
        Assert.Equal(frame, Assert.Single(service.Compile.Frames));
        Assert.Throws<ArgumentException>(() =>
            runner.RunAsync(new byte[DockerWorkerProfile.Build.MaximumRequestBytes + 1], CancellationToken.None).GetAwaiter().GetResult());

        // A service without compilation refuses the reserved profile before anything runs.
        await using var reserved = await ServiceCase.StartAsync();
        await using var refused = Runner(reserved, FerruleServiceProtocol.CompileProfile);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await refused.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Empty(reserved.Compile.Frames);

        // The build compiler reports the service identity as its engine build.
        var source = new Weft.MappingBackends.BuildNativeSource(new(new(Guid.NewGuid())), new NoAdmission(), new NoObjects());
        Assert.Equal(CompileWorkerIdentity.Combine(Identity, ServiceCase.Toolchain), new Weft.MappingBackends.FerruleBuildCompiler(runner, source).Backend.EngineBuild);
        await runner.StopAsync();
        await refused.StopAsync();
    }

    [Fact]
    public async Task AChangedToolchainWithTheSameNativeWorkerIsRefusedBeforeDelivery()
    {
        await using var changed = await ServiceCase.StartAsync(compile: true, compileToolchain: "sha256:" + new string('4', 64));
        await using var runner = Runner(changed, FerruleServiceProtocol.CompileProfile);
        var result = await runner.RunAsync(new byte[] { 1 }, CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, result.Failure);
        Assert.Empty(changed.Compile.Frames);
        Assert.NotEqual(Identity, runner.CompilationBuildIdentity);
        Assert.NotEqual(CompileWorkerIdentity.Combine(Identity, "sha256:" + new string('4', 64)), runner.CompilationBuildIdentity);
    }

    [Fact]
    public async Task AChangedToolchainOnTheAnswerCannotBeAcceptedAsACompletedCompile()
    {
        await using var service = await ServiceCase.StartAsync(compile: true);
        var connector = new ChangedCompileAnswer(Connector(service));
        var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompileProfile, Identity, ServiceCase.Toolchain));
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Single(service.Compile.Frames);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.DisposeAsync());
    }

    [Fact]
    public void ACompileRunnerRequiresAnExplicitToolchainPin()
    {
        var connector = new StatusConnector(HttpStatusCode.BadRequest);
        Assert.Throws<ArgumentException>(() => new FerruleServiceRunnerOptions(connector, FerruleServiceProtocol.CompileProfile, Identity));
        var inspection = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, Identity, ServiceCase.Toolchain));
        Assert.Equal(CompileWorkerIdentity.Combine(Identity, ServiceCase.Toolchain), inspection.BuildIdentity);
        Assert.Null(inspection.CompilationBuildIdentity);
    }

    private sealed class ChangedCompileAnswer(IFerruleServiceConnector inner) : IFerruleServiceConnector
    {
        public IFerruleServiceChannel Open() => new Channel(inner.Open());
        private sealed class Channel(IFerruleServiceChannel inner) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var response = await inner.SendAsync(request, token);
                if (request.Method == HttpMethod.Post && response.StatusCode == HttpStatusCode.OK)
                {
                    response.Headers.Remove(FerruleServiceProtocol.CompileToolchainHeader);
                    response.Headers.Add(FerruleServiceProtocol.CompileToolchainHeader, "sha256:" + new string('4', 64));
                }
                return response;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class NoAdmission : Weft.Deployment.IBuildAdmissionReader
    {
        public Task<Weft.Deployment.BuildAdmission?> ReadAsync(Weft.Application.ActorContext actor, Weft.Deployment.BuildIntent intent, CancellationToken token = default) =>
            Task.FromResult<Weft.Deployment.BuildAdmission?>(null);
    }

    private sealed class NoObjects : Weft.Application.IPrivateObjectStore
    {
        public Task<byte[]> ReadVerifiedAsync(Weft.Domain.RevisionScope scope, Weft.Domain.PrivateObjectReference value, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<Weft.Application.PrivateObjectPublishResult> PublishCreateOnlyAsync(Weft.Domain.RevisionScope scope, Weft.Domain.PrivateObjectReference value,
            Stream source, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class StatusConnector(HttpStatusCode status) : IFerruleServiceConnector
    {
        internal int Deliveries;
        public IFerruleServiceChannel Open() => new Channel(this, status);

        private sealed class Channel(StatusConnector owner, HttpStatusCode status) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get)
                {
                    var ready = new HttpResponseMessage(HttpStatusCode.NoContent);
                    ready.Headers.Add(InspectionTransport.ImageHeader, Identity);
                    ready.Headers.Add(FerruleServiceProtocol.ProtocolHeader, FerruleServiceProtocol.Version);
                    return ready;
                }
                await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
                Interlocked.Increment(ref owner.Deliveries);
                var response = new HttpResponseMessage(status);
                response.Headers.Add(FerruleServiceProtocol.ProtocolHeader, FerruleServiceProtocol.Version);
                return response;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static PinnedServiceConnector Connector(ServiceCase service) =>
        new(new(service.Endpoint, service.ServerPin, service.ClientCertificate));

    private static FerruleServiceRunner Runner(ServiceCase service, string profile, TimeSpan? retryDelay = null) =>
        new(new(Connector(service), profile, Identity, profile == FerruleServiceProtocol.CompileProfile ? ServiceCase.Toolchain : null)
        { RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50) });

    /// <summary>A loopback layer-4 balancer that assigns each new connection to the next backend.</summary>
    internal sealed class RoundRobinBalancer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly int[] backends;
        private readonly List<Socket> sockets = [];
        private Task? accepting;
        private int next;
        private int connections;
        private bool severed;

        private RoundRobinBalancer(int[] backends) => this.backends = backends;
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal int Connections => Volatile.Read(ref connections);

        internal static RoundRobinBalancer Start(params int[] backends)
        {
            var balancer = new RoundRobinBalancer(backends);
            balancer.listener.Start();
            balancer.accepting = balancer.AcceptAsync();
            return balancer;
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                Socket inbound;
                try { inbound = await listener.AcceptSocketAsync(); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref connections);
                var outbound = new Socket(SocketType.Stream, ProtocolType.Tcp);
                lock (sockets)
                {
                    sockets.Add(inbound);
                    sockets.Add(outbound);
                    if (severed) { Close(inbound); Close(outbound); continue; }
                }
                var backend = backends[(Interlocked.Increment(ref next) - 1) % backends.Length];
                try { await outbound.ConnectAsync(IPAddress.Loopback, backend); }
                catch (SocketException) { Close(inbound); Close(outbound); continue; }
                _ = PumpAsync(inbound, outbound);
                _ = PumpAsync(outbound, inbound);
            }
        }

        private static async Task PumpAsync(Socket from, Socket to)
        {
            var buffer = new byte[65536];
            try
            {
                while (true)
                {
                    var count = await from.ReceiveAsync(buffer, SocketFlags.None);
                    if (count == 0) break;
                    await to.SendAsync(buffer.AsMemory(0, count), SocketFlags.None);
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
            Close(to);
        }

        internal void Sever()
        {
            lock (sockets)
            {
                severed = true;
                foreach (var socket in sockets) Close(socket);
            }
        }

        private static void Close(Socket socket)
        {
            try { socket.Close(0); }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            Sever();
            if (accepting is not null) await accepting;
        }
    }
}
