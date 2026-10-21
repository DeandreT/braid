using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Weft.Jobs;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>
/// The pinned inspection transport against an in-process worker endpoint and a scripted runner. Real Docker
/// execution through the same transport is qualified separately.
/// </summary>
public sealed class InspectionTransportTests
{
    private const string Image = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task ExactFramesAndRunnerAnswersCrossTheTransportUnchanged()
    {
        await using var test = await TransportCase.StartAsync();
        var frame = new byte[(1024 * 1024) + 257];
        RandomNumberGenerator.Fill(frame);
        for (var index = 0; index < 256; index++) frame[index] = (byte)index;
        var output = new byte[(2 * 1024 * 1024) + 3];
        RandomNumberGenerator.Fill(output);
        test.Runner.Behavior = (_, _) => Task.FromResult(new WorkerProcessResult(output, WorkerProcessFailure.None));
        await using var client = test.Client();
        var result = await client.RunAsync(frame, CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.None, result.Failure);
        Assert.Equal(output, result.Output);
        Assert.Equal(frame, Assert.Single(test.Runner.Frames));
        Assert.Equal(Image, client.BuildIdentity);

        foreach (var failure in Enum.GetValues<WorkerProcessFailure>().Where(value => value is not
            (WorkerProcessFailure.None or WorkerProcessFailure.CleanupFailed)))
        {
            test.Runner.Behavior = (_, _) => Task.FromResult(new WorkerProcessResult([1, 2, 3], failure));
            var failed = await client.RunAsync(frame.AsMemory(0, 9), CancellationToken.None);
            Assert.Equal(failure, failed.Failure);
            Assert.Empty(failed.Output);
        }
        Assert.Equal(frame.AsMemory(0, 9).ToArray(), test.Runner.Frames[^1]);
        await client.StopAsync();
    }

    [Fact]
    public async Task OnlyMutuallyPinnedPeersReachTheRunner()
    {
        await using var test = await TransportCase.StartAsync();
        using var stranger = TransportCase.Certificate("CN=Unpinned inspection client");
        await using (var unpinnedClient = new RemoteInspectionWorkerRunner(new(test.Endpoint, test.WorkerPin, stranger, Image)))
        {
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await unpinnedClient.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            await unpinnedClient.StopAsync();
        }
        await using (var wrongWorker = new RemoteInspectionWorkerRunner(new(test.Endpoint, TransportCase.Pin(stranger), test.ClientCertificate, Image)))
        {
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await wrongWorker.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            await wrongWorker.StopAsync();
        }
        const string otherImage = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        await using (var otherBuild = new RemoteInspectionWorkerRunner(new(test.Endpoint, test.WorkerPin, test.ClientCertificate, otherImage)))
        {
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await otherBuild.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            await otherBuild.StopAsync();
        }
        Assert.Empty(test.Runner.Frames);

        // Refusals before execution do not close admission.
        await using var client = test.Client();
        Assert.Equal(WorkerProcessFailure.None, (await client.RunAsync(new byte[] { 7 }, CancellationToken.None)).Failure);
        await client.StopAsync();
    }

