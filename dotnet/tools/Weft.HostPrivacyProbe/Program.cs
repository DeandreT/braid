using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Weft.Identity;
using Weft.Tests;

namespace Weft.HostPrivacyProbe;

internal static class Program
{
    internal const string Marker = "weft-owned-host-private-marker";

    public static async Task<int> Main()
    {
        var report = new HostPrivacyReport();
        using var observation = new HostPrivacyObservation(Marker);
        using var output = new HostMarkerWriter(Console.Out, Marker);
        using var errorOutput = new HostMarkerWriter(Console.Error, Marker);
        Console.SetOut(output); Console.SetError(errorOutput);
        string? operation = Environment.GetEnvironmentVariable("WEFT_HOST_PROBE_OPERATION");
        report.Operation = operation is "reference" or "protected" ? operation : "invalid";
        IHost? active = null; Task? entrypoint = null;
        bool entrypointWaitAttempted = false;
        Exception? primary = null;
        string phase = "initialization";
        try
        {
            observation.IdentityLogger = new HostIdentityMarkerLogger(Marker);
            LogHelper.Logger = observation.IdentityLogger;
            IdentityModelEventSource.ShowPII = true;
            IdentityModelEventSource.LogCompleteSecurityArtifact = true;
            if (operation == "reference")
            {
                phase = "reference-start";
                active = Host.CreateDefaultBuilder([]).ConfigureLogging(logging =>
                    logging.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Trace))
                    .ConfigureWebHostDefaults(web =>
                    {
                        web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
                        web.Configure(app => app.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; }));
                    }).Build();
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(35)); await active.StartAsync(startup.Token);
                string bound = active.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = Client(new Uri(bound), null);
                observation.LateSubscribe();
                phase = "reference-incoming";
                await IncomingAsync(client, protectedHost: false, report);
                phase = "reference-stop";
                Task stop = active.StopAsync(CancellationToken.None); await stop.WaitAsync(TimeSpan.FromSeconds(10));
                Task dispose = active is IAsyncDisposable asyncHost ? asyncHost.DisposeAsync().AsTask() : DisposeSync(active);
                await dispose.WaitAsync(TimeSpan.FromSeconds(10)); active = null;
                phase = "reference-outgoing";
                await OutgoingReferenceAsync(observation, report);
            }
            else if (operation == "protected")
            {
                // Same published entrypoint, not a second builder or a privileged product route.
                phase = "protected-load";
                Assembly server = Assembly.LoadFrom(Required("WEFT_HOST_PROBE_SERVER_ASSEMBLY"));
                Directory.SetCurrentDirectory(Path.GetDirectoryName(server.Location)!);
                MethodInfo main = server.EntryPoint ?? throw new HostProbeFailure("entrypoint-shape");
                entrypoint = InvokeEntrypointAsync(main);
                phase = "protected-start";
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                Task first = await Task.WhenAny(observation.Built.Task, entrypoint).WaitAsync(startup.Token);
                if (first == entrypoint) await entrypoint;
                Require(first == observation.Built.Task, "startup");
                active = await observation.Built.Task;
                phase = "bootstrap-origin";
                var origin = new Uri(Required("WEFT_PUBLIC_ORIGIN"));
                phase = "bootstrap-certificate";
                // Pin only the public certificate; the delivered private key is a separate file.
                using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Required("WEFT_TLS_CERTIFICATE_FILE")));
                phase = "bootstrap-client";
                using var client = Client(origin, certificate);
                phase = "bootstrap-transport";
                await StartupAsync(client, startup.Token);
                phase = "composition";
                var authentication = active.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                var browsers = active.Services.GetServices<ServerSessionBrowser>().ToArray();
                var clients = active.Services.GetServices<EntraBrowserClient>().ToArray();
                var modules = active.Services.GetServices<EntraBrowserAuthentication>().ToArray();
                report.CompositionClosed = authentication.DefaultScheme is null && authentication.DefaultAuthenticateScheme is null
                    && authentication.DefaultChallengeScheme is null && authentication.DefaultForbidScheme is null
                    && authentication.DefaultSignInScheme is null && authentication.DefaultSignOutScheme is null
                    && browsers.Length == 1 && clients.Length == 1 && modules.Length == 1
                    && ReferenceEquals(browsers[0], active.Services.GetRequiredService<ServerSessionBrowser>())
                    && ReferenceEquals(clients[0], active.Services.GetRequiredService<EntraBrowserClient>())
                    && ReferenceEquals(modules[0], active.Services.GetRequiredService<EntraBrowserAuthentication>());
                Require(report.CompositionClosed, "composition");
                observation.LateSubscribe();
                phase = "protected-incoming";
                await IncomingAsync(client, protectedHost: true, report);
                phase = "protected-stop";
                active.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                entrypointWaitAttempted = true;
                await entrypoint.WaitAsync(TimeSpan.FromSeconds(10)); active = null;
                observation.CountIncoming = false;
                phase = "protected-outgoing";
                string[] privateOperands = await OutgoingProtectedAsync(report);
                phase = "malformed-provider-scenario";
                report.MalformedProviderRequests = await HostMalformedProviderScenario.RunAsync();
                output.CheckPrivate(privateOperands); errorOutput.CheckPrivate(privateOperands);
            }
            else throw new HostProbeFailure();
            // These are actual public channels; the protected host must have replaced/disposed them.
            phase = "identity";
            LogHelper.LogWarning(Marker);
            IdentityModelEventSource.Logger.WriteWarning(Marker);
            report.FlagsClosed = !IdentityModelEventSource.ShowPII && !IdentityModelEventSource.LogCompleteSecurityArtifact;
            report.OperationSucceeded = true;
        }
        catch (Exception error)
        {
            var failure = error as HostProbeFailure ?? new HostProbeFailure(phase);
            primary = failure; report.FailureCategory = failure.Category;
        }
        finally
        {
            if (active is not null)
            {
                try
                {
                    active.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                    if (entrypoint is not null)
                    {
                        if (!entrypointWaitAttempted)
                        { entrypointWaitAttempted = true; await entrypoint.WaitAsync(TimeSpan.FromSeconds(10)); }
                        else if (entrypoint.IsCompleted) await entrypoint;
                        else throw new HostProbeFailure("cleanup");
                    }
                    else
                    {
                        Task stop = active.StopAsync(CancellationToken.None); await stop.WaitAsync(TimeSpan.FromSeconds(10));
                        Task dispose = active is IAsyncDisposable asyncHost ? asyncHost.DisposeAsync().AsTask() : DisposeSync(active);
                        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception error)
                {
                    var failure = error as HostProbeFailure ?? new HostProbeFailure("cleanup");
                    primary ??= failure; report.OperationSucceeded = false; report.FailureCategory ??= failure.Category;
                }
            }
            if (entrypoint is not null && (!entrypointWaitAttempted || entrypoint.IsCompleted))
            {
                try
                {
                    if (!entrypointWaitAttempted) { entrypointWaitAttempted = true; await entrypoint.WaitAsync(TimeSpan.FromSeconds(10)); }
                    else await entrypoint;
                }
                catch (Exception error)
                {
                    var failure = error as HostProbeFailure ?? new HostProbeFailure("cleanup");
                    primary ??= failure; report.OperationSucceeded = false; report.FailureCategory ??= failure.Category;
                }
            }
            report.IncomingGraphs = observation.IncomingGraphs;
            report.IncomingMarker = observation.IncomingMarker;
            report.IncomingActivities = observation.IncomingActivities;
            report.OutgoingGraphs = observation.OutgoingGraphs;
            report.OutgoingActivities = observation.OutgoingActivities;
            report.ReferenceContentGraph = observation.ReferenceContentGraph;
            report.IdentityEventMarker = observation.IdentityEventMarker;
            report.IdentityEvents = observation.IdentityEvents;
            report.IdentityLogMarker = observation.IdentityLogger?.MarkerSeen == true;
            report.ConsoleMarker = output.MarkerSeen || errorOutput.MarkerSeen;
            report.SafeHttpStops = observation.SafeHttpStops;
            report.SafeServerStops = observation.SafeServerStops;
            report.LateAttempts = observation.LateAttempts;
            report.ObserverFailed = observation.Failed || output.Overflow || errorOutput.Overflow;
            report.UnsafeEventMarkerObservedPrivately = observation.UnsafeEventMarker;
            try { Write(report); }
            catch (Exception) { primary ??= new HostProbeFailure(); }
        }
        return primary is null && report.OperationSucceeded && !report.ObserverFailed ? 0 : 1;
    }

    private static Task DisposeSync(IDisposable value) { value.Dispose(); return Task.CompletedTask; }
    private static Task InvokeEntrypointAsync(MethodInfo main) => Task.Run(async () =>
    {
        try
        {
            // A compiler-generated synchronous wrapper for async Main may itself block until shutdown.
            object? value = main.Invoke(null, main.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
            if (value is Task<int> code) Require(await code == 0, "entrypoint-exit");
            else if (value is Task work) await work;
            else if (value is int result) Require(result == 0, "entrypoint-exit");
            else Require(value is null, "entrypoint-shape");
        }
        catch (HostProbeFailure) { throw; }
        catch (Exception) { throw new HostProbeFailure("entrypoint-invoke"); }
    });
    private static void Require(bool value, string category) { if (!value) throw new HostProbeFailure(category); }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new HostProbeFailure("required-setting");

    private static HttpClient Client(Uri origin, X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
        if (certificate is not null)
        {
            byte[] pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
            handler.ServerCertificateCustomValidationCallback = (message, actual, _, errors) => actual is not null
                && message.RequestUri?.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority)
                && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                && CryptographicOperations.FixedTimeEquals(pin, actual.GetCertHash(HashAlgorithmName.SHA256));
        }
        return new(handler)
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static async Task StartupAsync(HttpClient client, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string phase = "bootstrap-transport";
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session"); message.Headers.Add("X-Weft-Session", "1");
                using var response = await client.SendAsync(message, token);
                byte[] body = await BoundedAsync(response, token);
                phase = "bootstrap-shape";
                using var document = JsonDocument.Parse(body);
                if (response.IsSuccessStatusCode)
                {
                    Require(document.RootElement.GetProperty("version").GetInt32() == 1
                        && document.RootElement.GetProperty("mode").GetString() == "session", "startup-mode");
                    return;
                }
                Require((int)response.StatusCode == 503, "startup-status");
            }
            catch (HttpRequestException) { }
            catch (HostProbeFailure) { throw; }
            catch (Exception) { throw new HostProbeFailure(phase); }
            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }
    }

    private static async Task IncomingAsync(HttpClient client, bool protectedHost, HostPrivacyReport report)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/callback/entra?code=" + Marker + "&state=" + Marker))
        {
            request.Headers.TryAddWithoutValidation("Cookie", "__Host-Weft.Session=" + Marker);
            using var reply = await client.SendAsync(request, budget.Token);
            byte[] body = await BoundedAsync(reply, budget.Token);
            Require(protectedHost ? (int)reply.StatusCode is 400 or 401 or 403 : (int)reply.StatusCode == 204, "callback");
            Require(!protectedHost || !Encoding.UTF8.GetString(body).Contains(Marker, StringComparison.Ordinal), "body");
            report.CompletedRequests++;
        }
        await MalformedIncomingAsync(client.BaseAddress!, protectedHost ? Required("WEFT_TLS_CERTIFICATE_FILE") : null);
        report.CompletedRequests++;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/callback/entra"))
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Marker));
            using var reply = await client.SendAsync(request, budget.Token); _ = await BoundedAsync(reply, budget.Token); report.CompletedRequests++;
        }
        if (protectedHost)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session"); request.Headers.Add("X-Weft-Session", "1");
            request.Headers.TryAddWithoutValidation("Cookie", Required("WEFT_HOST_PROBE_COOKIE"));
            using var reply = await client.SendAsync(request, budget.Token); using var body = JsonDocument.Parse(await BoundedAsync(reply, budget.Token));
            report.PositiveSession = reply.IsSuccessStatusCode && body.RootElement.GetProperty("state").GetString() == "authenticated";
            using var contextRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/context");
            contextRequest.Headers.TryAddWithoutValidation("Cookie", Required("WEFT_HOST_PROBE_COOKIE"));
            using var contextReply = await client.SendAsync(contextRequest, budget.Token); using var contextBody = JsonDocument.Parse(await BoundedAsync(contextReply, budget.Token));
            report.PositiveCatalog = contextReply.IsSuccessStatusCode && contextBody.RootElement.GetProperty("mode").GetString() == "session";
            Require(report.PositiveSession && report.PositiveCatalog, "positive"); report.CompletedRequests += 2;
        }
    }

    private static async Task MalformedIncomingAsync(Uri origin, string? certificatePath)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(IPAddress.Parse(origin.Host), origin.Port, budget.Token); }
        catch (Exception) { throw new HostProbeFailure("malformed-connect"); }
        await using var network = new NetworkStream(socket, ownsSocket: false); Stream stream = network; SslStream? tls = null;
        string phase = "malformed-tls";
        try
        {
            if (certificatePath is not null)
            {
                using var expected = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
                byte[] pin = expected.GetCertHash(HashAlgorithmName.SHA256);
                tls = new SslStream(network, leaveInnerStreamOpen: true, (_, actual, _, errors) => actual is not null
                    && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                    && CryptographicOperations.FixedTimeEquals(pin, actual.GetCertHash(HashAlgorithmName.SHA256)));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = origin.Host }, budget.Token); stream = tls;
            }
            byte[] request = Encoding.ASCII.GetBytes("GET /api/auth/callback/entra HTTP/1.1\r\nHost: " + origin.Authority + "\r\n" + Marker + "\r\nConnection: close\r\n\r\n");
            phase = "malformed-write";
            await stream.WriteAsync(request, budget.Token); await stream.FlushAsync(budget.Token);
            using var received = new MemoryStream(); var buffer = new byte[1024];
            phase = "malformed-read";
            while (true)
            {
                int count = await stream.ReadAsync(buffer, budget.Token); if (count == 0) break;
                Require(received.Length + count <= 8192, "malformed-bound"); received.Write(buffer, 0, count);
            }
            string reply = Encoding.ASCII.GetString(received.ToArray());
            Require(reply.StartsWith("HTTP/1.1 400", StringComparison.Ordinal) && !reply.Contains(Marker, StringComparison.Ordinal), "malformed-ingress");
        }
        catch (HostProbeFailure) { throw; }
        catch (Exception) { throw new HostProbeFailure(phase); }
        finally { if (tls is not null) await tls.DisposeAsync(); }
    }

    private static async Task<byte[]> BoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[1024];
        while (true) { int count = await stream.ReadAsync(buffer, token); if (count == 0) return output.ToArray(); Require(output.Length + count <= 65_536, "body-bound"); output.Write(buffer, 0, count); }
    }

    private static async Task OutgoingReferenceAsync(HostPrivacyObservation observation, HostPrivacyReport report)
    {
        using var crypto = new EntraProtocolCrypto(); await using var issuer = new EntraProtocolHttpsIssuer(crypto);
        await issuer.StartAsync();
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(crypto.Root);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy },
            ConnectCallback = async (context, token) =>
            {
                Require(context.DnsEndPoint.Host == "login.microsoftonline.com" && context.DnsEndPoint.Port == 443, "reference-authority");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, issuer.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, EntraProtocolFixturePolicy.TokenAddress)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("client_assertion=" + Marker)) };
        request.Content.Headers.ContentType = new("application/x-www-form-urlencoded");
        observation.ExpectedReferenceRequest = request;
        using var reply = await client.SendAsync(request); _ = await BoundedAsync(reply, CancellationToken.None);
        Require((int)reply.StatusCode == 400, "reference-provider"); report.ProviderRequests++;
    }

    private static async Task<string[]> OutgoingProtectedAsync(HostPrivacyReport report)
    {
        string[] operands = [];
        await EntraProtocolHttpsFixture.RunAsync(async fixture =>
        {
            var good = await fixture.FlowAsync(); var goodReply = await fixture.CompleteAsync(good);
            fixture.Success(goodReply); fixture.CheckPrivacy(goodReply); report.ProtocolSuccess = true;
            fixture.Issuer.BreakPkce = true;
            var rejected = await fixture.FlowAsync(); var rejectedReply = await fixture.CompleteAsync(rejected);
            fixture.Denied(rejectedReply); fixture.CheckPrivacy(rejectedReply); fixture.Issuer.BreakPkce = false;
            fixture.Issuer.TokenCase = EntraProtocolTokenCase.BadNonce;
            var invalid = await fixture.FlowAsync(); var invalidReply = await fixture.CompleteAsync(invalid);
            fixture.Denied(invalidReply); fixture.CheckPrivacy(invalidReply); fixture.Issuer.TokenCase = EntraProtocolTokenCase.Good;
            fixture.Issuer.MalformedTokenResponse = true;
            var malformed = await fixture.FlowAsync(); var malformedReply = await fixture.CompleteAsync(malformed);
            Require(malformedReply.Request.Verified is null && malformedReply.Request.Failure is not null
                && malformedReply.Request.EvidencePublished == 0 && fixture.Ports.Calls == 0, "malformed-provider");
            EntraProtocolHttpsFixture.NoApplicationEffects(malformedReply);
            fixture.CheckPrivacy(malformedReply);
            fixture.Issuer.MalformedTokenResponse = false;
            var gate = new EntraProtocolGate(); fixture.Issuer.TokenGate = gate;
            var flow = await fixture.FlowAsync(); var record = fixture.NewRequest(ownCancellation: true);
            Task<EntraProtocolReply> pending = fixture.CompleteAsync(flow, record: record);
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); record.OriginalCancellation!.Cancel();
                var canceled = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Require(canceled.Request.Canceled && canceled.Request.CancellationWasOriginal, "cancellation");
            }
            finally { gate.Release.TrySetResult(); fixture.Issuer.TokenGate = null; }
            report.ProviderRequests = fixture.Ledger().Count;
            Require(report.ProviderRequests > 0 && fixture.Issuer.AssertionAccepted > 0 && fixture.Issuer.PkceAccepted > 0, "provider-positive");
            operands = fixture.Issuer.PrivateOperands().ToArray();
        });
        return operands;
    }

    private static void Write(HostPrivacyReport report)
    {
        string path = Required("WEFT_HOST_PROBE_RESULT"); string directory = Path.GetDirectoryName(path) ?? throw new HostProbeFailure();
        Require(Path.IsPathFullyQualified(path) && new DirectoryInfo(directory).LinkTarget is null
            && File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "result-directory");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Require(bytes.Length <= 8192, "result-bound");
        using var output = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        output.Write(bytes);
    }
}

