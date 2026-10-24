using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using Microsoft.Identity.Client;
using Weft.Cli;
using Weft.Cli.Authentication;
using Weft.Cli.Configuration;

namespace Weft.Tests.Cli;

/// <summary>A test token store: the MSAL cache in memory, standing in for the operating-system protected store.</summary>
internal sealed class CliMemoryTokenCaches : ITokenCacheStore
{
    private readonly ConcurrentDictionary<string, byte[]> data = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }
    public int Attached;

    public Task AttachAsync(CliProfile profile, ITokenCache cache, CancellationToken cancellationToken)
    {
        if (Unavailable)
            throw MsalProtectedTokenCacheStore.Unavailable();
        Interlocked.Increment(ref Attached);
        cache.SetBeforeAccess(args =>
        {
            if (data.TryGetValue(profile.Name, out var bytes))
                args.TokenCache.DeserializeMsalV3(bytes);
        });
        cache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                data[profile.Name] = args.TokenCache.SerializeMsalV3();
        });
        return Task.CompletedTask;
    }

    public bool Holds(string profile) => data.TryGetValue(profile, out var bytes) && Encoding.UTF8.GetString(bytes).Contains("\"secret\"", StringComparison.Ordinal);
}

/// <summary>Virtual time: delays complete at once and advance the clock, and every requested delay is recorded.</summary>
internal sealed class CliTestTime : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Delays.Enqueue(dueTime);
            lock (gate)
                now += dueTime;
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        return new Stopped();
    }

    private sealed class Stopped : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record CliResult(int Exit, string Out, string Error, byte[] Binary)
{
    public CliExitCode Code => (CliExitCode)Exit;
    public override string ToString() => $"exit {Exit}\nstdout:\n{Out}\nstderr:\n{Error}";
}

/// <summary>
/// Runs <c>weft</c> commands in process against an owned issuer and an API handler, with a private configuration
/// directory, the in-memory token store and virtual time. Each run gets fresh streams; configuration and tokens persist.
/// </summary>
internal sealed class CliHarness : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("weft-cli-test-").FullName;

    private readonly IMsalHttpClientFactory identity;
    private readonly Func<IEnumerable<string>> secrets;
    private readonly Guid tenant, client;
    private readonly string scope;

    public CliHarness(CliTestIssuer issuer, HttpMessageHandler? api, Uri? server = null)
        : this(issuer.MsalHttp, api, server ?? new Uri("https://weft.example.test/"), () => issuer.IssuedSecrets, issuer.Tenant,
            issuer.PublicClient, issuer.Scope)
    {
    }

    /// <summary>A harness for any issuer: its MSAL transport, the registration to sign in with, and every secret it issued.</summary>
    public CliHarness(IMsalHttpClientFactory identity, HttpMessageHandler? api, Uri server, Func<IEnumerable<string>> secrets, Guid tenant,
        Guid client, string scope)
    {
        this.identity = identity;
        this.secrets = secrets;
        this.tenant = tenant;
        this.client = client;
        this.scope = scope;
        Api = api;
        Server = server;
    }

    public HttpMessageHandler? Api { get; }
    public Uri Server { get; }
    public CliMemoryTokenCaches Caches { get; } = new();
    public CliTestTime Time { get; } = new();
    public TimeSpan InvocationTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public string ConfigurationDirectory => directory;
    public Func<Uri, Task>? OpenBrowser { get; set; }

    /// <summary>Saves the default profile for this issuer and server.</summary>
    public async Task<CliResult> ConfigureAsync(string name = "test")
    {
        var result = await RunAsync("profile", "set", name, "--server", Server.GetLeftPart(UriPartial.Authority), "--tenant", tenant.ToString("D"),
            "--client-id", client.ToString("D"), "--scope", scope, "--default");
        Assert.True(result.Exit == 0, result.ToString());
        return result;
    }

    public async Task<CliResult> LoginAsync()
    {
        var result = await RunAsync("login");
        Assert.True(result.Exit == 0, result.ToString());
        return result;
    }

    public Task<CliResult> RunAsync(params string[] args) => RunWithInputAsync(null, args);

    public async Task<CliResult> RunWithInputAsync(byte[]? input, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var binary = new MemoryStream();
        var environment = new CliEnvironment
        {
            Out = output,
            Error = error,
            OpenStandardInput = () => new MemoryStream(input ?? []),
            OpenStandardOutput = () => new NonClosing(binary),
            ConfigurationDirectory = directory,
            TokenCaches = Caches,
            ApiHandler = Api,
            IdentityHttp = identity,
            OpenBrowser = OpenBrowser,
            Time = Time
        };
        using var timeout = new CancellationTokenSource(InvocationTimeout);
        var exit = await CliApplication.RunAsync(args, environment, timeout.Token);
        var result = new CliResult(exit, output.ToString(), error.ToString(), binary.ToArray());
        foreach (var secret in secrets())
            Assert.False(result.Out.Contains(secret, StringComparison.Ordinal) || result.Error.Contains(secret, StringComparison.Ordinal),
                "A token reached the CLI's output.");
        return result;
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }
}