    [Fact]
    public async Task UnreachableWorkerIsUnavailableAndDoesNotCloseAdmission()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var client = TransportCase.Certificate("CN=Inspection client");
        await using var runner = new RemoteInspectionWorkerRunner(new(new Uri("https://127.0.0.1:" + port), new string('a', 64), client, Image));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await runner.StopAsync();
        Assert.Throws<ArgumentException>(() => runner.RunAsync(new byte[InspectionTransport.MaximumFrameBytes + 1], CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task CallerCancellationReturnsOnlyAfterTheWorkerSettled()
    {
        await using var test = await TransportCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = 0;
        test.Runner.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            // Cleanup of the owned container happens before the runner answers.
            await Task.Delay(300, CancellationToken.None);
            Interlocked.Exchange(ref settled, 1);
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        await using var client = test.Client();
        using var cancellation = new CancellationTokenSource();
        var running = client.RunAsync(new byte[] { 1, 2 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerProcessFailure.Canceled, result.Failure);
        Assert.Equal(1, Volatile.Read(ref settled));

        // Ordinary settled cancellation keeps admission open.
        test.Runner.Behavior = (_, _) => Task.FromResult(new WorkerProcessResult([9], WorkerProcessFailure.None));
        Assert.Equal(new byte[] { 9 }, (await client.RunAsync(new byte[] { 3 }, CancellationToken.None)).Output);
        await client.StopAsync();
    }

    [Fact]
    public async Task CancellationBeforeAnyFrameByteRunsNothing()
    {
        await using var test = await TransportCase.StartAsync();
        await using var client = test.Client();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await client.RunAsync(new byte[] { 1 }, canceled.Token)).Failure);
        Assert.Empty(test.Runner.Frames);
        await client.StopAsync();
    }

    [Fact]
    public async Task UnconfirmedSettlementIsCleanupFailureThatClosesAdmissionAndFaultsStop()
    {
        await using var test = await TransportCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Runner.Behavior = async (_, _) =>
        {
            started.TrySetResult();
            // A runner that cannot confirm settlement within the client's bound.
            await release.Task;
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        var client = test.Client(settlement: TimeSpan.FromMilliseconds(400));
        using var cancellation = new CancellationTokenSource();
        var running = client.RunAsync(new byte[] { 1 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await client.RunAsync(new byte[] { 2 }, CancellationToken.None)).Failure);
        Assert.Single(test.Runner.Frames);
        await Assert.ThrowsAsync<InvalidOperationException>(client.StopAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.DisposeAsync());
        release.TrySetResult();
    }

    [Fact]
    public async Task WorkerReportedCleanupFailureClosesAdmission()
    {
        await using var test = await TransportCase.StartAsync();
        test.Runner.Behavior = (_, _) => Task.FromResult(new WorkerProcessResult([], WorkerProcessFailure.CleanupFailed));
        var client = test.Client();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await client.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await client.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Single(test.Runner.Frames);
        await Assert.ThrowsAsync<InvalidOperationException>(client.StopAsync);

        // A runner exception is unconfirmed cleanup on the worker side as well.
        test.Runner.Behavior = (_, _) => throw new InvalidOperationException("runner fault");
        var second = test.Client();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await second.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(second.StopAsync);
    }

    [Fact]
    public async Task LostAnswerAfterDeliveryIsUncertainAndTheWorkerCancelsTheOperation()
    {
        await using var test = await TransportCase.StartAsync();
        await using var relay = await TcpRelay.StartAsync(test.Port);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Runner.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { observed.TrySetResult(); }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        // The cancellation request uses the same severed route and cannot reach the worker either.
        var client = new RemoteInspectionWorkerRunner(new(new Uri("https://127.0.0.1:" + relay.Port), test.WorkerPin, test.ClientCertificate, Image)
        {
            SettlementBound = TimeSpan.FromMilliseconds(500),
            CancelRequestBound = TimeSpan.FromMilliseconds(500)
        });
        var running = client.RunAsync(new byte[] { 4, 5, 6 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        relay.Sever();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await client.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(client.StopAsync);
    }

    [Fact]
    public async Task ExpiredResponseBoundCancelsRemotelyAndReportsTimeout()
    {
        await using var test = await TransportCase.StartAsync();
        test.Runner.Behavior = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        await using var client = new RemoteInspectionWorkerRunner(new(test.Endpoint, test.WorkerPin, test.ClientCertificate, Image)
        {
            ResponseBound = TimeSpan.FromMilliseconds(500)
        });
        Assert.Equal(WorkerProcessFailure.TimedOut, (await client.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await client.StopAsync();
    }

    [Fact]
    public async Task StoppingTheClientCancelsAndJoinsActiveOperations()
    {
        await using var test = await TransportCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Runner.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        var client = test.Client();
        var running = client.RunAsync(new byte[] { 1 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = client.StopAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await client.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task StoppingTheWorkerCancelsAdmittedOperationsAndRefusesNewOnes()
    {
        await using var test = await TransportCase.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Runner.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        await using var client = test.Client();
        var running = client.RunAsync(new byte[] { 1 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.Server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await client.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        await client.StopAsync();
    }

    [Fact]
    public async Task WorkerAdmitsEachOperationOnceAndHonorsEarlyCancellation()
    {
        await using var test = await TransportCase.StartAsync();
        using var http = test.RawClient();
        var operation = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync(InspectionTransport.OperationsPath + operation + "/cancel", null)).StatusCode);
        using (var refused = await http.SendAsync(TransportCase.Run(operation, [1, 2])))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Equal("canceled", Assert.Single(refused.Headers.GetValues(InspectionTransport.ResultHeader)));
            Assert.Equal(Image, Assert.Single(refused.Headers.GetValues(InspectionTransport.ImageHeader)));
            Assert.Empty(await refused.Content.ReadAsByteArrayAsync());
        }
        Assert.Empty(test.Runner.Frames);
        using (var duplicate = await http.SendAsync(TransportCase.Run(operation, [1, 2])))
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var fresh = Guid.NewGuid().ToString("N");
        using (var ran = await http.SendAsync(TransportCase.Run(fresh, [3])))
            Assert.Equal("none", Assert.Single(ran.Headers.GetValues(InspectionTransport.ResultHeader)));
        using (var replay = await http.SendAsync(TransportCase.Run(fresh, [3])))
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Single(test.Runner.Frames);

        foreach (var malformed in new[]
        {
            TransportCase.Run("NOT-AN-OPERATION", [1]),
            TransportCase.Run(Guid.NewGuid().ToString("N"), [1], mediaType: "application/json"),
            TransportCase.Run(Guid.NewGuid().ToString("N"), [1], image: "sha256:" + new string('3', 64)),
            TransportCase.Run(Guid.NewGuid().ToString("N"), [])
        })
        {
            using var response = await http.SendAsync(malformed);
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound);
            malformed.Dispose();
        }
        Assert.Single(test.Runner.Frames);
    }

    [Fact]
    public async Task WorkerRefusesOperationsBeyondItsQueueBounds()
    {
        await using var test = await TransportCase.StartAsync(admissionWait: TimeSpan.FromMilliseconds(300));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Runner.Behavior = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new WorkerProcessResult([1], WorkerProcessFailure.None);
        };
        await using var client = test.Client();
        var first = client.RunAsync(new byte[] { 1 }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Not admitted to the single active runner within the admission wait: refused, never executed.
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await client.RunAsync(new byte[] { 2 }, CancellationToken.None)).Failure);
        release.TrySetResult();
        Assert.Equal(WorkerProcessFailure.None, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Single(test.Runner.Frames);
        await client.StopAsync();
    }

    [Fact]
    public void ConfigurationAdmitsOnlyCanonicalOriginsPinsAndImages()
    {
        using var certificate = TransportCase.Certificate("CN=Inspection client");
        var pin = new string('a', 64);
        foreach (var endpoint in new[] { "http://worker:8443", "https://worker:8443/", "https://worker:8443/v1", "https://user@worker:8443",
            "https://worker:8443?x=1", "https://worker:8443#f", "https://WORKER:8443" })
            Assert.Throws<ArgumentException>(() => new RemoteInspectionWorkerOptions(new Uri(endpoint), pin, certificate, Image));
        Assert.Throws<ArgumentException>(() => new RemoteInspectionWorkerOptions(new Uri("https://worker:8443"), new string('A', 64), certificate, Image));
        Assert.Throws<ArgumentException>(() => new RemoteInspectionWorkerOptions(new Uri("https://worker:8443"), new string('a', 63), certificate, Image));
        Assert.Throws<ArgumentException>(() => new RemoteInspectionWorkerOptions(new Uri("https://worker:8443"), pin, certificate, Image.ToUpperInvariant()));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        Assert.Throws<ArgumentException>(() => new RemoteInspectionWorkerOptions(new Uri("https://worker:8443"), pin, publicOnly, Image));
        Assert.Equal(new Uri("https://10.0.0.4:8443"), new RemoteInspectionWorkerOptions(new Uri("https://10.0.0.4:8443"), pin, certificate, Image).Endpoint);
        Assert.Equal(TransportCase.Pin(certificate), InspectionTransport.CertificateDigest(certificate));
        Assert.True(InspectionTransport.HasDigest(certificate, TransportCase.Pin(certificate)));
        Assert.False(InspectionTransport.HasDigest(certificate, pin));
        Assert.False(InspectionTransport.HasDigest(null, pin));
        foreach (var failure in Enum.GetValues<WorkerProcessFailure>())
        {
            Assert.True(InspectionTransport.TryDecode(InspectionTransport.Encode(failure), out var decoded));
            Assert.Equal(failure, decoded);
        }
        Assert.False(InspectionTransport.TryDecode("None", out _));
        Assert.Throws<ArgumentException>(() => new InspectionTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 1), publicOnly, pin));
        Assert.Throws<ArgumentException>(() => new InspectionTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 1), certificate, "pin"));
    }

    internal sealed class ScriptedRunner : IInspectionWorkerRunner
    {
        private readonly List<byte[]> frames = [];
        internal Func<ReadOnlyMemory<byte>, CancellationToken, Task<WorkerProcessResult>> Behavior { get; set; } =
            (frame, _) => Task.FromResult(new WorkerProcessResult(frame.ToArray(), WorkerProcessFailure.None));
        internal IReadOnlyList<byte[]> Frames
        {
            get { lock (frames) return frames.ToArray(); }
        }
        public string BuildIdentity => Image;

        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            lock (frames) frames.Add(requestFrame.ToArray());
            return Behavior(requestFrame, cancellationToken);
        }
    }

    internal sealed class TransportCase : IAsyncDisposable
    {
        private TransportCase(InspectionTransportServer server, X509Certificate2 worker, X509Certificate2 client, ScriptedRunner runner)
        {
            Server = server;
            WorkerCertificate = worker;
            ClientCertificate = client;
            Runner = runner;
        }

        internal InspectionTransportServer Server { get; }
        internal X509Certificate2 WorkerCertificate { get; }
        internal X509Certificate2 ClientCertificate { get; }
        internal ScriptedRunner Runner { get; }
        internal int Port => Server.Port;
        internal Uri Endpoint => new("https://127.0.0.1:" + Port);
        internal string WorkerPin => Pin(WorkerCertificate);

        internal static async Task<TransportCase> StartAsync(TimeSpan? admissionWait = null)
        {
            var worker = Certificate("CN=Inspection worker");
            var client = Certificate("CN=Inspection client");
            var runner = new ScriptedRunner();
            var server = new InspectionTransportServer(new InspectionTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0), worker, Pin(client))
            {
                AdmissionWait = admissionWait ?? InspectionTransport.AdmissionWait
            }, runner);
            await server.StartAsync(CancellationToken.None);
            return new(server, worker, client, runner);
        }

