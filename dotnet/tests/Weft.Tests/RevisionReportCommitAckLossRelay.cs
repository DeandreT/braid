using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Npgsql;

namespace Weft.Tests;

/// <summary>
/// A single owned plaintext PostgreSQL connection. Every ordinary frame is
/// forwarded unchanged; only actual COMMIT completion/idle acknowledgements
/// are withheld, and the caller is closed after independent durability proof.
/// No authentication, query, parameter or result payload is retained/logged.
/// </summary>
internal sealed class RevisionReportCommitAckLossRelay : IAsyncDisposable
{
    private const int MaximumFrameLength = 4 * 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(90));
    private readonly TaskCompletionSource _acknowledgementWithheld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private TcpClient? _caller;
    private TcpClient? _server;
    private int _commitSeen;
    private int _commitCompleteSeen;
    private int _idleReadySeen;
    private int _disposed;

    internal RevisionReportCommitAckLossRelay(string upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _listener.Start(1);
        Completion = RunAsync();
    }

    internal Task Completion { get; }
    internal Task CommitAcknowledgementWithheld => _acknowledgementWithheld.Task;
    internal bool FrontendCommitSeen => Volatile.Read(ref _commitSeen) == 1;
    internal bool BackendCommitCompleteSeen => Volatile.Read(ref _commitCompleteSeen) == 1;
    internal bool BackendIdleReadySeen => Volatile.Read(ref _idleReadySeen) == 1;

    internal string Route(string actualConnectionString)
    {
        var routed = new NpgsqlConnectionStringBuilder(actualConnectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable
        };
        return routed.ConnectionString;
    }

    internal void DisconnectCaller() => _disconnect.TrySetResult();

    private async Task RunAsync()
    {
        Task? frontend = null;
        Task? backend = null;
        try
        {
            _caller = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            _listener.Stop();
            _caller.NoDelay = true;
            _server = new TcpClient { NoDelay = true };
            await _server.ConnectAsync(_upstreamHost, _upstreamPort, _lifetime.Token);
            frontend = ForwardFrontendAsync(_caller.GetStream(), _server.GetStream(), _lifetime.Token);
            backend = ForwardBackendAsync(_server.GetStream(), _caller.GetStream(), _lifetime.Token);
            var finished = await Task.WhenAny(frontend, backend);
            await finished;
            if (!_disconnect.Task.IsCompleted || !BackendIdleReadySeen)
                throw RelayFailure();
        }
        catch (Exception) when (_disposed == 1)
        {
            _acknowledgementWithheld.TrySetCanceled();
        }
        catch (Exception)
        {
            var failure = RelayFailure();
            _acknowledgementWithheld.TrySetException(failure);
            throw failure;
        }
        finally
        {
            _listener.Stop();
            _lifetime.Cancel();
            _caller?.Dispose();
            _server?.Dispose();
            // Both pumps are canceled and sockets are closed before draining.
            if (frontend is not null) await DrainAsync(frontend);
            if (backend is not null) await DrainAsync(backend);
        }
    }

    private async Task ForwardFrontendAsync(Stream caller, Stream server, CancellationToken cancellationToken)
    {
        var startup = new byte[8];
        var header = new byte[5];
        var smallPayload = new byte[7];
        var copy = new byte[8192];
        try
        {
            await caller.ReadExactlyAsync(startup, cancellationToken);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(0, 4));
            if (startupLength is < 8 or > MaximumFrameLength ||
                BinaryPrimitives.ReadInt32BigEndian(startup.AsSpan(4, 4)) != 196608)
                throw RelayFailure();
            await server.WriteAsync(startup, cancellationToken);
            await CopyPayloadAsync(caller, server, startupLength - 8, copy, cancellationToken);
            CryptographicOperations.ZeroMemory(startup);
            while (true)
            {
                await caller.ReadExactlyAsync(header, cancellationToken);
                var length = FrameLength(header);
                if (header[0] == (byte)'Q' && length == 11)
                {
                    await caller.ReadExactlyAsync(smallPayload, cancellationToken);
                    // Pinned Npgsql 10.0.3 transaction CommitAsync sends this
                    // exact pregenerated simple-query frame, without parsing
                    // application SQL or saving any frontend payload history:
                    // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlTransaction.cs#L123
                    // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/PregeneratedMessages.cs#L18
                    if (smallPayload.AsSpan().SequenceEqual("COMMIT\0"u8))
                    {
                        if (Interlocked.CompareExchange(ref _commitSeen, 1, 0) != 0)
                            throw RelayFailure();
                    }
                    await server.WriteAsync(header, cancellationToken);
                    await server.WriteAsync(smallPayload, cancellationToken);
                    CryptographicOperations.ZeroMemory(smallPayload);
                }
                else
                {
                    await server.WriteAsync(header, cancellationToken);
                    await CopyPayloadAsync(caller, server, length - 4, copy, cancellationToken);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(startup);
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(smallPayload);
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private async Task ForwardBackendAsync(Stream server, Stream caller, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        var commitTag = new byte[7];
        var status = new byte[1];
        var copy = new byte[8192];
        try
        {
            while (true)
            {
                await server.ReadExactlyAsync(header, cancellationToken);
                var length = FrameLength(header);
                if (!FrontendCommitSeen)
                {
                    await caller.WriteAsync(header, cancellationToken);
                    await CopyPayloadAsync(server, caller, length - 4, copy, cancellationToken);
                    continue;
                }
                // Npgsql's internal commit waits for CommandComplete then
                // ReadyForQuery. These frames originate from the real server:
                // https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.cs#L2876
                // https://www.postgresql.org/docs/16/protocol-message-formats.html
                if (!BackendCommitCompleteSeen)
                {
                    if (header[0] != (byte)'C' || length != 11)
                        throw RelayFailure();
                    await server.ReadExactlyAsync(commitTag, cancellationToken);
                    if (!commitTag.AsSpan().SequenceEqual("COMMIT\0"u8))
                        throw RelayFailure();
                    CryptographicOperations.ZeroMemory(commitTag);
                    Volatile.Write(ref _commitCompleteSeen, 1);
                    continue;
                }
                if (header[0] != (byte)'Z' || length != 5)
                    throw RelayFailure();
                await server.ReadExactlyAsync(status, cancellationToken);
                if (status[0] != (byte)'I')
                    throw RelayFailure();
                CryptographicOperations.ZeroMemory(status);
                Volatile.Write(ref _idleReadySeen, 1);
                _acknowledgementWithheld.TrySetResult();
                await _disconnect.Task.WaitAsync(cancellationToken);
                return;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(commitTag);
            CryptographicOperations.ZeroMemory(status);
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static int FrameLength(byte[] header)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        return length is >= 4 and <= MaximumFrameLength ? length : throw RelayFailure();
    }

    private static async Task CopyPayloadAsync(Stream source, Stream destination, int count, byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var length = Math.Min(count, buffer.Length);
            try
            {
                await source.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            count -= length;
        }
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
    }

    private static InvalidOperationException RelayFailure()
        => new("The owned PostgreSQL commit acknowledgement relay failed.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disconnect.TrySetResult();
        _lifetime.Cancel();
        _listener.Stop();
        _caller?.Dispose();
        _server?.Dispose();
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { _lifetime.Dispose(); }
    }
}
