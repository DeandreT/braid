using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Braid.Server;

public static class DevelopmentListenAddress
{
    public static void ConfigureKestrel(KestrelServerOptions server, IPEndPoint address)
    {
        if (!IPAddress.IsLoopback(address.Address))
        {
            throw new ArgumentException("The development server requires a loopback listener.", nameof(address));
        }
        // Replace configured endpoints so environment settings cannot add public listeners.
        server.Configure();
        server.Limits.MaxRequestLineSize = 65_536;
        // Only this HTTP/1 request-line profile is qualified; HTTP/2 has separate bounds.
        server.Listen(address.Address, address.Port, listener => listener.Protocols = HttpProtocols.Http1);
    }

    public static IPEndPoint Parse(string? configured)
    {
        if (!IPEndPoint.TryParse(configured ?? "127.0.0.1:4100", out var address)
            || !IPAddress.IsLoopback(address.Address))
        {
            throw new ArgumentException("BRAID_DEV_LISTEN_ADDR must be a loopback IP address and port.");
        }
        return address;
    }
}