internal sealed class HostProbeFailure : Exception
{
    internal HostProbeFailure(string category = "operation") : base("The owned host privacy operation failed.")
    { Category = IsClosedCategory(category) ? category : "operation"; }
    internal string Category { get; }
    internal static bool IsClosedCategory(string category) => category is
        "operation" or "cleanup" or "initialization" or "required-setting" or
        "reference-start" or "reference-incoming" or "reference-stop" or "reference-outgoing" or
        "protected-load" or "protected-start" or "protected-startup" or "protected-incoming" or "protected-stop" or "protected-outgoing" or
        "bootstrap-origin" or "bootstrap-certificate" or "bootstrap-client" or "bootstrap-transport" or "bootstrap-shape" or
        "startup" or "startup-mode" or "startup-status" or "composition" or "entrypoint-exit" or "entrypoint-shape" or "entrypoint-invoke" or
        "callback" or "body" or "positive" or "body-bound" or
        "malformed-connect" or "malformed-tls" or "malformed-write" or "malformed-read" or "malformed-bound" or "malformed-ingress" or
        "reference-authority" or "reference-provider" or "malformed-provider" or "cancellation" or "provider-positive" or
        "malformed-provider-scenario" or "malformed-provider-status" or "malformed-provider-header" or "malformed-provider-cleanup" or
        "malformed-provider-bound" or "malformed-provider-eof" or "malformed-provider-request" or "malformed-provider-peer" or
        "identity" or "result-directory" or "result-bound";
    public override string ToString() => nameof(HostProbeFailure);
}

