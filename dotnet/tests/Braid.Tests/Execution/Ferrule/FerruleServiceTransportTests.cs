using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Braid.MappingBackends.Workers;
using Braid.WorkerTransport;
using static Braid.Tests.InspectionTransportTests;

namespace Braid.Tests;

/// <summary>
/// <c>braid.ferrule-service.v1</c> routes of the shared worker transport against scripted runners: profile routing,
/// per-replica concurrency with retryable refusals, cancellation on the operation's own HTTP/2 connection,
/// capability reporting, probes and the platform-sandbox listener.
/// </summary>
public sealed class FerruleServiceTransportTests
{
    private const string Identity = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task ReplicaIdentityIsStablePerProcessDifferentAcrossReplicasAndPresentOnRefusals()
    {
        await using var first = await ServiceCase.StartAsync();
        await using var second = await ServiceCase.StartAsync();
        using var http = first.Client();
        using var other = second.Client();
        using var ready = await http.GetAsync(FerruleServiceProtocol.ReadyPath);
        var identity = Assert.Single(ready.Headers.GetValues(FerruleServiceProtocol.ReplicaHeader));
        Assert.Matches("^[0-9a-f]{32}$", identity);
        using var otherReady = await other.GetAsync(FerruleServiceProtocol.ReadyPath);
        Assert.NotEqual(identity, Assert.Single(otherReady.Headers.GetValues(FerruleServiceProtocol.ReplicaHeader)));
        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Get, FerruleServiceProtocol.CapabilitiesPath),
            new HttpRequestMessage(HttpMethod.Get, FerruleServiceProtocol.ReadinessPath),
            Operation("unknown", [1]), Operation(FerruleServiceProtocol.CompileProfile, [1]),
            Operation(FerruleServiceProtocol.InspectionProfile, [1]),
            new HttpRequestMessage(HttpMethod.Post, FerruleServiceProtocol.CancelPath("inspection", "invalid"))
        })
        {
            using (request)
            using (var response = await http.SendAsync(request))
                Assert.Equal(identity, Assert.Single(response.Headers.GetValues(FerruleServiceProtocol.ReplicaHeader)));
        }
    }

    [Fact]
    public async Task ProfilesRouteExactFramesToTheirOwnRunners()
    {
        await using var test = await ServiceCase.StartAsync();
        using var http = test.Client();
        using (var ready = await http.GetAsync(FerruleServiceProtocol.ReadyPath))
        {
            Assert.Equal(HttpStatusCode.NoContent, ready.StatusCode);
            Assert.Equal(HttpVersion.Version20, ready.Version);
            Assert.Equal(Identity, Assert.Single(ready.Headers.GetValues(InspectionTransport.ImageHeader)));
            Assert.Equal(FerruleServiceProtocol.Version, Assert.Single(ready.Headers.GetValues(FerruleServiceProtocol.ProtocolHeader)));
        }

        test.Inspection.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult([.. frame.ToArray(), 1], WorkerProcessFailure.None));
        test.Preview.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult([.. frame.ToArray(), 2], WorkerProcessFailure.None));
        using (var inspected = await http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, [7, 8])))
        {
            Assert.Equal("none", Assert.Single(inspected.Headers.GetValues(InspectionTransport.ResultHeader)));
            Assert.Equal(new byte[] { 7, 8, 1 }, await inspected.Content.ReadAsByteArrayAsync());
        }
        using (var previewed = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, [9])))
            Assert.Equal(new byte[] { 9, 2 }, await previewed.Content.ReadAsByteArrayAsync());
        Assert.Equal(new byte[] { 7, 8 }, Assert.Single(test.Inspection.Frames));
        Assert.Equal(new byte[] { 9 }, Assert.Single(test.Preview.Frames));

        // A preview frame larger than the inspection bound is admitted only on the preview route.
        var large = new byte[DockerWorkerProfile.Inspection.MaximumRequestBytes + 1];
        using (var refused = await http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, large)))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var admitted = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, large)))
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        using (var reserved = await http.SendAsync(Operation(FerruleServiceProtocol.CompileProfile, [1])))
            Assert.Equal(HttpStatusCode.NotImplemented, reserved.StatusCode);
        using (var unknown = await http.SendAsync(Operation("build", [1])))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Single(test.Inspection.Frames);
        Assert.Equal(2, test.Preview.Frames.Count);

        // The inspection transport routes stay available, so an existing pinned client works unchanged.
        await using var legacy = new RemoteInspectionWorkerRunner(new(test.Endpoint, test.ServerPin, test.ClientCertificate, Identity));
        var answered = await legacy.RunAsync(new byte[] { 5 }, CancellationToken.None);
        Assert.Equal(new byte[] { 5, 1 }, answered.Output);
        await legacy.StopAsync();
    }

    [Fact]
    public async Task CapabilitiesReportTheProtocolProfilesAndReplicaLimits()
    {
        await using var test = await ServiceCase.StartAsync(concurrency: 3, capacity: 5);
        using var http = test.Client();
        using var response = await http.GetAsync(FerruleServiceProtocol.CapabilitiesPath);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var root = document.RootElement;
        Assert.Equal(FerruleServiceProtocol.Version, root.GetProperty("protocol").GetString());
        Assert.Equal(Identity, root.GetProperty("buildIdentity").GetString());
        Assert.Equal("9149905759b4f65baf043b975624e45805a50142", root.GetProperty("engineRevision").GetString());
        Assert.Equal(3, root.GetProperty("concurrency").GetInt32());
        Assert.Equal(5, root.GetProperty("capacity").GetInt32());
        var profiles = root.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(["inspection", "preview"], profiles.Select(profile => profile.GetProperty("name").GetString()));
        Assert.Equal(DockerWorkerProfile.Preview.MaximumRequestBytes, profiles[1].GetProperty("maximumRequestBytes").GetInt32());
        Assert.Equal(268_435_456, profiles[0].GetProperty("memoryBytes").GetInt64());
        Assert.Equal(30, profiles[1].GetProperty("wallSeconds").GetInt32());
        Assert.Equal(["compile", "execute"], root.GetProperty("reserved").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task EnabledCompilationIsServedWithItsToolchainAndTheBuildProfilesBounds()
    {
        await using var test = await ServiceCase.StartAsync(compile: true);
        using var http = test.Client();
        test.Compile.Behavior = (frame, _) => Task.FromResult(new WorkerProcessResult([.. frame.ToArray(), 3], WorkerProcessFailure.None));
        using (var compiled = await http.SendAsync(Operation(FerruleServiceProtocol.CompileProfile, [4])))
        {
            Assert.Equal("none", Assert.Single(compiled.Headers.GetValues(InspectionTransport.ResultHeader)));
            Assert.Equal(new byte[] { 4, 3 }, await compiled.Content.ReadAsByteArrayAsync());
        }
        var largest = new byte[DockerWorkerProfile.Build.MaximumRequestBytes];
        using (var admitted = await http.SendAsync(Operation(FerruleServiceProtocol.CompileProfile, largest)))
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        using (var refused = await http.SendAsync(Operation(FerruleServiceProtocol.CompileProfile, new byte[largest.Length + 1])))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(2, test.Compile.Frames.Count);
        Assert.Empty(test.Inspection.Frames);

        using var response = await http.GetAsync(FerruleServiceProtocol.CapabilitiesPath);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var root = document.RootElement;
        var profiles = root.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(["compile", "inspection", "preview"], profiles.Select(profile => profile.GetProperty("name").GetString()));
        Assert.Equal(120, profiles[0].GetProperty("wallSeconds").GetInt32());
        Assert.Equal(120, profiles[0].GetProperty("cpuSeconds").GetInt32());
        Assert.Equal(2_147_483_648, profiles[0].GetProperty("memoryBytes").GetInt64());
        var toolchain = profiles[0].GetProperty("toolchain");
        Assert.Equal("braid.ferrule-build.v1", toolchain.GetProperty("protocol").GetString());
        Assert.Equal("braid.ferrule-csharp-json.v1", toolchain.GetProperty("profile").GetString());
        Assert.Equal("10.0.112", toolchain.GetProperty("sdkVersion").GetString());
        Assert.Equal("10.0.12", toolchain.GetProperty("runtimeVersion").GetString());
        Assert.Equal(ServiceCase.Toolchain, toolchain.GetProperty("identity").GetString());
        Assert.Equal(CompileWorkerIdentity.Combine(Identity, ServiceCase.Toolchain), toolchain.GetProperty("buildIdentity").GetString());
        Assert.False(profiles[1].TryGetProperty("toolchain", out _));
        Assert.Equal(["execute"], root.GetProperty("reserved").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task CompilationRequiresTheExactToolchainBeforeBufferingOrExecution()
    {
        await using var test = await ServiceCase.StartAsync(compile: true);
        using var http = test.Client();
        foreach (var value in new string?[] { null, "sha256:" + new string('4', 64), ServiceCase.Toolchain + "," + ServiceCase.Toolchain })
        {
            using var request = Operation(FerruleServiceProtocol.CompileProfile, [1]);
            request.Headers.Remove(FerruleServiceProtocol.CompileToolchainHeader);
            if (value is not null) request.Headers.TryAddWithoutValidation(FerruleServiceProtocol.CompileToolchainHeader, value);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Empty(test.Compile.Frames);
        }
        using var accepted = await http.SendAsync(Operation(FerruleServiceProtocol.CompileProfile, [1]));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(ServiceCase.Toolchain, Assert.Single(accepted.Headers.GetValues(FerruleServiceProtocol.CompileToolchainHeader)));
    }

    [Fact]
    public async Task ReplicaRunsItsConcurrencyQueuesItsCapacityAndRefusesTheRestRetryably()
    {
        await using var test = await ServiceCase.StartAsync(concurrency: 2, capacity: 3, admissionWait: TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        test.Inspection.Behavior = async (frame, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None);
        };
        using var http = test.Client(connections: 8);
        var admitted = Enumerable.Range(0, 3).Select(index => http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, [(byte)index]))).ToArray();
        await WaitUntil(() => Volatile.Read(ref running) == 2);
        // Capacity is full: two running and one queued. A fourth operation never reaches a runner.
        await WaitUntil(() => test.Inspection.Frames.Count == 2);
        using (var busy = await http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, [9])))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter?.Delta);
        }
        release.TrySetResult();
        foreach (var response in await Task.WhenAll(admitted))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
        Assert.Equal(2, Volatile.Read(ref peak));
        Assert.Equal(3, test.Inspection.Frames.Count);
    }

    [Fact]
    public async Task QueuedOperationNotStartedWithinTheAdmissionWaitIsRefusedUnexecuted()
    {
        await using var test = await ServiceCase.StartAsync(concurrency: 1, capacity: 2, admissionWait: TimeSpan.FromMilliseconds(300));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Inspection.Behavior = async (frame, _) =>
        {
            await release.Task;
            return new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None);
        };
        using var http = test.Client(connections: 4);
        var first = http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, [1]));
        await WaitUntil(() => test.Inspection.Frames.Count == 1);
        using (var waited = await http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, [2])))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, waited.StatusCode);
        release.TrySetResult();
        (await first).Dispose();
        Assert.Single(test.Inspection.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteBodiesReserveCapacityAndTheirAbandonedIdentityCanBeRetried(bool http2)
    {
        await using var test = await ServiceCase.StartAsync(concurrency: 1, capacity: 1);
        using var http = test.Client(connections: 3);
        using var cancel = new CancellationTokenSource();
        using var content = new WithheldContent();
        var operation = Guid.NewGuid().ToString("N");
        using var request = Operation("inspection", [], operation, http2);
        request.Content = content;
        var pending = http.SendAsync(request, cancel.Token);
        try
        {
            await content.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => test.Server.ReservedRequests == 1);
            using var refused = await http.SendAsync(Operation("inspection", [2], http2: http2));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), refused.Headers.RetryAfter?.Delta);
            Assert.Empty(test.Inspection.Frames);
        }
        finally
        {
            cancel.Cancel();
            await ObserveAbandonedAsync(pending);
        }
        await WaitUntil(() => test.Server.ReservedRequests == 0);
        using var retry = await http.SendAsync(Operation("inspection", [3], operation, http2));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(new byte[] { 3 }, Assert.Single(test.Inspection.Frames));
    }

    [Fact]
    public async Task AFullBodyReaderDoesNotBlockEarlyCancellationOrRememberItsPartialFrameAsExecuted()
    {
        await using var test = await ServiceCase.StartAsync(concurrency: 1, capacity: 1);
        using var http = test.Client();
        using var cancel = new CancellationTokenSource();
        using var content = new WithheldContent();
        var operation = Guid.NewGuid().ToString("N");
        using var request = Operation("inspection", [], operation);
        request.Content = content;
        var pending = http.SendAsync(request, cancel.Token);
        try
        {
            await content.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => test.Server.ReservedRequests == 1);
            using var canceled = await http.PostAsync(FerruleServiceProtocol.CancelPath("inspection", operation), null);
            Assert.Equal(HttpStatusCode.NoContent, canceled.StatusCode);
            Assert.Empty(test.Inspection.Frames);
        }
        finally
        {
            cancel.Cancel();
            await ObserveAbandonedAsync(pending);
        }
        await WaitUntil(() => test.Server.ReservedRequests == 0);
        using var refused = await http.SendAsync(Operation("inspection", [3], operation));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Equal("canceled", Assert.Single(refused.Headers.GetValues(InspectionTransport.ResultHeader)));
        Assert.Empty(test.Inspection.Frames);
        using var next = await http.SendAsync(Operation("inspection", [4]));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(new byte[] { 4 }, Assert.Single(test.Inspection.Frames));
    }

    [Fact]
    public async Task AWithheldBodyExpiresWithoutRunningAndReleasesCapacity()
    {
        await using var test = await ServiceCase.StartAsync(concurrency: 1, capacity: 1);
        using var http = test.Client();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var content = new WithheldContent();
        var operation = Guid.NewGuid().ToString("N");
        using var request = Operation("inspection", [], operation);
        request.Content = content;
        var pending = http.SendAsync(request, cancel.Token);
        try
        {
            await content.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await Record.ExceptionAsync(async () => { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(20)); });
            Assert.True(error is HttpRequestException or OperationCanceledException);
            Assert.False(cancel.IsCancellationRequested);
            await WaitUntil(() => test.Server.ReservedRequests == 0);
            Assert.Empty(test.Inspection.Frames);
            using var retry = await http.SendAsync(Operation("inspection", [5], operation));
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(new byte[] { 5 }, Assert.Single(test.Inspection.Frames));
        }
        finally
        {
            cancel.Cancel();
            await ObserveAbandonedAsync(pending);
        }
    }

    [Fact]
    public async Task CancellationOnTheOperationsOwnConnectionSettlesItBeforeTheAnswer()
    {
        await using var test = await ServiceCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = 0;
        test.Preview.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            await Task.Delay(200, CancellationToken.None);
            Interlocked.Exchange(ref settled, 1);
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        // One connection: HTTP/2 carries the cancellation as a second stream beside the outstanding operation.
        using var http = test.Client(connections: 1);
        var operation = Guid.NewGuid().ToString("N");
        var running = http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, [1], operation));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var cancel = await http.PostAsync(FerruleServiceProtocol.CancelPath(FerruleServiceProtocol.PreviewProfile, operation), null))
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        using var answer = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("canceled", Assert.Single(answer.Headers.GetValues(InspectionTransport.ResultHeader)));
        Assert.Equal(1, Volatile.Read(ref settled));
        Assert.Equal(HttpVersion.Version20, answer.Version);

        // Early cancellation refuses the operation when it arrives; a replay of its identity conflicts.
        var early = Guid.NewGuid().ToString("N");
        (await http.PostAsync(FerruleServiceProtocol.CancelPath(FerruleServiceProtocol.PreviewProfile, early), null)).Dispose();
        using (var refused = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, [1], early)))
            Assert.Equal("canceled", Assert.Single(refused.Headers.GetValues(InspectionTransport.ResultHeader)));
        using (var replay = await http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, [1], early)))
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Single(test.Preview.Frames);
    }

    [Fact]
    public async Task ProbesServeOnlyHealthOnThePlainListenerAndReflectReadiness()
    {
        var healthy = true;
        await using var test = await ServiceCase.StartAsync(health: true, healthy: () => Volatile.Read(ref healthy));
        using var plain = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + test.Server.HealthPort), Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(HttpStatusCode.NoContent, (await plain.GetAsync(FerruleServiceProtocol.LivenessPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await plain.GetAsync(FerruleServiceProtocol.ReadinessPath)).StatusCode);
        foreach (var path in new[] { FerruleServiceProtocol.ReadyPath, FerruleServiceProtocol.CapabilitiesPath, InspectionTransport.ReadyPath })
            Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync(path)).StatusCode);
        using (var content = new ByteArrayContent([1]))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
            Assert.Equal(HttpStatusCode.NotFound,
                (await plain.PostAsync(FerruleServiceProtocol.OperationPath("inspection", Guid.NewGuid().ToString("N")), content)).StatusCode);
        }
        Volatile.Write(ref healthy, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await plain.GetAsync(FerruleServiceProtocol.ReadinessPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await plain.GetAsync(FerruleServiceProtocol.LivenessPath)).StatusCode);
        using (var pinned = test.Client())
        {
            using var ready = await pinned.GetAsync(FerruleServiceProtocol.ReadyPath);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.NotNull(ready.Headers.RetryAfter);
            using var refused = await pinned.SendAsync(Operation("inspection", [1]));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.NotNull(refused.Headers.RetryAfter);
        }
        Volatile.Write(ref healthy, true);
        await test.Server.StopAsync(CancellationToken.None);
        Assert.Empty(test.Inspection.Frames);
    }

    [Fact]
    public async Task EveryAdmittedClientPinReachesTheServiceAndNoOtherDoes()
    {
        using var second = TransportCase.Certificate("CN=Braid.Worker service client");
        await using var test = await ServiceCase.StartAsync(extraClient: second);
        using (var first = test.Client())
            Assert.Equal(HttpStatusCode.NoContent, (await first.GetAsync(FerruleServiceProtocol.ReadyPath)).StatusCode);
        using (var other = test.Client(certificate: second))
            Assert.Equal(HttpStatusCode.NoContent, (await other.GetAsync(FerruleServiceProtocol.ReadyPath)).StatusCode);
        using var stranger = TransportCase.Certificate("CN=Unpinned");
        using var refused = test.Client(certificate: stranger);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => refused.GetAsync(FerruleServiceProtocol.ReadyPath));
    }

    [Fact]
    public async Task PlatformSandboxListenerServesOnlyServiceRoutesOverPlainHttp()
    {
        var inspection = new ScriptedRunner();
        var preview = new ScriptedRunner();
        await using var server = new WorkerTransportServer(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0)),
            new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
            {
                [FerruleServiceProtocol.InspectionProfile] = inspection,
                [FerruleServiceProtocol.PreviewProfile] = preview
            });
        await server.StartAsync(CancellationToken.None);
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + server.Port), Timeout = TimeSpan.FromSeconds(10) };
        using (var ready = await http.GetAsync(FerruleServiceProtocol.ReadyPath))
            Assert.Equal(Identity, Assert.Single(ready.Headers.GetValues(InspectionTransport.ImageHeader)));
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync(FerruleServiceProtocol.ReadinessPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(InspectionTransport.ReadyPath)).StatusCode);
        using (var answered = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, [3], http2: false)))
            Assert.Equal(new byte[] { 3 }, await answered.Content.ReadAsByteArrayAsync());
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ServerOptionsAndRunnersAreValidatedBeforeListening()
    {
        using var certificate = TransportCase.Certificate("CN=Service");
        var listen = new IPEndPoint(IPAddress.Loopback, 0);
        var pin = new string('a', 64);
        Assert.Throws<ArgumentException>(() => new WorkerTransportServerOptions(listen, certificate, []));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServerOptions(listen, certificate, [pin, pin]));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServerOptions(listen, certificate, ["pin"]));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServerOptions(listen, certificate, Enumerable.Range(0, 9).Select(i => new string((char)('a' + i), 64)).ToArray()));
        var runners = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { ["inspection"] = new ScriptedRunner() };
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen, certificate, [pin]) { Concurrency = 0 }, runners));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen, certificate, [pin]) { Concurrency = 4, Capacity = 3 }, runners));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen, certificate, [pin]) { AdmissionWait = TimeSpan.FromSeconds(21) }, runners));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen, certificate, [pin]),
            new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { ["preview"] = new ScriptedRunner() }));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen, certificate, [pin]) { ServiceRoutes = true },
            new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { ["compile"] = new ScriptedRunner() }));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen, certificate, [pin]) { ServiceRoutes = true },
            new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
            {
                ["inspection"] = new ScriptedRunner(),
                ["preview"] = new OtherIdentityRunner()
            }));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen) { HealthListen = listen }, runners));
        // A served compile profile and its toolchain identity come together.
        var compiling = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { ["compile"] = new ScriptedRunner() };
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen) { CompileToolchain = "ferrule:latest" }, compiling));
        Assert.Throws<ArgumentException>(() => new WorkerTransportServer(new WorkerTransportServerOptions(listen) { CompileToolchain = ServiceCase.Toolchain },
            new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal) { ["preview"] = new ScriptedRunner() }));
        _ = new WorkerTransportServer(new WorkerTransportServerOptions(listen) { CompileToolchain = ServiceCase.Toolchain }, compiling);
    }

    internal static HttpRequestMessage Operation(string profile, byte[] frame, string? operation = null, bool http2 = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, FerruleServiceProtocol.OperationPath(profile, operation ?? Guid.NewGuid().ToString("N")))
        {
            Content = new ByteArrayContent(frame),
            Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
        request.Headers.TryAddWithoutValidation(InspectionTransport.ImageHeader, Identity);
        if (profile == FerruleServiceProtocol.CompileProfile)
            request.Headers.TryAddWithoutValidation(FerruleServiceProtocol.CompileToolchainHeader, ServiceCase.Toolchain);
        if (profile == FerruleServiceProtocol.CompiledProfile)
            request.Headers.TryAddWithoutValidation(FerruleServiceProtocol.CompiledRuntimeHeader, ServiceCase.RuntimeIdentity);
        return request;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private static async Task ObserveAbandonedAsync(Task<HttpResponseMessage> request)
    {
        try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException) { }
    }

    private sealed class WithheldContent : HttpContent
    {
        internal TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WithheldContent() => Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
        protected override bool TryComputeLength(out long length) { length = DockerWorkerProfile.Inspection.MaximumRequestBytes; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            Sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class OtherIdentityRunner : IInspectionWorkerRunner
    {
        public string BuildIdentity => "sha256:" + new string('2', 64);
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerProcessResult([], WorkerProcessFailure.None));
    }

    internal sealed class ServiceCase : IAsyncDisposable
    {
        private readonly bool ownsCertificates;

        private ServiceCase(WorkerTransportServer server, System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate,
            System.Security.Cryptography.X509Certificates.X509Certificate2 client, ScriptedRunner inspection, ScriptedRunner preview,
            ScriptedRunner compile, ScriptedRunner runtime, bool ownsCertificates)
        {
            this.ownsCertificates = ownsCertificates;
            Server = server;
            ServerCertificate = serverCertificate;
            ClientCertificate = client;
            Inspection = inspection;
            Preview = preview;
            Compile = compile;
            Runtime = runtime;
        }

        internal const string Toolchain = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        internal const string RuntimeIdentity = "sha256:4444444444444444444444444444444444444444444444444444444444444444";

        internal WorkerTransportServer Server { get; }
        internal System.Security.Cryptography.X509Certificates.X509Certificate2 ServerCertificate { get; }
        internal System.Security.Cryptography.X509Certificates.X509Certificate2 ClientCertificate { get; }
        internal ScriptedRunner Inspection { get; }
        internal ScriptedRunner Preview { get; }
        /// <summary>The compile profile's runner; it is served only by a case started with compilation.</summary>
        internal ScriptedRunner Compile { get; }
        internal ScriptedRunner Runtime { get; }
        internal Uri Endpoint => new("https://127.0.0.1:" + Server.Port);
        internal string ServerPin => TransportCase.Pin(ServerCertificate);

        internal static async Task<ServiceCase> StartAsync(int concurrency = 2, int capacity = 4, TimeSpan? admissionWait = null,
            bool health = false, Func<bool>? healthy = null, System.Security.Cryptography.X509Certificates.X509Certificate2? extraClient = null,
            System.Security.Cryptography.X509Certificates.X509Certificate2? serverCertificate = null,
            System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate = null, bool compile = false, string? compileToolchain = null,
            bool execute = false, string? runtimeIdentity = null)
        {
            // Replicas of one service share its certificate and admit the same clients.
            var owned = serverCertificate is null;
            serverCertificate ??= TransportCase.Certificate("CN=Ferrule service");
            var client = clientCertificate ?? TransportCase.Certificate("CN=Session host service client");
            var inspection = new ScriptedRunner();
            var preview = new ScriptedRunner();
            var compiler = new ScriptedRunner();
            var runtime = new ScriptedRunner();
            var served = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
            {
                [FerruleServiceProtocol.InspectionProfile] = inspection,
                [FerruleServiceProtocol.PreviewProfile] = preview
            };
            if (compile) served[FerruleServiceProtocol.CompileProfile] = compiler;
            if (execute) served[FerruleServiceProtocol.CompiledProfile] = runtime;
            string[] pins = extraClient is null ? [TransportCase.Pin(client)] : [TransportCase.Pin(client), TransportCase.Pin(extraClient)];
            var server = new WorkerTransportServer(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0), serverCertificate, pins)
            {
                ServiceRoutes = true,
                Concurrency = concurrency,
                Capacity = capacity,
                AdmissionWait = admissionWait ?? TimeSpan.FromSeconds(5),
                HealthListen = health ? new IPEndPoint(IPAddress.Loopback, 0) : null,
                Healthy = healthy,
                EngineRevision = "9149905759b4f65baf043b975624e45805a50142",
                CompileToolchain = compile ? compileToolchain ?? Toolchain : null,
                CompiledRuntimeIdentity = execute ? runtimeIdentity ?? RuntimeIdentity : null
            }, served);
            await server.StartAsync(CancellationToken.None);
            return new(server, serverCertificate, client, inspection, preview, compiler, runtime, owned);
        }

        /// <summary>An HTTP/2 client that pins the service and presents a client certificate.</summary>
        internal HttpClient Client(int connections = 1, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = connections,
                EnableMultipleHttp2Connections = connections > 1,
                SslOptions = new()
                {
                    ClientCertificates = [certificate ?? ClientCertificate],
                    RemoteCertificateValidationCallback = (_, presented, _, _) => InspectionTransport.HasDigest(presented, ServerPin)
                }
            };
            return new HttpClient(handler)
            {
                BaseAddress = Endpoint,
                Timeout = TimeSpan.FromSeconds(30),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync(CancellationToken.None);
            await Server.DisposeAsync();
            if (!ownsCertificates) return;
            ServerCertificate.Dispose();
            ClientCertificate.Dispose();
        }
    }
}
