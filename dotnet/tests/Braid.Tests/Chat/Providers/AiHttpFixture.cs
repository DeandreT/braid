using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Braid.Tests;

/// <summary>Owned real HTTP/1.1 TCP peer. Private wire values never enter diagnostic strings.</summary>
internal sealed class AiHttpFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> clients = new();
    private readonly ConcurrentQueue<Task> jobs = new();
    private readonly ConcurrentQueue<Request> requests = new();
    private readonly Func<Exchange, CancellationToken, Task> script;
    private readonly Task accept;
    private readonly object pulseLock = new();
    private TaskCompletionSource pulse = NewSignal();
    private int connections;
    private int completed;
    private int unexpected;
    private bool disposed;
    internal AiHttpFixture(Func<Exchange, CancellationToken, Task> script)
    {
        this.script = script;
        listener.Start(16);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Origin = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
        accept = AcceptAsync();
    }
    internal Uri Origin { get; }
    internal int ConnectionCount => Volatile.Read(ref connections);
    internal int RequestCount => requests.Count;
    internal int CompletedConnections => Volatile.Read(ref completed);
    internal bool HasUnexpectedServerFailure => Volatile.Read(ref unexpected) != 0;
    internal Request[] Requests => requests.ToArray();
    internal Uri Endpoint(string path) => new(Origin, path);
    internal static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
    internal static JsonDocument Json(string value)
    {
        try { return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch { throw new InvalidOperationException("Private fixture JSON was invalid."); }
    }
    internal static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Changed()
    {
        lock (pulseLock) { var old = pulse; pulse = NewSignal(); old.TrySetResult(); }
    }
    internal async Task WaitForRequestsAsync(int count, CancellationToken ct = default)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        while (RequestCount < count)
        {
            Task next;
            lock (pulseLock) { if (RequestCount >= count) return; next = pulse.Task; }
            await next.WaitAsync(bound.Token);
        }
    }
    internal async Task WaitForCompletedAsync(int count, CancellationToken ct = default)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        while (CompletedConnections < count)
        {
            Task next;
            lock (pulseLock) { if (CompletedConnections >= count) return; next = pulse.Task; }
            await next.WaitAsync(bound.Token);
        }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                client.NoDelay = true;
                var ordinal = Interlocked.Increment(ref connections);
                if (ordinal > 64) { client.Dispose(); throw new InvalidOperationException("Fixture connection bound exceeded."); }
                clients.TryAdd(ordinal, client);
                jobs.Enqueue(ServeAsync(ordinal, client));
                Changed();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        catch { Interlocked.Exchange(ref unexpected, 1); }
    }
    private async Task ServeAsync(int ordinal, TcpClient client)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            var request = await Request.ReadAsync(stream, timeout.Token);
            requests.Enqueue(request); Changed();
            await script(new Exchange(client, stream, request, ordinal), timeout.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        catch { Interlocked.Exchange(ref unexpected, 1); }
        finally
        {
            clients.TryRemove(ordinal, out _); client.Dispose();
            Interlocked.Increment(ref completed); Changed();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel(); listener.Stop();
        foreach (var client in clients.Values) client.Dispose();
        try { await accept.WaitAsync(TimeSpan.FromSeconds(5)); await Task.WhenAll(jobs.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { throw new InvalidOperationException("Owned HTTP fixture did not settle."); }
        finally { stop.Dispose(); }
        if (!clients.IsEmpty) throw new InvalidOperationException("Owned HTTP fixture retained a connection.");
        if (HasUnexpectedServerFailure) throw new InvalidOperationException("Owned HTTP fixture had an unexpected server failure.");
    }
    public override string ToString() => "Owned AI HTTP fixture.";

    internal sealed class Request
    {
        private Request(string method, string target, string version, Dictionary<string, string> headers, byte[] body)
        { Method = method; Target = target; Version = version; Headers = headers; Body = body; }
        private string Method { get; }
        private string Target { get; }
        private string Version { get; }
        private Dictionary<string, string> Headers { get; }
        private byte[] Body { get; }
        internal bool IsPostAt(string path) => Method == "POST" && Target == path && Version == "HTTP/1.1";
        internal bool IsGetAt(string target) => Method == "GET" && Target == target && Version == "HTTP/1.1" && Body.Length == 0;
        internal bool HeaderIs(string name, string value) => Headers.TryGetValue(name, out var actual) && string.Equals(actual, value, StringComparison.Ordinal);
        internal bool HasHeader(string name) => Headers.ContainsKey(name);
        internal JsonDocument Json()
        {
            try { return JsonDocument.Parse(Body, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch { throw new InvalidOperationException("Fixture request was not bounded JSON."); }
        }
        internal bool ContainsUtf8(string value) => Body.AsSpan().IndexOf(Utf8(value)) >= 0;
        public override string ToString() => "Private captured AI request.";
        internal static async Task<Request> ReadAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384)
            {
                if (await stream.ReadAsync(one, ct) != 1) throw new IOException("Incomplete fixture HTTP headers.");
                header.Add(one[0]);
                var n = header.Count;
                if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
            }
            if (header.Count >= 16384) throw new IOException("Fixture HTTP header bound exceeded.");
            if (header.Any(value => value > 127)) throw new IOException("Invalid fixture HTTP headers.");
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
            var start = lines[0].Split(' ');
            if (start.Length != 3) throw new IOException("Invalid fixture HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(value => value.Length != 0))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new IOException("Invalid fixture HTTP header field.");
            }
            if (headers.TryGetValue("Expect", out var expect) && expect == "100-continue")
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), ct);
            byte[] body;
            if (headers.ContainsKey("Content-Length") && headers.ContainsKey("Transfer-Encoding")) throw new IOException("Conflicting fixture request framing.");
            if (headers.TryGetValue("Content-Length", out var text))
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size is < 0 or > 262144)
                    throw new IOException("Fixture body bound exceeded.");
                body = new byte[size]; await stream.ReadExactlyAsync(body, ct);
            }
            else if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding == "chunked")
            {
                using var buffer = new MemoryStream();
                while (true)
                {
                    var sizeLine = await ReadLineAsync(stream, ct);
                    if (!int.TryParse(sizeLine, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0 || size > 262144 - buffer.Length)
                        throw new IOException("Fixture chunk bound exceeded.");
                    if (size == 0) { if ((await ReadLineAsync(stream, ct)).Length != 0) throw new IOException("Unexpected fixture trailers."); break; }
                    var chunk = new byte[size]; await stream.ReadExactlyAsync(chunk, ct); buffer.Write(chunk);
                    if ((await ReadLineAsync(stream, ct)).Length != 0) throw new IOException("Invalid fixture chunk framing.");
                }
                body = buffer.ToArray();
            }
            else if (start[0] == "GET") body = [];
            else throw new IOException("Missing fixture request framing.");
            return new(start[0], start[1], start[2], headers, body);
        }
        private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>(); var one = new byte[1];
            while (bytes.Count < 128)
            {
                if (await stream.ReadAsync(one, ct) != 1) throw new IOException("Incomplete fixture framing.");
                if (one[0] == 10)
                {
                    if (bytes.Count == 0 || bytes[^1] != 13) throw new IOException("Invalid fixture framing.");
                    bytes.RemoveAt(bytes.Count - 1); return Encoding.ASCII.GetString(bytes.ToArray());
                }
                bytes.Add(one[0]);
            }
            throw new IOException("Fixture framing bound exceeded.");
        }
    }
    internal sealed class Exchange(TcpClient client, NetworkStream stream, Request request, int ordinal)
    {
        internal Request Request { get; } = request;
        internal int Ordinal { get; } = ordinal;
        internal Task HeadersAsync(string contentType, CancellationToken ct, int status = 200, string extra = "", int? length = null)
        {
            var reason = status == 200 ? "OK" : "Fixture";
            var headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\nContent-Type: " + contentType +
                "\r\nConnection: close\r\n" + (length is { } n ? "Content-Length: " + n.ToString(CultureInfo.InvariantCulture) + "\r\n" : "") + extra + "\r\n";
            return stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).AsTask();
        }
        internal async Task RespondAsync(string contentType, byte[] body, CancellationToken ct, bool fragmented = false, int status = 200, string extra = "")
        {
            // Early client closure is valid in refusal/limit/cancellation cases. Client-side
            // event/status oracles still determine whether the response was accepted correctly.
            try { await HeadersAsync(contentType, ct, status, extra, body.Length); await WriteAsync(body, ct, fragmented); }
            catch (IOException) { }
        }
        internal async Task WriteAsync(byte[] bytes, CancellationToken ct, bool fragmented = false)
        {
            if (!fragmented) { await stream.WriteAsync(bytes, ct); return; }
            var offset = 0; var width = 1;
            while (offset < bytes.Length)
            {
                var count = Math.Min(width, bytes.Length - offset);
                await stream.WriteAsync(bytes.AsMemory(offset, count), ct);
                offset += count; width = width == 5 ? 1 : width + 1;
            }
        }
        internal void CloseWithoutResponse() => client.Client.Shutdown(SocketShutdown.Both);
        internal async Task ObserveClientCloseAsync(CancellationToken ct)
        {
            var one = new byte[1];
            try { if (await stream.ReadAsync(one, ct) != 0) throw new InvalidOperationException("Unexpected bytes followed the complete fixture request."); }
            catch (IOException) { }
            catch (SocketException) { }
        }
        public override string ToString() => "Owned HTTP exchange.";
    }
}
