using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Npgsql;

namespace Braid.Tests;

/// <summary>
/// One test-owned plaintext protocol-v3 connection. Real COMMIT completion,
/// intervening parameter status and idle frames are withheld. Other bytes are forwarded, never logged.
/// The bounded caller separately proves durability before requesting socket closure.
/// </summary>
internal sealed class ServerSessionCommitAckLossRelay : IAsyncDisposable
{
    private const int MaximumFrameLength = 4 * 1024 * 1024;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
    private readonly TaskCompletionSource withheld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private readonly object disposal = new();
    private TcpClient? caller;
    private TcpClient? server;
    private Task? disposeTask;
    private int stopping;
    private int frontendCommit;
    private int backendCommit;
    private int backendIdle;
    private int parameterStatusFramesWithheld;
    private int requestedDropAccepted;
    private int closeFailed;

    internal ServerSessionCommitAckLossRelay(string actualConnectionString)
    {
        try
        {
            var actual = new NpgsqlConnectionStringBuilder(actualConnectionString);
            if (string.IsNullOrWhiteSpace(actual.Host) || actual.Host.Contains(',') ||
                actual.Host.StartsWith('/') || actual.Port is < 1 or > 65535)
                throw Failure();
            upstreamHost = actual.Host;
            upstreamPort = actual.Port;
            var routed = new NpgsqlConnectionStringBuilder(actual.ConnectionString)
            {
                Host = IPAddress.Loopback.ToString(),
                SslMode = SslMode.Disable,
                GssEncryptionMode = GssEncryptionMode.Disable
            };
            listener.Start(1);
            routed.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            RoutedConnectionString = routed.ConnectionString;
            Completion = RunAsync();
        }
        catch (Exception)
        {
            CloseOwnedEndpoints();
            lifetime.Dispose();
            throw Failure();
        }
    }

    // Private test input only; never included in an assertion operand or diagnostic.
    internal string RoutedConnectionString { get; }
    internal Task Completion { get; }
    internal Task CommitAcknowledgementWithheld => withheld.Task;
    internal bool FrontendCommitSeen => Volatile.Read(ref frontendCommit) == 1;
    internal bool BackendCommitCompleteSeen => Volatile.Read(ref backendCommit) == 1;
    internal bool BackendIdleReadySeen => Volatile.Read(ref backendIdle) == 1;
    internal int ParameterStatusFramesWithheld => Volatile.Read(ref parameterStatusFramesWithheld);
    internal bool RequestedDropAccepted => Volatile.Read(ref requestedDropAccepted) == 1;

    internal void DropAcknowledgement() => disconnect.TrySetResult();

    private async Task RunAsync()
    {
        Task? frontend = null;
        Task? backend = null;
        Exception? failure = null;
        try
        {
            caller = await listener.AcceptTcpClientAsync(lifetime.Token);
            listener.Stop();
            lifetime.Token.ThrowIfCancellationRequested();
            caller.NoDelay = true;
            server = new TcpClient { NoDelay = true };
            await server.ConnectAsync(upstreamHost, upstreamPort, lifetime.Token);
            frontend = ForwardFrontendAsync(caller.GetStream(), server.GetStream(), lifetime.Token);
            backend = ForwardBackendAsync(server.GetStream(), caller.GetStream(), lifetime.Token);
            var finished = await Task.WhenAny(frontend, backend);
            await finished;
            if (!RequestedDropAccepted || !BackendIdleReadySeen)
                throw Failure();
        }
        catch (Exception)
        {
            if (Volatile.Read(ref stopping) == 0)
                failure = Failure();
        }
        finally
        {
            CloseOwnedEndpoints();
            // Every operation in either pump receives the canceled token and
            // its exact sockets are closed before both tasks are actually awaited.
            // A timed WaitAsync alone must not be mistaken for pump settlement.
            if (frontend is not null) await ObservePumpAsync(frontend);
            if (backend is not null) await ObservePumpAsync(backend);
            if (Volatile.Read(ref closeFailed) != 0) failure ??= Failure();
            if (!withheld.Task.IsCompleted)
            {
                if (failure is not null) withheld.TrySetException(failure);
                else withheld.TrySetCanceled();
            }
        }
        if (failure is not null) throw failure;
    }

