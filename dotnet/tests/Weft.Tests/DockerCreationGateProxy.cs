using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Weft.Tests;

/// <summary>
/// Test-owned HTTP/1 Unix-socket proxy. It delays publication of one fully captured
/// create control request; all daemon operations and the worker remain real.
/// Native attach streams are forwarded without buffering, parsing, or logging.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class DockerCreationGateProxy : IAsyncDisposable
{
    private const int MaximumControlBytes = 64 * 1024;
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<Socket, byte> sockets = new();
    private readonly ConcurrentBag<Task> connections = new();
    private readonly Task accepting;
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int held;
    private int disposed;
    private ConnectionState? gatedConnection;
    internal string DirectoryPath { get; }
    internal string SocketPath { get; }
    internal TaskCompletionSource<string> CapturedCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<PublishedCreation> PublishedCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CreateForwardCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DockerCreationGateProxy()
    {
        DirectoryPath = Directory.CreateTempSubdirectory("weft-create-gate-").FullName;
        File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SocketPath = Path.Combine(DirectoryPath, "daemon.sock");
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            listener.Listen(16);
            accepting = AcceptAsync();
        }
        catch
        {
            listener.Dispose();
            File.Delete(SocketPath);
            Directory.Delete(DirectoryPath);
            shutdown.Dispose();
            throw;
        }
    }

    internal void ReleaseCreate() => release.TrySetResult();

    internal void AbortGatedForward()
    {
        var connection = Volatile.Read(ref gatedConnection);
        connection?.Client?.Dispose();
        connection?.Daemon?.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptAsync(shutdown.Token);
                sockets.TryAdd(client, 0);
                connections.Add(ObserveForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
        catch (SocketException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { Failure.TrySetResult(error); }
    }

    private async Task ObserveForwardAsync(Socket client)
    {
        var state = new ConnectionState();
        try { await ForwardAsync(client, state); }
        finally
        {
            // The underlying forwarding task is terminal and its sockets are closed.
            // A parser failure after an actual 201 must not bypass owned cleanup.
            if (state.OwnsGatedCreate) CreateForwardCompleted.TrySetResult();
        }
    }

    private async Task ForwardAsync(Socket client, ConnectionState state)
    {
        using var daemon = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        state.Client = client;
        state.Daemon = daemon;
        sockets.TryAdd(daemon, 0);
        try
        {
            await daemon.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), shutdown.Token);
            using var incoming = new NetworkStream(client, ownsSocket: false);
            using var upstream = new NetworkStream(daemon, ownsSocket: false);
            var requests = Channel.CreateUnbounded<Request>();
            var sending = SendRequestsAsync(incoming, upstream, requests.Writer,
                () => daemon.Shutdown(SocketShutdown.Send), state);
            var receiving = SendResponsesAsync(upstream, incoming, requests.Reader);
            var first = await Task.WhenAny(sending, receiving);
            if (first.IsFaulted || first.IsCanceled)
            {
                client.Dispose();
                daemon.Dispose();
            }
            await Task.WhenAll(sending, receiving);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
        catch (SocketException) when (shutdown.IsCancellationRequested) { }
        catch (IOException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { Failure.TrySetResult(error); }
        finally
        {
            sockets.TryRemove(client, out _);
            sockets.TryRemove(daemon, out _);
            client.Dispose();
            daemon.Dispose();
        }
    }

    private async Task SendRequestsAsync(Stream client, Stream daemon, ChannelWriter<Request> requests,
        Action closeDaemonWrite, ConnectionState state)
    {
        try
        {
            while (await ReadHeaderAsync(client, shutdown.Token) is { } header)
            {
                var message = Message.Parse(header);
                var first = message.FirstLine.Split(' ', 3);
                if (first.Length != 3) throw new InvalidOperationException("The test proxy received an invalid control request.");
                var create = first[0] == "POST" && first[1].Split('?', 2)[0].EndsWith("/containers/create", StringComparison.Ordinal);
                string? name = null;
                byte[]? captured = null;
                var gated = create && Interlocked.CompareExchange(ref held, 1, 0) == 0;
                if (gated)
                {
                    state.OwnsGatedCreate = true;
                    Volatile.Write(ref gatedConnection, state);
                    name = CreateName(first[1]);
                    using var body = new MemoryStream();
                    await TransferBodyAsync(client, message, ReadBodyBytes, shutdown.Token);
                    captured = body.ToArray();
                    CapturedCreate.TrySetResult(name);
                    // Publication is independent of CLI disconnect/caller cancellation. Failure
                    // cleanup must release and await this request before removing its exact name.
                    await release.Task.WaitAsync(shutdown.Token);
                    ValueTask ReadBodyBytes(ReadOnlyMemory<byte> bytes)
                    {
                        if (body.Length + bytes.Length > MaximumControlBytes)
                            throw new InvalidOperationException("The test create control request exceeded its capture bound.");
                        return body.WriteAsync(bytes, shutdown.Token);
                    }
                }
                var upgrade = message.Headers.ContainsKey("Upgrade");
                await requests.WriteAsync(new(first[0], gated ? name : null), shutdown.Token);
                await daemon.WriteAsync(header, shutdown.Token);
                if (captured is not null)
                    await daemon.WriteAsync(captured, shutdown.Token);
                else
                    await TransferBodyAsync(client, message, bytes => daemon.WriteAsync(bytes, shutdown.Token), shutdown.Token);
                await daemon.FlushAsync(shutdown.Token);
                if (upgrade)
                {
                    await client.CopyToAsync(daemon, shutdown.Token);
                    // Docker attach closes only its write half at stdin EOF. Forward that
                    // EOF without dropping the daemon's remaining worker output.
                    closeDaemonWrite();
                    return;
                }
            }
        }
        finally { requests.TryComplete(); }
    }

    private async Task SendResponsesAsync(Stream daemon, Stream client, ChannelReader<Request> requests)
    {
        await foreach (var request in requests.ReadAllAsync(shutdown.Token))
        {
            var header = await ReadHeaderAsync(daemon, shutdown.Token) ??
                throw new IOException("The daemon ended a test control response before its header.");
            var response = Message.Parse(header);
            var first = response.FirstLine.Split(' ', 3);
            if (first.Length < 2 || !int.TryParse(first[1], CultureInfo.InvariantCulture, out var status))
                throw new InvalidOperationException("The test proxy received an invalid daemon status.");
            var writable = true;
            await ForwardBytes(header);
            if (status == 101)
            {
                await daemon.CopyToAsync(client, shutdown.Token);
                return;
            }
            using var creationBody = request.GatedName is null ? null : new MemoryStream();
            if (request.Method != "HEAD" && status is not (204 or 304))
            {
                await TransferBodyAsync(daemon, response, ForwardBytes, shutdown.Token, decodedBody: creationBody);
            }
            if (request.GatedName is { } name)
            {
                if (status != 201 || creationBody is null)
                    throw new InvalidOperationException("The actual daemon did not publish the gated creation.");
                using var document = JsonDocument.Parse(creationBody.ToArray());
                var id = document.RootElement.GetProperty("Id").GetString();
                if (id is not { Length: 64 } || id.Any(character => !char.IsAsciiHexDigit(character)))
                    throw new InvalidOperationException("The actual daemon did not provide a complete container ID.");
                PublishedCreate.TrySetResult(new(name, id));
            }
            async ValueTask ForwardBytes(ReadOnlyMemory<byte> bytes)
            {
                if (!writable) return;
                try { await client.WriteAsync(bytes, shutdown.Token); }
                catch (IOException) when (request.GatedName is not null) { writable = false; }
                catch (SocketException) when (request.GatedName is not null) { writable = false; }
            }
        }
    }

    private static string CreateName(string path)
    {
        var uri = new Uri("http://docker" + path);
        var name = uri.Query.TrimStart('?').Split('&').Single(value => value.StartsWith("name=", StringComparison.Ordinal));
        name = Uri.UnescapeDataString(name["name=".Length..]);
        if (name.Length != 45 || !name.StartsWith("weft-inspect-", StringComparison.Ordinal) ||
            name["weft-inspect-".Length..].Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new InvalidOperationException("The test proxy refused an unexpected worker name.");
        return name;
    }

    private static async Task<byte[]?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var one = new byte[1];
        var tail = 0;
        while (await stream.ReadAsync(one, cancellationToken) != 0)
        {
            header.WriteByte(one[0]);
            tail = (tail << 8) | one[0];
            if (header.Length > MaximumControlBytes)
                throw new InvalidOperationException("A test control header exceeded its bound.");
            if (tail == 0x0d0a0d0a) return header.ToArray();
        }
        if (header.Length == 0) return null;
        throw new IOException("A test control header ended prematurely.");
    }

    private static async Task TransferBodyAsync(Stream source, Message message,
        Func<ReadOnlyMemory<byte>, ValueTask> destination, CancellationToken cancellationToken,
        MemoryStream? decodedBody = null)
    {
        if (message.Headers.TryGetValue("Transfer-Encoding", out var encoding))
        {
            if (!string.Equals(encoding, "chunked", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test proxy does not accept another control transfer coding.");
            while (true)
            {
                var line = await ReadLineAsync(source, cancellationToken);
                await destination(line);
                var text = Encoding.ASCII.GetString(line).TrimEnd('\r', '\n').Split(';', 2)[0];
                if (!long.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var count) || count < 0)
                    throw new InvalidOperationException("The test daemon control chunk size was invalid.");
                if (count == 0)
                {
                    byte[] trailer;
                    do { trailer = await ReadLineAsync(source, cancellationToken); await destination(trailer); }
                    while (trailer.Length != 2);
                    return;
                }
                await TransferExactAsync(source, count, destination, cancellationToken, decodedBody);
                var ending = new byte[2];
                await source.ReadExactlyAsync(ending, cancellationToken);
                if (ending[0] != '\r' || ending[1] != '\n')
                    throw new InvalidOperationException("The daemon control chunk did not terminate correctly.");
                await destination(ending);
            }
        }
        else if (message.Headers.TryGetValue("Content-Length", out var length))
        {
            if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
                throw new InvalidOperationException("The test control length was invalid.");
            await TransferExactAsync(source, count, destination, cancellationToken, decodedBody);
        }
    }

    private static async Task TransferExactAsync(Stream source, long count,
        Func<ReadOnlyMemory<byte>, ValueTask> destination, CancellationToken cancellationToken,
        MemoryStream? decodedBody = null)
    {
        var buffer = new byte[8192];
        while (count > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken);
            if (read == 0) throw new IOException("The test control body ended prematurely.");
            if (decodedBody is not null)
            {
                if (decodedBody.Length + read > MaximumControlBytes)
                    throw new InvalidOperationException("The decoded create response exceeded its capture bound.");
                await decodedBody.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination(buffer.AsMemory(0, read));
            count -= read;
        }
    }

    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        var previous = -1;
        while (await stream.ReadAsync(one, cancellationToken) != 0)
        {
            line.WriteByte(one[0]);
            if (line.Length > MaximumControlBytes) throw new InvalidOperationException("A test control line exceeded its bound.");
            if (previous == '\r' && one[0] == '\n') return line.ToArray();
            previous = one[0];
        }
        throw new IOException("A test control line ended prematurely.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ReleaseCreate();
        shutdown.Cancel();
        listener.Dispose();
        foreach (var socket in sockets.Keys) socket.Dispose();
        try
        {
            await accepting.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            shutdown.Dispose();
            File.Delete(SocketPath);
            Directory.Delete(DirectoryPath);
        }
    }

    internal sealed record PublishedCreation(string Name, string ContainerId);
    private sealed class ConnectionState
    {
        internal bool OwnsGatedCreate;
        internal Socket? Client;
        internal Socket? Daemon;
    }
    private sealed record Request(string Method, string? GatedName);
    private sealed record Message(string FirstLine, IReadOnlyDictionary<string, string> Headers)
    {
        internal static Message Parse(byte[] raw)
        {
            var lines = Encoding.ASCII.GetString(raw).Split("\r\n", StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(line => line.Length != 0))
            {
                var separator = line.IndexOf(':');
                if (separator < 1 || !headers.TryAdd(line[..separator], line[(separator + 1)..].Trim(' ', '\t')))
                    throw new InvalidOperationException("The test control headers were invalid or duplicated.");
            }
            return new(lines[0], headers);
        }
    }
}
