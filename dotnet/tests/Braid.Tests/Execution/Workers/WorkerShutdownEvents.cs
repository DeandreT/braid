using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Braid.Tests;

/// <summary>A live, bounded daemon stream; no lookback and no image-wide ownership inference.</summary>
[SupportedOSPlatform("linux")]
internal sealed class WorkerShutdownEvents : IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly HttpResponseMessage response;
    private readonly CancellationTokenSource lifetime;
    private readonly Task reading;
    private readonly object gate = new();
    private readonly List<WorkerShutdownEvent> rows = [];
    private TaskCompletionSource changed = NewSignal();
    private Exception? failure;
    private bool ended;
    private int disposed;

    private WorkerShutdownEvents(HttpClient client, HttpResponseMessage response, CancellationTokenSource lifetime)
    {
        this.client = client; this.response = response; this.lifetime = lifetime;
        reading = ReadAsync();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task<WorkerShutdownEvents> OpenAsync(CancellationToken token)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://docker"), Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var filters = JsonSerializer.Serialize(new Dictionary<string, string[]>
            { ["type"] = ["container"], ["event"] = ["create", "start", "die", "destroy"] });
            var response = await client.GetAsync("/events?filters=" + Uri.EscapeDataString(filters), HttpCompletionOption.ResponseHeadersRead, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new(client, response, lifetime);
        }
        catch { client.Dispose(); lifetime.Dispose(); throw; }
    }

    internal WorkerShutdownEvent[] Rows(string exactId)
    { lock (gate) { ThrowFailure(); return rows.Where(row => row.Id == exactId).ToArray(); } }

    internal async Task<WorkerShutdownEvent> WaitAsync(string exactId, string action, CancellationToken token)
    {
        while (true)
        {
            Task next;
            lock (gate)
            {
                ThrowFailure();
                if (rows.FirstOrDefault(row => row.Id == exactId && row.Action == action) is { } match) return match;
                if (ended) throw new InvalidOperationException("Owned daemon stream ended before a required event.");
                next = changed.Task;
            }
            await next.WaitAsync(token);
        }
    }
    private void ThrowFailure()
    { if (failure is not null) throw new InvalidOperationException("Owned daemon observation failed.", failure); }

    private async Task ReadAsync()
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
            var buffer = new byte[8192];
            using var line = new MemoryStream();
            var total = 0; var observed = 0;
            while (await stream.ReadAsync(buffer, lifetime.Token) is var count && count != 0)
            {
                total += count;
                if (total > 2 * 1024 * 1024) throw new InvalidOperationException("Daemon event stream exceeded its byte bound.");
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != '\n')
                    {
                        if (line.Length >= 65536) throw new InvalidOperationException("Daemon event line exceeded its bound.");
                        line.WriteByte(buffer[index]); continue;
                    }
                    if (++observed > 1000) throw new InvalidOperationException("Daemon event stream exceeded its row bound.");
                    using var document = JsonDocument.Parse(line.ToArray());
                    line.SetLength(0);
                    var item = document.RootElement;
                    if (item.GetProperty("Type").GetString() != "container") throw new InvalidOperationException("Unexpected daemon event type.");
                    var actor = item.GetProperty("Actor");
                    var id = actor.GetProperty("ID").GetString();
                    if (id is not { Length: 64 } || !id.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Incomplete event ID.");
                    var attributes = actor.GetProperty("Attributes");
                    var name = attributes.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                    if (name is null || !name.StartsWith("braid-inspect-", StringComparison.Ordinal)) continue;
                    var action = item.GetProperty("Action").GetString();
                    if (action is not ("create" or "start" or "die" or "destroy")) throw new InvalidOperationException("Unexpected event action.");
                    var image = attributes.TryGetProperty("image", out var imageValue) ? imageValue.GetString() : null;
                    var row = new WorkerShutdownEvent(id, name, action, item.GetProperty("timeNano").GetInt64(), image);
                    lock (gate)
                    {
                        rows.Add(row); var previous = changed; changed = NewSignal(); previous.TrySetResult();
                    }
                }
            }
            if (line.Length != 0) throw new InvalidOperationException("Daemon event stream ended with a partial event.");
            if (!lifetime.IsCancellationRequested) throw new InvalidOperationException("Daemon event stream ended unexpectedly.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && Volatile.Read(ref disposed) != 0) { }
        catch (Exception error) { lock (gate) { failure = error; changed.TrySetResult(); } throw; }
        finally { lock (gate) { ended = true; changed.TrySetResult(); } }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await lifetime.CancelAsync();
            await reading.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { response.Dispose(); client.Dispose(); lifetime.Dispose(); }
    }
}

internal sealed record WorkerShutdownEvent(string Id, string Name, string Action, long TimeNano, string? Image);
