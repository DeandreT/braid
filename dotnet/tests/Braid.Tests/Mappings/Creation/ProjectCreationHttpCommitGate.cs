using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Npgsql;

namespace Braid.Tests;

/// <summary>
/// Private single-connection protocol relay. It holds one real frontend COMMIT
/// before sending it, keeping the actual HTTP creator transaction uncommitted.
/// Every other byte is forwarded; no application SQL, receipt or credential is
/// fabricated, logged or retained. This does not simulate an ambiguous commit.
/// </summary>
internal sealed class ProjectCreationHttpCommitGate : IAsyncDisposable
{
    private const int MaximumFrameLength = 4 * 1024 * 1024;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
    private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private TcpClient? caller;
    private TcpClient? server;
    private int armed;
    private int consumed;
    private int forwarded;
    private int disposed;

    internal ProjectCreationHttpCommitGate(string actualConnectionString)
    {
        var actual = new NpgsqlConnectionStringBuilder(actualConnectionString);
        upstreamHost = actual.Host ?? throw new ArgumentException("An owned upstream host is required.", nameof(actualConnectionString));
        upstreamPort = actual.Port;
        listener.Start(1);
        ConnectionString = new NpgsqlConnectionStringBuilder(actualConnectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString;
        Completion = RunAsync();
    }

    internal string ConnectionString { get; }
    internal Task Completion { get; }
    internal Task CommitHeld => held.Task;
    internal bool ActualCommitForwarded => Volatile.Read(ref forwarded) == 1;

    internal void ArmNextCommit()
    {
        if (Interlocked.CompareExchange(ref armed, 1, 0) != 0)
            throw new InvalidOperationException("The owned commit gate can be armed only once.");
    }

    internal void ReleaseCommit() => release.TrySetResult();

    private async Task RunAsync()
    {
        Task? frontend = null;
        Task? backend = null;
        try
        {
            caller = await listener.AcceptTcpClientAsync(lifetime.Token);
            listener.Stop();
            caller.NoDelay = true;
            server = new TcpClient { NoDelay = true };
            await server.ConnectAsync(upstreamHost, upstreamPort, lifetime.Token);
            frontend = ForwardFrontendAsync(caller.GetStream(), server.GetStream(), lifetime.Token);
            backend = ForwardBytesAsync(server.GetStream(), caller.GetStream(), lifetime.Token);
            var finished = await Task.WhenAny(frontend, backend);
            await finished;
        }
        catch (Exception) when (Volatile.Read(ref disposed) == 1) { }
        catch (Exception)
        {
            var error = new InvalidOperationException("The owned PostgreSQL commit gate failed.");
            held.TrySetException(error);
            throw error;
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            caller?.Dispose();
            server?.Dispose();
            if (frontend is not null) await DrainAsync(frontend);
            if (backend is not null) await DrainAsync(backend);
        }
    }

    private async Task ForwardFrontendAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var startup = new byte[8];
        var header = new byte[5];
        var candidate = new byte[7];
        var buffer = new byte[8192];
        try
        {
            await source.ReadExactlyAsync(startup, cancellationToken);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(0, 4));
            if (startupLength is < 8 or > MaximumFrameLength || BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(4, 4)) != 196608)
                throw new InvalidOperationException("The owned relay requires the explicit plaintext PostgreSQL startup protocol.");
            await destination.WriteAsync(startup, cancellationToken);
            await CopyAsync(source, destination, startupLength - 8, buffer, cancellationToken);
            CryptographicOperations.ZeroMemory(startup);
            while (true)
            {
                await source.ReadExactlyAsync(header, cancellationToken);
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
                if (length is < 4 or > MaximumFrameLength)
                    throw new InvalidOperationException("The owned PostgreSQL frame bound was exceeded.");
                if (header[0] == (byte)'Q' && length == 11)
                {
                    await source.ReadExactlyAsync(candidate, cancellationToken);
                    // The unchanged qualified Npgsql pin emits this pregenerated
                    // COMMIT simple-query frame. Match only these seven control
                    // bytes; do not inspect or persist application SQL frames.
                    var selected = Volatile.Read(ref armed) == 1 && candidate.AsSpan().SequenceEqual("COMMIT\0"u8)
                        && Interlocked.CompareExchange(ref consumed, 1, 0) == 0;
                    if (selected)
                    {
                        held.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                    await destination.WriteAsync(header, cancellationToken);
                    await destination.WriteAsync(candidate, cancellationToken);
                    if (selected) Volatile.Write(ref forwarded, 1);
                    CryptographicOperations.ZeroMemory(candidate);
                }
                else
                {
                    await destination.WriteAsync(header, cancellationToken);
                    await CopyAsync(source, destination, length - 4, buffer, cancellationToken);
                }
                if (header[0] == (byte)'X') return;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(startup);
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, int remaining, byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            try
            {
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            remaining -= count;
        }
    }

    private static async Task ForwardBytesAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0) return;
                try { await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.TrySetResult();
        lifetime.Cancel();
        listener.Stop();
        caller?.Dispose();
        server?.Dispose();
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { lifetime.Dispose(); }
    }
}