    private async Task ForwardFrontendAsync(Stream source, Stream destination, CancellationToken token)
    {
        var startup = new byte[8];
        var header = new byte[5];
        var tag = new byte[7];
        var buffer = new byte[8192];
        try
        {
            await source.ReadExactlyAsync(startup, token);
            var length = BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(0, 4));
            if (length is < 8 or > 65536 || BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(4, 4)) != 196608)
                throw Failure();
            await destination.WriteAsync(startup, token);
            await CopyAsync(source, destination, length - 8, buffer, token);
            CryptographicOperations.ZeroMemory(startup);
            while (true)
            {
                await source.ReadExactlyAsync(header, token);
                length = FrameLength(header);
                if (header[0] == (byte)'Q' && length == 11)
                {
                    await source.ReadExactlyAsync(tag, token);
                    // Pinned Npgsql10.0.3 CommitAsync uses this pregenerated Q
                    // frame. No application SQL or parameter history is parsed.
                    if (tag.AsSpan().SequenceEqual("COMMIT\0"u8) &&
                        Interlocked.CompareExchange(ref frontendCommit, 1, 0) != 0)
                        throw Failure();
                    await destination.WriteAsync(header, token);
                    await destination.WriteAsync(tag, token);
                    CryptographicOperations.ZeroMemory(tag);
                }
                else
                {
                    await destination.WriteAsync(header, token);
                    await CopyAsync(source, destination, length - 4, buffer, token);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(startup);
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private async Task ForwardBackendAsync(Stream source, Stream destination, CancellationToken token)
    {
        var header = new byte[5];
        var tag = new byte[7];
        var idle = new byte[1];
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                await source.ReadExactlyAsync(header, token);
                var length = FrameLength(header);
                if (header[0] == (byte)'C' && length == 11)
                {
                    await source.ReadExactlyAsync(tag, token);
                    if (tag.AsSpan().SequenceEqual("COMMIT\0"u8))
                    {
                        if (!FrontendCommitSeen || Interlocked.CompareExchange(ref backendCommit, 1, 0) != 0)
                            throw Failure();
                        CryptographicOperations.ZeroMemory(tag);
                        // Neither the actual CommandComplete nor its idle-ready
                        // acknowledgement is delivered to the original Npgsql.
                        var statusFrames = 0;
                        var statusBytes = 0;
                        while (true)
                        {
                            await source.ReadExactlyAsync(header, token);
                            var nextLength = FrameLength(header);
                            if (header[0] == (byte)'S')
                            {
                                // SET LOCAL restoration can report ParameterStatus before idle:
                                // https://www.postgresql.org/docs/16/protocol-flow.html#PROTOCOL-ASYNC
                                // Withhold bounded status frames without retaining their values.
                                if (++statusFrames > 16 || nextLength < 6 || nextLength > 65536 - statusBytes)
                                    throw Failure();
                                statusBytes += nextLength;
                                await CopyAsync(source, Stream.Null, nextLength - 4, buffer, token);
                                Volatile.Write(ref parameterStatusFramesWithheld, statusFrames);
                                continue;
                            }
                            if (header[0] != (byte)'Z' || nextLength != 5) throw Failure();
                            break;
                        }
                        await source.ReadExactlyAsync(idle, token);
                        if (idle[0] != (byte)'I') throw Failure();
                        CryptographicOperations.ZeroMemory(idle);
                        Volatile.Write(ref backendIdle, 1);
                        withheld.TrySetResult();
                        await disconnect.Task.WaitAsync(token);
                        token.ThrowIfCancellationRequested();
                        Volatile.Write(ref requestedDropAccepted, 1);
                        return;
                    }
                    await destination.WriteAsync(header, token);
                    await destination.WriteAsync(tag, token);
                    CryptographicOperations.ZeroMemory(tag);
                }
                else
                {
                    await destination.WriteAsync(header, token);
                    await CopyAsync(source, destination, length - 4, buffer, token);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(idle);
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static int FrameLength(byte[] header)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        return length is >= 4 and <= MaximumFrameLength ? length : throw Failure();
    }

    private static async Task CopyAsync(Stream source, Stream destination, int remaining, byte[] buffer,
        CancellationToken token)
    {
        while (remaining > 0)
        {
            var length = Math.Min(remaining, buffer.Length);
            try
            {
                await source.ReadExactlyAsync(buffer.AsMemory(0, length), token);
                await destination.WriteAsync(buffer.AsMemory(0, length), token);
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            remaining -= length;
        }
    }

    private void CloseOwnedEndpoints()
    {
        Attempt(lifetime.Cancel);
        Attempt(listener.Stop);
        Attempt(() => caller?.Dispose());
        Attempt(() => server?.Dispose());
        void Attempt(Action close)
        {
            try { close(); }
            catch (Exception) { Volatile.Write(ref closeFailed, 1); }
        }
    }

    private static async Task ObservePumpAsync(Task pump)
    {
        try { await pump; }
        catch (Exception) { /* The first pump outcome is classified before owned endpoint closure. */ }
    }

    private static InvalidOperationException Failure() => new("The owned session acknowledgement relay failed.");

    public ValueTask DisposeAsync()
    {
        lock (disposal) return new(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        disconnect.TrySetResult();
        CloseOwnedEndpoints();
        try { await Completion; }
        finally
        {
            try { await withheld.Task; }
            catch (Exception) { /* Observe even an unused gate on a failed setup path. */ }
            lifetime.Dispose();
        }
    }
}
