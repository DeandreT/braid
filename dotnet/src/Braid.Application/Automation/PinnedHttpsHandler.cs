using System.Net;
using System.Net.Sockets;

namespace Braid.Application.Automation;

/// <summary>
/// The safe HTTPS connection shared by notification webhooks and trigger HTTPS destinations: one connection to the exact
/// host and port, every resolved address admitted by <see cref="NotificationUrlPolicy"/>, and no redirects, proxies or cookies.
/// </summary>
public static class PinnedHttpsHandler
{
    /// <param name="connected">Called once the TCP connection exists; before it, nothing can have been sent.</param>
    public static SocketsHttpHandler Create(Uri uri, bool hosted, Action? connected = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, cancellation) =>
            {
                if (context.DnsEndPoint.Host != uri.DnsSafeHost || context.DnsEndPoint.Port != uri.Port)
                    throw new HttpRequestException("Notification connection refused.");
                var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellation).ConfigureAwait(false);
                if (addresses.Length is < 1 or > 32 || addresses.Any(a => !NotificationUrlPolicy.Allowed(a, hosted)))
                    throw new HttpRequestException("Notification address refused.");
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses[0], uri.Port, cancellation).ConfigureAwait(false); }
                catch { socket.Dispose(); throw; }
                connected?.Invoke();
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
    }
}
