using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Deployment.Triggers;

namespace Braid.Tests;

/// <summary>The HTTPS destination against an in-process loopback receiver with a self-signed certificate.</summary>
public sealed class HttpsOutputDestinationTests
{
    internal static readonly string KeyFile = Path.Combine(Path.GetTempPath(), "braid-trigger-hook.key");
    internal static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task An_accepted_post_carries_the_exact_bytes_idempotency_key_digest_and_signature()
    {
        await using var receiver = await Receiver.StartAsync(_ => Task.FromResult(202));
        var delivery = TriggerQueueProcessingTests.Delivery(version: 3);
        Assert.Equal(DeliveryObservationKind.Accepted, await receiver.Destination().SendAsync(delivery, default));
        var request = Assert.Single(receiver.Requests);
        Assert.Equal(delivery.CopyContent(), request.Body);
        Assert.Equal(delivery.DeliveryId.ToString("D"), request.Headers["Idempotency-Key"]);
        Assert.Equal(delivery.DeliveryId.ToString("D"), request.Headers["X-Braid-Delivery-Id"]);
        Assert.Equal(delivery.ContentDigest.Value, request.Headers["X-Braid-Content-SHA256"]);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        var signed = Encoding.ASCII.GetBytes(request.Headers["X-Braid-Timestamp"] + ".").Concat(delivery.CopyContent()).ToArray();
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromBase64String(Key), signed)), request.Headers["X-Braid-Signature"]);
    }

    [Theory]
    [InlineData(409, DeliveryObservationKind.ProvenKnownNotAccepted)]
    [InlineData(500, DeliveryObservationKind.Unknown)]
    [InlineData(503, DeliveryObservationKind.Unknown)]
    [InlineData(200, DeliveryObservationKind.Unknown)]
    public async Task Only_configured_statuses_are_definite_and_every_other_reply_is_ambiguous(int status, DeliveryObservationKind expected)
    {
        await using var receiver = await Receiver.StartAsync(_ => Task.FromResult(status));
        Assert.Equal(expected, await receiver.Destination().SendAsync(TriggerQueueProcessingTests.Delivery(version: 3), default));
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task A_reset_after_the_request_was_written_or_a_timeout_after_send_is_unknown()
    {
        await using var reset = await Receiver.StartAsync(context => { context.Abort(); return Task.FromResult(0); });
        Assert.Equal(DeliveryObservationKind.Unknown, await reset.Destination().SendAsync(TriggerQueueProcessingTests.Delivery(version: 3), default));
        Assert.Single(reset.Requests);
        await using var slow = await Receiver.StartAsync(async context => { await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted); return 202; });
        using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(DeliveryObservationKind.Unknown, await slow.Destination().SendAsync(TriggerQueueProcessingTests.Delivery(version: 3), window.Token));
        Assert.Single(slow.Requests);
    }

    [Fact]
    public async Task No_connection_or_a_refused_tls_handshake_is_proven_not_accepted_before_any_request()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var closed = new HttpsDestinationConnector(new("hook", 3), new Uri("https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/orders"), false,
            [202], [409], KeyFile);
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await Destination(closed).SendAsync(TriggerQueueProcessingTests.Delivery(version: 3), default));
        await using var receiver = await Receiver.StartAsync(_ => Task.FromResult(202));
        // Without the Development pin the self-signed certificate is refused during the handshake.
        var unpinned = new HttpsDestinationConnector(new("hook", 3), receiver.Endpoint, false, [202], [409], KeyFile);
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await Destination(unpinned).SendAsync(TriggerQueueProcessingTests.Delivery(version: 3), default));
        Assert.Equal(DeliveryObservationKind.PermanentFailure, await receiver.Destination().SendAsync(TriggerQueueProcessingTests.Delivery(version: 2), default));
        Assert.Empty(receiver.Requests);
    }

    [Fact]
    public void Connectors_refuse_unsafe_endpoints_and_unexplicit_statuses()
    {
        HttpsDestinationConnector Create(string url, bool hosted, int[] accepted, int[] rejected, string? pin = null) =>
            new(new("hook", 1), new Uri(url), hosted, accepted, rejected, KeyFile, pin);
        Assert.Throws<ArgumentException>(() => Create("http://example.com/hook", true, [200], []));
        Assert.Throws<ArgumentException>(() => Create("https://127.0.0.1/hook", true, [200], []));
        Assert.Throws<ArgumentException>(() => Create("https://example.com:8443/hook", true, [200], []));
        Assert.Throws<ArgumentException>(() => Create("https://example.com/hook?token=x", true, [200], []));
        Assert.Throws<ArgumentException>(() => Create("https://user:pass@example.com/hook", true, [200], []));
        Assert.Throws<ArgumentException>(() => Create("https://example.com/hook", true, [], []));
        Assert.Throws<ArgumentException>(() => Create("https://example.com/hook", true, [302], []));
        Assert.Throws<ArgumentException>(() => Create("https://example.com/hook", true, [200], [200]));
        Assert.Throws<ArgumentException>(() => Create("https://example.com/hook", true, [200], [], new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new HttpsDestinationConnector(new("hook", 1), new Uri("https://example.com/hook"), true, [200], [], "relative.key"));
        _ = Create("https://example.com/hook", true, [200, 202], [409, 422]);
        _ = Create("https://127.0.0.1:8443/hook", false, [202], [], new string('a', 64));
    }

    internal static HttpsOutputDestination Destination(HttpsDestinationConnector connector) =>
        new(connector, (path, _) => path == KeyFile ? Key : throw new FileNotFoundException(), TimeProvider.System);

    internal sealed record ReceivedRequest(IReadOnlyDictionary<string, string> Headers, byte[] Body);

    /// <summary>A loopback HTTPS receiver whose reply each test chooses; a reply of 0 means the handler already ended the request.</summary>
    internal sealed class Receiver : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly X509Certificate2 certificate;
        private Receiver(WebApplication application, X509Certificate2 certificate, Uri endpoint)
        { this.application = application; this.certificate = certificate; Endpoint = endpoint; }

        internal Uri Endpoint { get; }
        internal ConcurrentQueue<ReceivedRequest> Requests { get; } = new();
        internal string CertificateSha256 => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        internal HttpsDestinationConnector Connector(long version = 3) => new(new("hook", version), Endpoint, false, [202], [409], KeyFile, CertificateSha256);
        internal HttpsOutputDestination Destination(long version = 3) => HttpsOutputDestinationTests.Destination(Connector(version));

        internal static async Task<Receiver> StartAsync(Func<HttpContext, Task<int>> reply)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Braid trigger receiver", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listener => { listener.Protocols = HttpProtocols.Http1; listener.UseHttps(certificate); }));
            var application = builder.Build();
            Receiver? receiver = null;
            application.Run(async context =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                receiver!.Requests.Enqueue(new(context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    body.ToArray()));
                var status = await reply(context);
                if (status != 0) context.Response.StatusCode = status;
            });
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await application.StartAsync(start.Token);
            var address = new Uri(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            receiver = new(application, certificate, new Uri("https://127.0.0.1:" + address.Port.ToString(CultureInfo.InvariantCulture) + "/orders"));
            return receiver;
        }

        public async ValueTask DisposeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await application.StopAsync(stop.Token); } catch (OperationCanceledException) { }
            await application.DisposeAsync();
            certificate.Dispose();
        }
    }
}