        internal RemoteInspectionWorkerRunner Client(TimeSpan? settlement = null) =>
            new(new(Endpoint, WorkerPin, ClientCertificate, Image)
            {
                SettlementBound = settlement ?? TimeSpan.FromSeconds(15),
                CancelRequestBound = TimeSpan.FromSeconds(5)
            });

        internal HttpClient RawClient()
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new()
                {
                    ClientCertificates = [ClientCertificate],
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => InspectionTransport.HasDigest(certificate, WorkerPin)
                }
            };
            return new HttpClient(handler) { BaseAddress = Endpoint, Timeout = TimeSpan.FromSeconds(30) };
        }

        internal static HttpRequestMessage Run(string operation, byte[] frame, string mediaType = InspectionTransport.MediaType, string image = Image)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, InspectionTransport.OperationsPath + operation) { Content = new ByteArrayContent(frame) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            request.Headers.TryAddWithoutValidation(InspectionTransport.ImageHeader, image);
            return request;
        }

        internal static X509Certificate2 Certificate(string subject)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
        }

        internal static string Pin(X509Certificate2 certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));

        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync(CancellationToken.None);
            await Server.DisposeAsync();
            WorkerCertificate.Dispose();
            ClientCertificate.Dispose();
        }
    }

    /// <summary>A loopback TCP relay whose established connections can be severed in both directions.</summary>
    private sealed class TcpRelay : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> sockets = [];
        private readonly int target;
        private Task? accepting;
        private bool severed;

        private TcpRelay(int target) => this.target = target;
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        internal static Task<TcpRelay> StartAsync(int target)
        {
            var relay = new TcpRelay(target);
            relay.listener.Start();
            relay.accepting = relay.AcceptAsync();
            return Task.FromResult(relay);
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                Socket inbound;
                try { inbound = await listener.AcceptSocketAsync(); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
                var outbound = new Socket(SocketType.Stream, ProtocolType.Tcp);
                lock (sockets)
                {
                    sockets.Add(inbound);
                    sockets.Add(outbound);
                    if (severed) { Close(inbound); Close(outbound); continue; }
                }
                try { await outbound.ConnectAsync(IPAddress.Loopback, target); }
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
