using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Braid.Identity;

internal sealed record EntraFixtureSend(string Method, string LogicalPath);

// Internal test composition only. This is not a configurable production provider.
internal sealed class EntraHttpsFixtureTransport
{
    private readonly object sync = new();
    private readonly List<EntraFixtureSend> attempts = [];
    private readonly int port;
    private readonly X509Certificate2 root;

    internal EntraHttpsFixtureTransport(int loopbackPort, X509Certificate2 trustRoot)
    {
        if (loopbackPort is < 1 or > 65535 || trustRoot is null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        port = loopbackPort;
        root = trustRoot; // Borrowed through the owning runtime's HTTP drain.
    }

    internal IReadOnlyList<EntraFixtureSend> SnapshotAttempts()
    {
        lock (sync) return Array.AsReadOnly(attempts.ToArray());
    }

    internal void Record(HttpRequestMessage request)
    {
        lock (sync)
        {
            if (attempts.Count == 256)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            attempts.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath));
        }
    }

    internal void Configure(SocketsHttpHandler handler)
    {
        var trust = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        trust.CustomTrustStore.Add(root);
        handler.SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = trust };
        handler.ConnectCallback = async (context, cancellation) =>
        {
            if (context.DnsEndPoint.Host != EntraEndpointPolicy.Host || context.DnsEndPoint.Port != 443)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
    }
}
