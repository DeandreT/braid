using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// Opens one channel per operation to one Ferrule service instance. A connector supplies the transport and the
/// caller's authentication; it never reads, changes or retries frames. Platform sandboxes plug in here without
/// changing the runner or the service.
/// </summary>
public interface IFerruleServiceConnector
{
    /// <summary>A fresh channel for exactly one operation; the runner disposes it once the operation has settled.</summary>
    IFerruleServiceChannel Open();
}

/// <summary>Every request of one channel, including a cancellation, reaches the same service instance.</summary>
public interface IFerruleServiceChannel : IAsyncDisposable
{
    /// <summary>
    /// Sends one request whose URI is a service-relative path. The channel completes the address, protocol version
    /// and authentication, and returns once the response headers arrive. It never sends a request twice.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>Trusted host configuration for one pinned Ferrule service endpoint, never supplied by a mapping or browser.</summary>
public sealed class PinnedServiceConnectorOptions
{
    public PinnedServiceConnectorOptions(Uri endpoint, string serviceCertificateSha256, X509Certificate2 clientCertificate)
    {
        ArgumentNullException.ThrowIfNull(clientCertificate);
        RemoteInspectionWorkerOptions.RequireEndpoint(endpoint);
        if (!InspectionTransport.IsCertificateDigest(serviceCertificateSha256))
            throw new ArgumentException("The Ferrule service certificate pin must be a lowercase SHA-256 digest.", nameof(serviceCertificateSha256));
        if (!clientCertificate.HasPrivateKey)
            throw new ArgumentException("The Ferrule service client certificate requires its private key.", nameof(clientCertificate));
        Endpoint = endpoint;
        ServiceCertificateSha256 = serviceCertificateSha256;
        ClientCertificate = clientCertificate;
    }

    /// <summary>One canonical HTTPS origin; a layer-4 balancer behind it keeps each connection on one replica.</summary>
    public Uri Endpoint { get; }
    public string ServiceCertificateSha256 { get; }
    /// <summary>Borrowed; its owner disposes it after every runner using it has stopped.</summary>
    public X509Certificate2 ClientCertificate { get; }
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Pinned mutual TLS: each channel is one new HTTP/2 connection on which ready, operation and cancellation travel
/// as streams, so they all reach the replica that accepted the connection. The handler cannot open a second
/// connection, so neither a request nor a cancellation can be delivered elsewhere or twice.
/// </summary>
public sealed class PinnedServiceConnector : IFerruleServiceConnector
{
    private readonly PinnedServiceConnectorOptions options;
    private readonly SslStreamCertificateContext clientContext;

    public PinnedServiceConnector(PinnedServiceConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        // The service pins this exact certificate; no issuer chain is fetched or sent.
        clientContext = SslStreamCertificateContext.Create(options.ClientCertificate, additionalCertificates: null, offline: true);
    }

    public IFerruleServiceChannel Open() => new Channel(options, CreateClient());

    private HttpClient CreateClient()
    {
        var guard = new SingleConnection(options.Endpoint);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectionsPerServer = 1,
            EnableMultipleHttp2Connections = false,
            MaxResponseHeadersLength = 16,
            MaxResponseDrainSize = 0,
            ResponseDrainTimeout = TimeSpan.Zero,
            ActivityHeadersPropagator = null,
            ConnectCallback = guard.ConnectAsync,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificateContext = clientContext,
                // Only the pinned service certificate is admitted; neither trust stores nor host names select it.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    InspectionTransport.HasDigest(certificate, options.ServiceCertificateSha256),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class Channel(PinnedServiceConnectorOptions options, HttpClient client) : IFerruleServiceChannel
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.RequestUri is not { IsAbsoluteUri: false } path)
                throw new ArgumentException("A service-relative request path is required.", nameof(request));
            request.RequestUri = new Uri(options.Endpoint, path);
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One connection per channel: an internal handler retry cannot reach another replica or deliver twice.</summary>
    private sealed class SingleConnection(Uri endpoint)
    {
        private int reserved;

        internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref reserved, 1) != 0 ||
                !string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), endpoint.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
                context.DnsEndPoint.Port != endpoint.Port)
                throw new HttpRequestException("The Ferrule service connection was refused.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
