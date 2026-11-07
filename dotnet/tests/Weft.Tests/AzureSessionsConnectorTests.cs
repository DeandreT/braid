using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.MappingBackends;
using Weft.MappingBackends.AzureSessions;
using Weft.MappingBackends.Workers;
using Weft.WorkerTransport;
using static Weft.Tests.InspectionTransportTests;

namespace Weft.Tests;

/// <summary>
/// The Azure Container Apps dynamic-sessions adapter against a local stub of the pool management API. The stub
/// authenticates bearer tokens, allocates one session per new identifier, forwards a session's requests to its own
/// Ferrule service instance in platform-sandbox mode, stops sessions, and injects platform failures. No Azure.
/// </summary>
public sealed class AzureSessionsConnectorTests
{
    private const string Identity = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task EachOperationRunsInAFreshSessionThatIsStoppedAndNeverReused()
    {
        await using var pool = await SessionsPoolStub.StartAsync();
        var credential = new FakeCredential();
        using var connector = Connector(pool, credential);
        await using var runner = Runner(connector, FerruleServiceProtocol.PreviewProfile);
        for (byte value = 1; value <= 3; value++)
        {
            var result = await runner.RunAsync(new byte[] { value, 9 }, CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, result.Failure);
            Assert.Equal(new byte[] { value, 9 }, result.Output);
        }

        var sessions = pool.Sessions;
        Assert.Equal(3, sessions.Count);
        Assert.Equal(3, sessions.Select(session => session.Identifier).Distinct(StringComparer.Ordinal).Count());
        Assert.All(sessions, session =>
        {
            Assert.Matches("^weft-[0-9a-f]{32}$", session.Identifier);
            Assert.Equal(["GET /v1/ready", "POST /v1/operations/preview/"], session.Requests.Select(Shape));
            Assert.Equal([1], session.Preview.Frames.Select(frame => frame.Length / 2));
            Assert.True(session.Stopped);
        });
        Assert.Empty(pool.Violations);
        Assert.Equal(1, credential.Calls);
        await runner.StopAsync();
    }

