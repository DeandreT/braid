using System.Net;
using System.Net.Sockets;
using System.Text;
using Weft.BrowserQualification;

namespace Weft.Tests;

// Test-parent owned; no TLS termination, request inspection, DNS or parsed upstream.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class BrowserQualificationConnectProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly int appPort;
    private readonly CancellationTokenSource intake = new();
    private readonly List<Task> originals = [];
    private readonly List<Socket> sockets = [];
    private readonly Task accepting;
    private bool closing;
    private Task? close;
    // The single inert proxy-vector destination (forbiddenTarget in the browser harness).
    internal const string ForbiddenAuthority = "192.0.2.1:44443";
    private int accepted, rejected, forbidden, connected, failed;
    internal string Origin { get; }
    internal int Rejected => Volatile.Read(ref rejected);
    internal int RejectedForbidden => Volatile.Read(ref forbidden);
    internal int Connected => Volatile.Read(ref connected);
    internal bool Settled { get; private set; }
    internal BrowserQualificationConnectProxy(int appPort)
    {
        QualificationWire.Require(appPort is > 0 and <= 65535); this.appPort = appPort;
        listener.Start(16); Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync(); Observe(accepting);
    }
    private static void Observe(Task original) => _ = original.ContinueWith(task => { _ = task.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                Socket socket = await listener.AcceptSocketAsync(intake.Token);
                lock (sockets)
                {
                    if (closing || ++accepted > 128) { socket.Dispose(); Interlocked.Increment(ref rejected); continue; }
                    sockets.Add(socket);
                    Task original = TunnelAsync(socket); Observe(original); originals.Add(original);
                }
            }
        }
        catch (OperationCanceledException) when (intake.IsCancellationRequested) { }
        catch (SocketException) when (intake.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (intake.IsCancellationRequested) { }
        catch { Interlocked.Increment(ref failed); }
    }
    internal static bool Permits(ReadOnlySpan<byte> bytes, int port)
    {
        if (bytes.Length is < 4 or > 4096 || bytes.IndexOf((byte)0) >= 0) return false;
        foreach (byte value in bytes) if (value > 126 || value < 32 && value is not (13 or 10)) return false;
        string text = Encoding.ASCII.GetString(bytes);
        if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return false;
        string[] lines = text[..^4].Split("\r\n", StringSplitOptions.None);
        string authority = "127.0.0.1:" + port;
        if (lines.Length is < 2 or > 4 || lines[0] != "CONNECT " + authority + " HTTP/1.1") return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool host = false;
        foreach (string line in lines.Skip(1))
        {
            int split = line.IndexOf(':'); if (split <= 0) return false;
            string name = line[..split], value = line[(split + 1)..];
            if (!names.Add(name) || !value.StartsWith(' ') || value.Length < 2 || value[1..].Trim() != value[1..]) return false;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) { if (value[1..] != authority) return false; host = true; }
            else if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) { if (value[1..] != "keep-alive") return false; }
            else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) { if (value.Length > 512) return false; }
            else return false; // No credentials, body framing, upgrade or connection modes.
        }
        return host;
    }
    private async Task TunnelAsync(Socket client)
    {
        Socket? upstream = null;
        try
        {
            await using var incoming = new NetworkStream(client, ownsSocket: false);
            byte[] header = new byte[4096]; int size = 0;
            using (var framing = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                // Read no byte past the CONNECT header, even if TLS arrives with it.
                while (size < header.Length)
                {
                    int count = await incoming.ReadAsync(header.AsMemory(size, 1), framing.Token);
                    if (count == 0) break; size++;
                    if (size >= 4 && header[size - 4] == 13 && header[size - 3] == 10 && header[size - 2] == 13 && header[size - 1] == 10) break;
                }
            }
            if (!Permits(header.AsSpan(0, size), appPort))
            {
                Interlocked.Increment(ref rejected);
                if (header.AsSpan(0, size).StartsWith(Encoding.ASCII.GetBytes("CONNECT " + ForbiddenAuthority + " HTTP/1.1\r\n")))
                    Interlocked.Increment(ref forbidden);
                using var refusal = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await incoming.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), refusal.Token);
                return;
            }
            Array.Clear(header);
            lock (sockets)
            {
                if (closing) { Interlocked.Increment(ref rejected); return; }
                upstream = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); sockets.Add(upstream);
            }
            using (var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await upstream.ConnectAsync(IPAddress.Loopback, appPort, connecting.Token);
            Interlocked.Increment(ref connected);
            await using var outgoing = new NetworkStream(upstream, ownsSocket: false);
            await incoming.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
            // Both ORIGINAL copy tasks are observed before awaiting either. No payload retention.
            Task upload = CopyAsync(incoming, outgoing, upstream), download = CopyAsync(outgoing, incoming, client);
            Observe(upload); Observe(download);
            try { await Task.WhenAll(upload, download); }
            // Either peer may reset an established opaque tunnel, as the host gate does when
            // it aborts a response. That ends the copy; it is not a proxy failure.
            catch (Exception reset) when (reset is IOException or SocketException or ObjectDisposedException) { }
            finally { client.Dispose(); upstream.Dispose(); }
        }
        catch { if (!closing) Interlocked.Increment(ref failed); }
        finally { client.Dispose(); upstream?.Dispose(); }
    }
    private static async Task CopyAsync(Stream from, Stream to, Socket destination)
    {
        try { await from.CopyToAsync(to, 16384); destination.Shutdown(SocketShutdown.Send); }
        catch { destination.Dispose(); throw; }
    }
    internal Task CloseAsync() => close ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        lock (sockets) closing = true;
        await intake.CancelAsync(); listener.Stop(); await accepting;
        // Called only AFTER original browser close. Socket close unblocks original copies;
        // it is not task settlement. All originals must still finish before dependencies go.
        Task[] tasks; lock (sockets) { foreach (Socket socket in sockets) socket.Dispose(); tasks = originals.ToArray(); }
        await Task.WhenAll(tasks); QualificationWire.Require(failed == 0); Settled = true; intake.Dispose();
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
