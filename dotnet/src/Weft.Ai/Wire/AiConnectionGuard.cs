using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Weft.Ai.Wire;

// Each round has a fresh instance. Reservation occurs before the first await;
// a SocketsHttpHandler internal retry cannot establish a second HTTP stream.
internal sealed class AiConnectionGuard
{
    private readonly string _host;
    private readonly int _port;
    private int _reserved;
    internal AiConnectionGuard(Uri endpoint) { _host = endpoint.IdnHost.Trim('[', ']'); _port = endpoint.Port; }
    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _reserved, 1) != 0) throw new HttpRequestException("AI connection refused.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(context.DnsEndPoint.Host.Trim('[', ']'), _host)
            || context.DnsEndPoint.Port != _port) throw new HttpRequestException("AI connection refused.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, true);
        }
        catch (OperationCanceledException) { socket.Dispose(); throw new OperationCanceledException(ct); }
        catch { socket.Dispose(); throw new HttpRequestException("AI connection failed."); }
    }
    public override string ToString() => nameof(AiConnectionGuard);
}
