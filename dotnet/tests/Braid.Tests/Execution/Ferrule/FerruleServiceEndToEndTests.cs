using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.WorkerTransport;
using static Braid.Tests.FerruleServiceTestSupport;
using static Braid.Tests.InspectionTransportTests;

namespace Braid.Tests;

/// <summary>The Ferrule service with real child processes behind its pinned listener, and the packaged service host.</summary>
[Collection(FerruleServiceProcessCollection.Name)]
public sealed class FerruleServiceEndToEndTests
{
    [ServiceWorkerFact]
    public async Task ExplicitDocumentFramesHaveDirectAndServiceEquivalenceForAuthoredEnvelopes()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = await ServiceHost.StartAsync(root);
            await using var direct = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Preview));
            using var http = service.Client();
            foreach (var family in new[] { "x12", "edifact" })
            {
                var request = DocumentPreviewTestData.Request(Scope, family);
                var result = await new FerrulePreviewBackend(direct).PreviewAsync(request);
                Assert.True(result.Status == Braid.Application.MappingPreviewStatus.Succeeded, result.Failure?.Message);
                Assert.Equal(Braid.Application.DocumentPreviewPolicy.Profile, result.Profile);
                var capture = new CapturingRunner();
                await new FerrulePreviewBackend(capture).PreviewAsync(request);
                var expected = await direct.RunAsync(capture.Frame!, CancellationToken.None);
                Assert.Equal(WorkerProcessFailure.None, expected.Failure);
                using var answer = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, capture.Frame!, direct.BuildIdentity));
                Assert.Equal("none", Assert.Single(answer.Headers.GetValues(InspectionTransport.ResultHeader)));
                Assert.Equal(expected.Output, await answer.Content.ReadAsByteArrayAsync());
                foreach (var dependency in new JsonNode[] { JsonValue.Create("../private/parser.sef")!, new JsonObject { ["kind"] = "missing_configuration" } })
                {
                    var native = JsonNode.Parse(request.Artifact.CopyBytes())!;
                    native["source_options"]!["edi_config_reference"] = dependency;
                    var refused = await new FerrulePreviewBackend(direct).PreviewAsync(request with
                    {
                        Artifact = Braid.Domain.NativeArtifact.Admit(Scope, System.Text.Encoding.UTF8.GetBytes(native.ToJsonString()), Braid.Application.PreviewPolicy.MaximumNativeBytes),
                        Primary = new(null, "not a document"u8)
                    });
                    Assert.Equal(Braid.Application.MappingPreviewStatus.UnsupportedCapability, refused.Status);
                    Assert.True(refused.Provenance.Confirmed);
                    Assert.Empty(refused.Artifacts);
                }
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(WorkerChildren());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceWorkerFact]
    public async Task DroppedChannelWithARealChildRecoversOnAFreshReplicaWithoutRestartingTheRunner()
    {
        var root = TemporaryRoot();
        try
        {
            await using var first = await ServiceHost.StartAsync(root, holdInspection: true);
            await using var replacement = await ServiceHost.StartAsync(root);
            await using var balancer = FerruleServiceRunnerTests.RoundRobinBalancer.Start(first.Server.Port);
            var connector = new SwitchingConnector(new PinnedServiceConnector(new(
                new Uri("https://127.0.0.1:" + balancer.Port), first.ServerPin, first.ClientCertificate)));
            await using var runner = new FerruleServiceRunner(new(connector, "inspection", first.Identity));
            var frame = await FerruleServiceProcessRunnerTests.InspectionFrameAsync();
            var pending = runner.RunAsync(frame, default);
            await WaitForWorkerChildren(1, sandboxReady: true);
            balancer.Sever();
            Assert.Equal(WorkerProcessFailure.CleanupFailed, (await pending.WaitAsync(TimeSpan.FromSeconds(15))).Failure);
            Assert.False(runner.Available);
            connector.Current = new PinnedServiceConnector(new(replacement.Endpoint, replacement.ServerPin, replacement.ClientCertificate));
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(frame, default)).Failure);
            var quarantined = Stopwatch.StartNew();
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65)))
                while (!runner.Available) await Task.Delay(25, deadline.Token);
            Assert.True(quarantined.Elapsed >= TimeSpan.FromSeconds(46));
            Assert.Empty(WorkerChildren());
            var recovered = await new FerruleInspectionBackend(runner).ValidateAsync(Artifact("valid-basic"));
            Assert.True(recovered.Provenance.Confirmed);
            Assert.Equal(MappingOperationStatus.Succeeded, recovered.Status);
            Assert.Empty(WorkerChildren());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            await runner.StopAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class SwitchingConnector(IFerruleServiceConnector current) : IFerruleServiceConnector
    {
        internal IFerruleServiceConnector Current = current;
        public IFerruleServiceChannel Open() => Current.Open();
    }

    [ServiceCompileFact]
    public async Task KillingAPackagedReplicaDuringCompilationRecoversTheSameHostRunnerOnAnotherReplica()
    {
        var root = TemporaryRoot();
        using var certificate = TransportCase.Certificate("CN=Recovery replicas");
        using var client = TransportCase.Certificate("CN=Recovery caller");
        try
        {
            await using var first = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(certificate),
                [TransportCase.Pin(client)], root, extra: PackagedFerruleService.Compilation);
            await using var replacement = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(certificate),
                [TransportCase.Pin(client)], root, extra: PackagedFerruleService.Compilation);
            using var http = ServiceHost.PinnedClient(new Uri("https://127.0.0.1:" + replacement.Port), TransportCase.Pin(certificate), client);
            using var capabilities = JsonDocument.Parse(await http.GetByteArrayAsync(FerruleServiceProtocol.CapabilitiesPath));
            var toolchain = capabilities.RootElement.GetProperty("profiles").EnumerateArray()
                .Single(profile => profile.GetProperty("name").GetString() == "compile").GetProperty("toolchain").GetProperty("identity").GetString()!;
            var connector = new SwitchingConnector(new PinnedServiceConnector(new(new Uri("https://127.0.0.1:" + first.Port), TransportCase.Pin(certificate), client)));
            var clock = new MaintenanceServiceTestTimeProvider();
            var options = new FerruleServiceRunnerOptions(connector, "compile", PackagedFerruleService.Identity, toolchain) { Clock = clock };
            var runner = new FerruleServiceRunner(options);
            var primary = await Record.ExceptionAsync(async () =>
            {
                var frame = BuildFrame("valid-basic", "compile");
                var pending = runner.RunAsync(frame, default);
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    // A replica is a process tree/cgroup, not just its listening process. Remove the whole owned tree.
                    while (!Directory.EnumerateDirectories($"/proc/{first.Process.Id}/task").Any(task =>
                    {
                        try { return File.ReadAllText(Path.Combine(task, "children")).Trim().Length > 0; }
                        catch (IOException) { return false; }
                    }))
                    {
                        Assert.False(pending.IsCompleted, "The compile must still be running when the replica is killed.");
                        await Task.Delay(1, deadline.Token);
                    }
                }
                Assert.False(pending.IsCompleted);
                first.Process.Kill(entireProcessTree: true);
                await first.Process.WaitForExitAsync();
                Assert.Equal(WorkerProcessFailure.CleanupFailed, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).Failure);
                Assert.False(runner.Available);
                connector.Current = new PinnedServiceConnector(new(new Uri("https://127.0.0.1:" + replacement.Port), TransportCase.Pin(certificate), client));
                // Advance only after recovery has registered its wait. Otherwise a timer
                // created after the advance waits for a second, unadvanced quarantine.
                await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(10));
                clock.Advance(options.QuarantineBound);
                await FerruleServiceRecoveryTests.Reopened(runner);
                var recovered = await runner.RunAsync(frame, default);
                Assert.Equal(WorkerProcessFailure.None, recovered.Failure);
                Assert.NotEmpty(recovered.Output);
                await runner.StopAsync();
            });
            var cleanup = await Record.ExceptionAsync(async () => await runner.DisposeAsync());
            if (primary is not null && cleanup is not null) throw new AggregateException(primary, cleanup);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ServiceWorkerFact]
    public async Task ServiceAnswersEqualDirectChildOutputsOnEveryRoute()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = await ServiceHost.StartAsync(root);
            await using var direct = new ProcessWorkerRunner(Options(root));
            await using var directPreview = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Preview));

            // The existing pinned inspection client (the Session host's) reaches the service unchanged.
            await using var remote = new RemoteInspectionWorkerRunner(new(service.Endpoint, service.ServerPin, service.ClientCertificate, direct.BuildIdentity));
            var comparing = new ComparingRunner(direct, remote);
            var backend = new FerruleInspectionBackend(comparing);
            foreach (var fixture in Fixtures())
            {
                await backend.InspectAsync(Artifact(fixture));
                await backend.ValidateAsync(Artifact(fixture));
                await backend.ProjectAsync(Artifact(fixture));
            }
            Assert.True(comparing.Compared >= 3 * Fixtures().Count());
            await remote.StopAsync();

            // Preview frames travel on the preview profile route over HTTP/2.
            var capture = new CapturingRunner();
            await new FerrulePreviewBackend(capture).PreviewAsync(Preview());
            var expected = await directPreview.RunAsync(capture.Frame!, CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, expected.Failure);
            using var http = service.Client();
            using var answer = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, capture.Frame!, direct.BuildIdentity));
            Assert.Equal("none", Assert.Single(answer.Headers.GetValues(InspectionTransport.ResultHeader)));
            Assert.Equal(expected.Output, await answer.Content.ReadAsByteArrayAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(WorkerChildren());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceWorkerFact]
    public async Task FullReplicaRefusesRetryablyWhileALiveChildHoldsItsOnlySlot()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = await ServiceHost.StartAsync(root, concurrency: 1, capacity: 1, holdInspection: true);
            var frame = await FerruleServiceProcessRunnerTests.InspectionFrameAsync();
            using var http = service.Client(connections: 4);
            var held = http.SendAsync(Operation(FerruleServiceProtocol.InspectionProfile, frame, service.Identity));
            await WaitForWorkerChildren(1);
            var capture = new CapturingRunner();
            await new FerrulePreviewBackend(capture).PreviewAsync(Preview());
            using (var busy = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, capture.Frame!, service.Identity)))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.NotNull(busy.Headers.RetryAfter);
            }
            using (var expired = await held.WaitAsync(TimeSpan.FromSeconds(15)))
                Assert.Equal("timedOut", Assert.Single(expired.Headers.GetValues(InspectionTransport.ResultHeader)));
            using (var admitted = await http.SendAsync(Operation(FerruleServiceProtocol.PreviewProfile, capture.Frame!, service.Identity)))
                Assert.Equal("none", Assert.Single(admitted.Headers.GetValues(InspectionTransport.ResultHeader)));
            Assert.Empty(WorkerChildren());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceWorkerFact]
    public async Task PackagedServiceHostServesPinnedOperationsAndStopsCleanly()
    {
        var root = TemporaryRoot();
        using var serverCertificate = TransportCase.Certificate("CN=Ferrule service");
        using var client = TransportCase.Certificate("CN=Ferrule service client");
        try
        {
            await using var service = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(serverCertificate), [TransportCase.Pin(client)], root);
            var identity = PackagedFerruleService.Identity;
            var endpoint = new Uri("https://127.0.0.1:" + service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var remote = new RemoteInspectionWorkerRunner(new(endpoint, TransportCase.Pin(serverCertificate), client, identity));
            var validation = await new FerruleInspectionBackend(remote).ValidateAsync(Artifact("invalid-function"));
            Assert.Equal(MappingOperationStatus.InvalidMapping, validation.Status);
            Assert.True(validation.Provenance.Confirmed);
            await remote.StopAsync();
            using (var http = ServiceHost.PinnedClient(endpoint, TransportCase.Pin(serverCertificate), client))
            using (var capabilities = await http.GetAsync(FerruleServiceProtocol.CapabilitiesPath))
            using (var document = JsonDocument.Parse(await capabilities.Content.ReadAsByteArrayAsync()))
            {
                Assert.Equal(identity, document.RootElement.GetProperty("buildIdentity").GetString());
                Assert.Equal(2, document.RootElement.GetProperty("concurrency").GetInt32());
                Assert.Equal(4, document.RootElement.GetProperty("capacity").GetInt32());
            }

            Assert.Equal(0, await service.StopAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task PackagedServiceCompilesThroughThePinnedRunnerWithTheDirectChildsBytes()
    {
        var root = TemporaryRoot();
        using var serverCertificate = TransportCase.Certificate("CN=Ferrule service");
        using var client = TransportCase.Certificate("CN=Ferrule service client");
        try
        {
            await using var direct = new ProcessCompileRunner(CompileOptions(root));
            var (directCompiler, intent, native) = FerruleServiceCompileTests.Compiler(direct, "valid-basic");
            var expected = (await directCompiler.BuildAsync(intent, new(native, native.Length))).Copy();
            await using var service = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(serverCertificate), [TransportCase.Pin(client)], root,
                extra: PackagedFerruleService.Compilation);
            var endpoint = new Uri("https://127.0.0.1:" + service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var runner = new FerruleServiceRunner(new(new PinnedServiceConnector(new(endpoint, TransportCase.Pin(serverCertificate), client)),
                FerruleServiceProtocol.CompileProfile, direct.BuildIdentity, direct.ToolchainIdentity));
            var (compiler, remoteIntent, _) = FerruleServiceCompileTests.Compiler(runner, "valid-basic");
            Assert.Equal(intent.Backend, remoteIntent.Backend);
            Assert.Equal(expected, (await compiler.BuildAsync(remoteIntent, new(native, native.Length))).Copy());
            await runner.StopAsync();
            using (var http = ServiceHost.PinnedClient(endpoint, TransportCase.Pin(serverCertificate), client))
            using (var capabilities = await http.GetAsync(FerruleServiceProtocol.CapabilitiesPath))
            using (var document = JsonDocument.Parse(await capabilities.Content.ReadAsByteArrayAsync()))
            {
                var compile = document.RootElement.GetProperty("profiles").EnumerateArray().Single(profile => profile.GetProperty("name").GetString() == "compile");
                Assert.Equal(direct.ToolchainIdentity, compile.GetProperty("toolchain").GetProperty("identity").GetString());
                Assert.Equal(["execute"], document.RootElement.GetProperty("reserved").EnumerateArray().Select(value => value.GetString()));
            }

            Assert.Equal(0, await service.StopAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedServiceRefusesToStartWhenTheCompileSandboxIsNotProved()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(PackagedFerruleService.Path)) return;
        var root = TemporaryRoot();
        using var installation = new FerruleServiceCompileTests.FakeInstallation();
        using var serverCertificate = TransportCase.Certificate("CN=Ferrule service");
        try
        {
            // A launcher that announces itself without any control can never be admitted.
            var launcher = installation.Script("launcher", "#!/bin/sh\nprintf BFC2\ncat >/dev/null\n");
            using var process = PackagedFerruleService.Launch(new Dictionary<string, string>
            {
                ["BRAID_FERRULE_SERVICE_LISTEN_ADDR"] = "127.0.0.1:" + PackagedFerruleService.FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256"] = new string('a', 64),
                ["BRAID_FERRULE_SERVICE_PEM"] = PackagedFerruleService.Bundle(serverCertificate),
                ["BRAID_FERRULE_SERVICE_WORKER_EXECUTABLE"] = launcher,
                ["BRAID_FERRULE_SERVICE_TEMPORARY_ROOT"] = root,
                ["BRAID_FERRULE_SERVICE_COMPILE_ENABLED"] = "true",
                ["BRAID_FERRULE_SERVICE_COMPILE_LAUNCHER"] = launcher,
                ["BRAID_FERRULE_SERVICE_COMPILE_TOOLCHAIN"] = installation.Toolchain
            });
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(1, process.ExitCode);
            Assert.Equal("BraidFerruleServiceConfigurationFailed", (await error).Trim());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedServiceHostRefusesIncompleteConfigurationWithoutEchoingIt()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(PackagedFerruleService.Path)) return;
        using var process = PackagedFerruleService.Launch(new Dictionary<string, string>
        {
            ["BRAID_FERRULE_SERVICE_LISTEN_ADDR"] = "127.0.0.1:1",
            ["BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_SHA256"] = new string('a', 64),
            ["BRAID_FERRULE_SERVICE_PEM"] = "not-a-pem-secret-value"
        });
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, process.ExitCode);
        Assert.Equal("BraidFerruleServiceConfigurationFailed", (await error).Trim());
        Assert.DoesNotContain("not-a-pem-secret-value", await process.StandardOutput.ReadToEndAsync(), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Operation(string profile, byte[] frame, string identity)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, FerruleServiceProtocol.OperationPath(profile, Guid.NewGuid().ToString("N")))
        {
            Content = new ByteArrayContent(frame),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(InspectionTransport.MediaType);
        request.Headers.TryAddWithoutValidation(InspectionTransport.ImageHeader, identity);
        return request;
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

    /// <summary>The service composition (child runners behind one pinned listener) on an ephemeral port.</summary>
    internal sealed class ServiceHost : IAsyncDisposable
    {
        private readonly ProcessWorkerRunner inspection;
        private readonly ProcessWorkerRunner preview;

        private ServiceHost(WorkerTransportServer server, ProcessWorkerRunner inspection, ProcessWorkerRunner preview,
            X509Certificate2 serverCertificate, X509Certificate2 client)
        {
            Server = server;
            this.inspection = inspection;
            this.preview = preview;
            ServerCertificate = serverCertificate;
            ClientCertificate = client;
        }

        internal WorkerTransportServer Server { get; }
        internal X509Certificate2 ServerCertificate { get; }
        internal X509Certificate2 ClientCertificate { get; }
        internal string Identity => inspection.BuildIdentity;
        internal Uri Endpoint => new("https://127.0.0.1:" + Server.Port);
        internal string ServerPin => TransportCase.Pin(ServerCertificate);

        internal static async Task<ServiceHost> StartAsync(string root, int concurrency = 2, int capacity = 4, bool holdInspection = false)
        {
            var inspection = new ProcessWorkerRunner(Options(root, deadline: holdInspection ? TimeSpan.FromSeconds(3) : null, withhold: holdInspection));
            var preview = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Preview));
            var serverCertificate = TransportCase.Certificate("CN=Ferrule service");
            var client = TransportCase.Certificate("CN=Ferrule service client");
            var server = new WorkerTransportServer(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0), serverCertificate, [TransportCase.Pin(client)])
            {
                ServiceRoutes = true,
                Concurrency = concurrency,
                Capacity = capacity,
                AdmissionWait = TimeSpan.Zero,
                Healthy = () => inspection.Available && preview.Available,
                EngineRevision = FerruleInspectionBackend.EngineRevision
            }, new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
            {
                [FerruleServiceProtocol.InspectionProfile] = inspection,
                [FerruleServiceProtocol.PreviewProfile] = preview
            });
            await server.StartAsync(CancellationToken.None);
            return new(server, inspection, preview, serverCertificate, client);
        }

        internal HttpClient Client(int connections = 1) => PinnedClient(Endpoint, ServerPin, ClientCertificate, connections);

        internal static HttpClient PinnedClient(Uri endpoint, string serverPin, X509Certificate2 client, int connections = 1) =>
            new(new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = connections,
                EnableMultipleHttp2Connections = connections > 1,
                SslOptions = new()
                {
                    ClientCertificates = [client],
                    RemoteCertificateValidationCallback = (_, presented, _, _) => InspectionTransport.HasDigest(presented, serverPin)
                }
            })
            {
                BaseAddress = endpoint,
                Timeout = TimeSpan.FromSeconds(60),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync(CancellationToken.None);
            await Server.DisposeAsync();
            await inspection.DisposeAsync();
            await preview.DisposeAsync();
            ServerCertificate.Dispose();
            ClientCertificate.Dispose();
        }
    }
}