    [Fact]
    public async Task CancellationReachesTheOperationsOwnSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = 0;
        await using var pool = await SessionsPoolStub.StartAsync(session => session.Inspection.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            await Task.Delay(200, CancellationToken.None);
            Interlocked.Exchange(ref settled, 1);
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        });
        using var connector = Connector(pool, new FakeCredential());
        await using var runner = Runner(connector, FerruleServiceProtocol.InspectionProfile);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(new byte[] { 1 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        Assert.Equal(1, Volatile.Read(ref settled));
        var session = Assert.Single(pool.Sessions);
        Assert.Equal(["GET /v1/ready", "POST /v1/operations/inspection/", "POST /v1/operations/inspection/"], session.Requests.Select(Shape));
        Assert.EndsWith("/cancel", session.Requests[2].Path, StringComparison.Ordinal);
        Assert.True(session.Stopped);
        await runner.StopAsync();
    }

    [Fact]
    public async Task ExhaustedPoolIsRetriedInAFreshSessionAndNothingRanBefore()
    {
        await using var pool = await SessionsPoolStub.StartAsync();
        pool.RefuseAllocations = 1;
        using var connector = Connector(pool, new FakeCredential());
        await using var runner = Runner(connector, FerruleServiceProtocol.InspectionProfile);
        Assert.Equal(new byte[] { 5 }, (await runner.RunAsync(new byte[] { 5 }, CancellationToken.None)).Output);
        Assert.Equal(2, pool.Identifiers.Count);
        var session = Assert.Single(pool.Sessions);
        Assert.Equal(pool.Identifiers[1], session.Identifier);
        Assert.Single(session.Inspection.Frames);
        await runner.StopAsync();
    }

    [Fact]
    public async Task GatewayFailureAfterDeliveryIsUncertainAndClosesAdmission()
    {
        await using var pool = await SessionsPoolStub.StartAsync();
        pool.FailAfterForwarding = "/v1/operations/";
        using var connector = Connector(pool, new FakeCredential());
        var runner = Runner(connector, FerruleServiceProtocol.InspectionProfile);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        // The frame was delivered and ran in the session, but no settled answer from the service confirmed it.
        Assert.Single(Assert.Single(pool.Sessions).Inspection.Frames);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 2 }, CancellationToken.None)).Failure);
        Assert.Single(pool.Sessions);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
    }

    [Fact]
    public async Task UnauthorizedCallersAndFailedTokensRunNothingAndKeepAdmissionOpen()
    {
        await using var pool = await SessionsPoolStub.StartAsync();
        using (var wrongToken = Connector(pool, new FakeCredential("not-the-pool-token")))
        await using (var refused = Runner(wrongToken, FerruleServiceProtocol.InspectionProfile))
        {
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await refused.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
            await refused.StopAsync();
        }

        var failing = new FakeCredential { Fail = true };
        using var connector = Connector(pool, failing);
        await using var runner = Runner(connector, FerruleServiceProtocol.InspectionProfile);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.Empty(pool.Sessions);
        failing.Fail = false;
        Assert.Equal(new byte[] { 2 }, (await runner.RunAsync(new byte[] { 2 }, CancellationToken.None)).Output);
        Assert.Single(pool.Sessions);
        await runner.StopAsync();
    }

    [Fact]
    public async Task GatewayUncertaintyRecoversThroughANewProbeSessionAndANewOperationSession()
    {
        await using var pool = await SessionsPoolStub.StartAsync();
        pool.FailAfterForwarding = "/v1/operations/";
        using var connector = Connector(pool, new FakeCredential());
        var clock = new MaintenanceServiceTestTimeProvider();
        var options = new FerruleServiceRunnerOptions(connector, "inspection", Identity) { Clock = clock };
        await using var runner = new FerruleServiceRunner(options);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        Assert.False(runner.Available);
        Assert.Single(pool.Sessions);
        pool.FailAfterForwarding = null;
        clock.Advance(options.QuarantineBound);
        await FerruleServiceRecoveryTests.Reopened(runner);
        Assert.Equal(2, pool.Sessions.Count);
        Assert.Empty(pool.Sessions[1].Inspection.Frames);
        Assert.Equal(new byte[] { 2 }, (await runner.RunAsync(new byte[] { 2 }, default)).Output);
        Assert.Equal(3, pool.Sessions.Count);
        Assert.Equal(3, pool.Sessions.Select(session => session.Identifier).Distinct(StringComparer.Ordinal).Count());
        Assert.All(pool.Sessions, session => Assert.True(session.Stopped));
        Assert.Empty(pool.Violations);
    }

    [Fact]
    public async Task ASessionReportingAnotherBuildReceivesNoFrame()
    {
        await using var pool = await SessionsPoolStub.StartAsync();
        using var connector = Connector(pool, new FakeCredential());
        await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, "sha256:" + new string('2', 64)));
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 1 }, CancellationToken.None)).Failure);
        Assert.All(pool.Sessions, session => Assert.Empty(session.Inspection.Frames));
        await runner.StopAsync();
    }

    [ServiceWorkerFact]
    public async Task SessionsRunTheServiceWithRealSandboxedChildren()
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            await using var direct = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(root));
            await using var pool = await SessionsPoolStub.StartAsync(realChildren: root);
            using var connector = Connector(pool, new FakeCredential());
            await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, direct.BuildIdentity));
            var expected = await new FerruleInspectionBackend(direct).ValidateAsync(FerruleServiceTestSupport.Artifact("invalid-function"));
            var actual = await new FerruleInspectionBackend(runner).ValidateAsync(FerruleServiceTestSupport.Artifact("invalid-function"));
            Assert.Equal(MappingOperationStatus.InvalidMapping, actual.Status);
            Assert.True(actual.Provenance.Confirmed);
            Assert.Equal(direct.BuildIdentity, actual.Provenance.BackendBuild);
            Assert.Equal(expected.Diagnostics.Select(d => d.Code + d.Location), actual.Diagnostics.Select(d => d.Code + d.Location));
            Assert.All(pool.Sessions, session => Assert.True(session.Stopped));
            await runner.StopAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompileOperationsRunInFreshSessionsAndCancellationReachesTheirOwn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = await SessionsPoolStub.StartAsync(session => session.Compile.Behavior = async (frame, token) =>
        {
            if (frame.Span[0] == 1) return new WorkerProcessResult([.. frame.ToArray(), 7], WorkerProcessFailure.None);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        }, compile: true);
        using var connector = Connector(pool, new FakeCredential());
        await using var runner = Runner(connector, FerruleServiceProtocol.CompileProfile);
        var frame = new byte[DockerWorkerProfile.Build.MaximumRequestBytes];
        frame[0] = 1;
        var compiled = await runner.RunAsync(frame, CancellationToken.None);
        Assert.Equal(WorkerProcessFailure.None, compiled.Failure);
        Assert.Equal([.. frame, 7], compiled.Output);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(new byte[] { 2 }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        var sessions = pool.Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.Equal(["GET /v1/ready", "POST /v1/operations/compile/"], sessions[0].Requests.Select(Shape));
        Assert.Equal(["GET /v1/ready", "POST /v1/operations/compile/", "POST /v1/operations/compile/"], sessions[1].Requests.Select(Shape));
        Assert.All(sessions, session =>
        {
            Assert.Single(session.Compile.Frames);
            Assert.Empty(session.Inspection.Frames);
            Assert.True(session.Stopped);
        });
        Assert.Empty(pool.Violations);
        await runner.StopAsync();
    }

    [ServiceCompileFact]
    public async Task SessionsCompileWithRealSandboxedChildren()
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            await using var direct = new ProcessCompileRunner(FerruleServiceTestSupport.CompileOptions(root));
            var (directCompiler, intent, native) = FerruleServiceCompileTests.Compiler(direct, "valid-basic");
            var expected = (await directCompiler.BuildAsync(intent, new(native, native.Length))).Copy();
            await using var pool = await SessionsPoolStub.StartAsync(realChildren: root, compile: true);
            using var connector = Connector(pool, new FakeCredential());
            await using var runner = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.CompileProfile, direct.BuildIdentity, direct.ToolchainIdentity));
            var (compiler, remoteIntent, _) = FerruleServiceCompileTests.Compiler(runner, "valid-basic");
            Assert.Equal(expected, (await compiler.BuildAsync(remoteIntent, new(native, native.Length))).Copy());
            Assert.All(pool.Sessions, session => Assert.True(session.Stopped));
            await runner.StopAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SettingsAdmitOnlyACanonicalPoolEndpointIdentityAndClientId()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AzureSessionsRunnerSettings.PoolEndpoint] = "https://weft-ferrule.example-env.westus2.azurecontainerapps.io",
            [AzureSessionsRunnerSettings.Identity] = Identity,
            [AzureSessionsRunnerSettings.ClientId] = "5c5b1a3e-0d3c-4f69-9d8e-0a8f0c3c1a2b"
        };
        var settings = AzureSessionsRunnerSettings.Read(key => values.GetValueOrDefault(key));
        Assert.Equal(Guid.Parse("5c5b1a3e-0d3c-4f69-9d8e-0a8f0c3c1a2b"), settings.ManagedIdentityClientId);
        Assert.True(AzureSessionsRunnerSettings.Configured(key => values.GetValueOrDefault(key)));
        using (settings.CreateConnector()) { }
        foreach (var (name, value) in new (string, string?)[]
        {
            (AzureSessionsRunnerSettings.PoolEndpoint, null), (AzureSessionsRunnerSettings.PoolEndpoint, "http://pool.example"),
            (AzureSessionsRunnerSettings.PoolEndpoint, "https://pool.example/sessions"), (AzureSessionsRunnerSettings.PoolEndpoint, "https://10.0.0.4"),
            (AzureSessionsRunnerSettings.PoolEndpoint, "https://pool.example?x=1"), (AzureSessionsRunnerSettings.Identity, null),
            (AzureSessionsRunnerSettings.ClientId, "5C5B1A3E-0D3C-4F69-9D8E-0A8F0C3C1A2B"), (AzureSessionsRunnerSettings.ClientId, Guid.Empty.ToString("D")),
            (AzureSessionsRunnerSettings.ClientId, "")
        })
        {
            var copy = new Dictionary<string, string?>(values) { [name] = value };
            Assert.Throws<InvalidOperationException>(() => AzureSessionsRunnerSettings.Read(key => copy.GetValueOrDefault(key)));
        }
        values.Remove(AzureSessionsRunnerSettings.ClientId);
        Assert.Null(AzureSessionsRunnerSettings.Read(key => values.GetValueOrDefault(key)).ManagedIdentityClientId);
    }

    private static string Shape(SessionsPoolStub.Forwarded request) =>
        request.Method + " " + (request.Path.StartsWith(FerruleServiceProtocol.OperationsPath, StringComparison.Ordinal)
            ? request.Path[..(request.Path.IndexOf('/', FerruleServiceProtocol.OperationsPath.Length) + 1)] : request.Path);

    private static AzureSessionsConnector Connector(SessionsPoolStub pool, FakeCredential credential) =>
        new(new AzureSessionsConnectorOptions(pool.Endpoint, credential)
        {
            ServerCertificateValidation = (_, certificate, _, _) => InspectionTransport.HasDigest(certificate, pool.CertificatePin)
        });

    private static FerruleServiceRunner Runner(AzureSessionsConnector connector, string profile) =>
        new(new(connector, profile, Identity, profile == FerruleServiceProtocol.CompileProfile ? "sha256:" + new string('3', 64) : null)
        { RetryDelay = TimeSpan.FromMilliseconds(20) });

    private sealed class FakeCredential(string token = SessionsPoolStub.Token) : TokenCredential
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal bool Fail { get; set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal([AzureSessionsConnectorOptions.TokenScope], requestContext.Scopes);
            Interlocked.Increment(ref calls);
            if (Fail) throw new InvalidOperationException("Managed identity is unavailable.");
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    /// <summary>A local stand-in for a custom-container session pool's management endpoint.</summary>
    internal sealed class SessionsPoolStub : IAsyncDisposable
    {
        internal const string Token = "stub-pool-token";
        private readonly object gate = new();
        private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
        private readonly List<string> identifiers = [];
        private readonly List<string> violations = [];
        private readonly Action<Session>? configure;
        private readonly string? realChildren;
        private readonly bool compile;
        private readonly HttpClient forwarder = new() { Timeout = Timeout.InfiniteTimeSpan };
        private WebApplication? application;
        private X509Certificate2? certificate;

        private SessionsPoolStub(Action<Session>? configure, string? realChildren, bool compile)
        {
            this.configure = configure;
            this.realChildren = realChildren;
            this.compile = compile;
        }

        internal Uri Endpoint { get; private set; } = null!;
        internal string CertificatePin => TransportCase.Pin(certificate!);
        internal int RefuseAllocations { get; set; }
        internal string? FailAfterForwarding { get; set; }

        internal IReadOnlyList<Session> Sessions { get { lock (gate) return [.. sessions.Values.OrderBy(session => session.Order)]; } }
        internal IReadOnlyList<string> Identifiers { get { lock (gate) return [.. identifiers]; } }
        internal IReadOnlyList<string> Violations { get { lock (gate) return [.. violations]; } }

        internal static async Task<SessionsPoolStub> StartAsync(Action<Session>? configure = null, string? realChildren = null, bool compile = false)
        {
            var stub = new SessionsPoolStub(configure, realChildren, compile);
            await stub.StartCoreAsync();
            return stub;
        }

        private async Task StartCoreAsync()
        {
            certificate = TransportCase.Certificate("CN=localhost");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Protocols = HttpProtocols.Http1AndHttp2;
                listener.UseHttps(certificate);
            }));
            application = builder.Build();
            application.Run(HandleAsync);
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            // The connector requires a DNS host; localhost resolves to the loopback listener.
            Endpoint = new Uri("https://localhost:" + new Uri(address.Replace("://127.0.0.1", "://localhost", StringComparison.Ordinal)).Port);
        }

        private async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            if (request.Headers.Authorization.ToString() != "Bearer " + Token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var identifier = request.Query["identifier"].ToString();
            if (identifier.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (request.Path == "/.management/stopSession")
            {
                Session? stopped;
                lock (gate) sessions.TryGetValue(identifier, out stopped);
                if (request.Query["api-version"] != AzureSessionsConnectorOptions.ManagementApiVersion || stopped is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await stopped.StopAsync();
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            Session session;
            lock (gate)
            {
                if (!identifiers.Contains(identifier)) identifiers.Add(identifier);
                if (sessions.TryGetValue(identifier, out var existing))
                {
                    if (existing.Stopped) violations.Add("Reused identifier " + identifier);
                    session = existing;
                }
                else if (RefuseAllocations > 0)
                {
                    RefuseAllocations--;
                    session = null!;
                }
                else
                {
                    session = new Session(identifier, sessions.Count);
                    sessions.Add(identifier, session);
                }
            }

            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await session.EnsureStartedAsync(configure, realChildren, compile);
            var body = new MemoryStream();
            await request.Body.CopyToAsync(body, context.RequestAborted);
            session.Record(new Forwarded(request.Method, request.Path.ToString()));
            using var forwarded = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(session.Address, request.Path.ToString()));
            if (body.Length > 0 || request.Method == HttpMethods.Post)
            {
                forwarded.Content = new ByteArrayContent(body.ToArray());
                if (request.ContentType is { } type) forwarded.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            }
            foreach (var header in request.Headers.Where(header => header.Key.StartsWith("Weft-", StringComparison.OrdinalIgnoreCase)))
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            using var answer = await forwarder.SendAsync(forwarded, context.RequestAborted);
            var bytes = await answer.Content.ReadAsByteArrayAsync(context.RequestAborted);
            if (FailAfterForwarding is { } prefix && request.Path.StartsWithSegments(prefix.TrimEnd('/')))
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            context.Response.StatusCode = (int)answer.StatusCode;
            foreach (var header in answer.Headers.Where(header => header.Key.StartsWith("Weft-", StringComparison.OrdinalIgnoreCase)))
                context.Response.Headers[header.Key] = header.Value.ToArray();
            if (answer.Headers.RetryAfter is { } retry) context.Response.Headers.RetryAfter = retry.ToString();
            if (answer.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in Sessions) await session.StopAsync();
            if (application is not null)
            {
                await application.StopAsync();
                await application.DisposeAsync();
            }
            forwarder.Dispose();
            certificate?.Dispose();
        }

        internal sealed record Forwarded(string Method, string Path);

        /// <summary>One session: its own Ferrule service instance listening in platform-sandbox mode.</summary>
        internal sealed class Session(string identifier, int order)
        {
            private readonly List<Forwarded> requests = [];
            private readonly SemaphoreSlim startGate = new(1, 1);
            private WorkerTransportServer? server;
            private IAsyncDisposable[] children = [];

            internal string Identifier => identifier;
            internal int Order => order;
            internal ScriptedRunner Inspection { get; } = new();
            internal ScriptedRunner Preview { get; } = new();
            internal ScriptedRunner Compile { get; } = new();
            internal Uri Address { get; private set; } = null!;
            internal bool Stopped { get; private set; }
            internal IReadOnlyList<Forwarded> Requests { get { lock (requests) return [.. requests]; } }

            internal void Record(Forwarded request)
            {
                lock (requests) requests.Add(request);
            }

            internal async Task EnsureStartedAsync(Action<Session>? configure, string? realChildren, bool compile)
            {
                await startGate.WaitAsync();
                try
                {
                    if (server is not null) return;
                    configure?.Invoke(this);
                    Dictionary<string, IInspectionWorkerRunner> runners;
                    string? toolchain = null;
                    if (realChildren is null)
                    {
                        runners = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
                        {
                            [FerruleServiceProtocol.InspectionProfile] = Inspection,
                            [FerruleServiceProtocol.PreviewProfile] = Preview
                        };
                        if (compile)
                        {
                            runners[FerruleServiceProtocol.CompileProfile] = Compile;
                            toolchain = "sha256:" + new string('3', 64);
                        }
                    }
                    else
                    {
                        var inspection = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(realChildren));
                        var preview = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(realChildren, DockerWorkerProfile.Preview));
                        runners = new Dictionary<string, IInspectionWorkerRunner>(StringComparer.Ordinal)
                        {
                            [FerruleServiceProtocol.InspectionProfile] = inspection,
                            [FerruleServiceProtocol.PreviewProfile] = preview
                        };
                        children = [inspection, preview];
                        if (compile)
                        {
                            var compiler = new ProcessCompileRunner(FerruleServiceTestSupport.CompileOptions(realChildren));
                            runners[FerruleServiceProtocol.CompileProfile] = compiler;
                            toolchain = compiler.ToolchainIdentity;
                            children = [inspection, preview, compiler];
                        }
                    }
                    server = new WorkerTransportServer(new WorkerTransportServerOptions(new IPEndPoint(IPAddress.Loopback, 0))
                    {
                        Concurrency = 1,
                        Capacity = 1,
                        AdmissionWait = TimeSpan.Zero,
                        CompileToolchain = toolchain
                    }, runners);
                    await server.StartAsync(CancellationToken.None);
                    Address = new Uri("http://127.0.0.1:" + server.Port);
                }
                finally
                {
                    startGate.Release();
                }
            }

            internal async Task StopAsync()
            {
                if (Stopped) return;
                Stopped = true;
                if (server is not null)
                {
                    await server.StopAsync(CancellationToken.None);
                    await server.DisposeAsync();
                }
                foreach (var child in children) await child.DisposeAsync();
            }
        }
    }
}
