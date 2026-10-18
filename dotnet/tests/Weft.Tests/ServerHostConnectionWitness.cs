using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Weft.Tests;

/// <summary>Opaque connection witness only; it sends no PG protocol or synthetic result.</summary>
internal sealed class ServerHostConnectionWitness : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(100));
    private readonly Task accepting;
    private Task? disposal;
    private int accepted;
    internal int Count => Volatile.Read(ref accepted);
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Port { get; }
    internal ServerHostConnectionWitness()
    {
        listener.Start(8); Port = ((IPEndPoint)listener.LocalEndpoint).Port; accepting = AcceptAsync();
    }
    internal string Route(string source) => new NpgsqlConnectionStringBuilder(source)
    {
        Host = "127.0.0.1",
        Port = Port,
        SslMode = SslMode.Disable,
        GssEncryptionMode = GssEncryptionMode.Disable,
        Timeout = 2,
        CommandTimeout = 5,
        Pooling = false
    }.ConnectionString;
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                Interlocked.Increment(ref accepted); Entered.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel(); listener.Stop(); await accepting.WaitAsync(TimeSpan.FromSeconds(5)); lifetime.Dispose();
    }
}
