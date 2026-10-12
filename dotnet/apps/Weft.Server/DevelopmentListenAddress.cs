using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Weft.Server;

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
        server.Listen(address.Address, address.Port);
    }

    public static IPEndPoint Parse(string? configured)
    {
        if (!IPEndPoint.TryParse(configured ?? "127.0.0.1:4100", out var address)
            || !IPAddress.IsLoopback(address.Address))
        {
            throw new ArgumentException("WEFT_DEV_LISTEN_ADDR must be a loopback IP address and port.");
        }
        return address;
    }
}