internal sealed class HostPrivacyReport
{
    public int Version { get; } = 1;
    public string Operation { get; set; } = "invalid";
    public bool OperationSucceeded { get; set; }
    public string? FailureCategory { get; set; }
    public bool PositiveSession { get; set; }
    public bool PositiveCatalog { get; set; }
    public bool ProtocolSuccess { get; set; }
    public bool CompositionClosed { get; set; }
    public bool FlagsClosed { get; set; }
    public int CompletedRequests { get; set; }
    public int ProviderRequests { get; set; }
    public int MalformedProviderRequests { get; set; }
    public int IncomingGraphs { get; set; }
    public bool IncomingMarker { get; set; }
    public int IncomingActivities { get; set; }
    public int OutgoingGraphs { get; set; }
    public int OutgoingActivities { get; set; }
    public bool ReferenceContentGraph { get; set; }
    public bool IdentityEventMarker { get; set; }
    public int IdentityEvents { get; set; }
    public bool IdentityLogMarker { get; set; }
    public bool ConsoleMarker { get; set; }
    public int SafeHttpStops { get; set; }
    public int SafeServerStops { get; set; }
    public int LateAttempts { get; set; }
    public bool ObserverFailed { get; set; }
    public bool UnsafeEventMarkerObservedPrivately { get; set; }
}
