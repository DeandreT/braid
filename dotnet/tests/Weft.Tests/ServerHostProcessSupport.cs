using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Weft.Tests;

internal sealed class ServerHostCaseFailure(string category) : Exception(category)
{
    public override string ToString() => nameof(ServerHostCaseFailure);
}

internal static class ServerHostCheck
{
    internal static void That(bool condition, string category)
    {
        if (!condition) throw new ServerHostCaseFailure(category);
    }

    internal static Exception Safe(Exception error, string category) =>
        error is ServerHostCaseFailure ? error : new ServerHostCaseFailure(category);

    internal static void Throw(Exception? primary, IReadOnlyCollection<Exception> cleanup)
    {
        if (primary is not null && cleanup.Count != 0)
            throw new AggregateException("The owned host case and cleanup failed.", new[] { primary }.Concat(cleanup));
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup.Count != 0) throw new AggregateException("The owned host cleanup failed.", cleanup);
    }
}

internal sealed class ServerHostReply(int status, Dictionary<string, string[]> headers, byte[] bytes)
{
    internal int Status { get; } = status;
    internal IReadOnlyDictionary<string, string[]> Headers { get; } = headers;
    internal byte[] Bytes { get; } = bytes;
    internal JsonDocument Json() => JsonDocument.Parse(Bytes, new JsonDocumentOptions { MaxDepth = 16 });
    public override string ToString() => nameof(ServerHostReply);
}

/// <summary>Exactly one directly owned published Server process; all output remains bounded private memory.</summary>
[SupportedOSPlatform("linux")]
internal sealed class ServerHostProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(100));
    private readonly Task<string> stdout, stderr;
    private Task? disposal, stopping;
    private bool overflow;
    internal bool Settled { get; private set; }
    internal int Id => process.Id;

    private ServerHostProcess(Process process)
    {
        this.process = process;
        stdout = CaptureAsync(process.StandardOutput);
        stderr = CaptureAsync(process.StandardError);
    }

    internal static ServerHostProcess Start(IReadOnlyDictionary<string, string?> environment, bool privacyProbe = false)
    {
        string variable = privacyProbe ? "WEFT_HOST_PRIVACY_PROBE_PATH" : "WEFT_HOST_TEST_SERVER_PATH";
        string? assembly = Environment.GetEnvironmentVariable(variable);
        ServerHostCheck.That(assembly is not null && Path.IsPathFullyQualified(assembly) && File.Exists(assembly),
            "The selected published host or non-shipped probe was not supplied.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assembly!)!
        };
        start.ArgumentList.Add(assembly!);
        start.Environment.Clear();
        foreach (string name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var pair in environment)
        {
            if (pair.Value is null) start.Environment.Remove(pair.Key);
            else start.Environment[pair.Key] = pair.Value;
        }
        // This launcher clears inherited variables; retain the isolated-home CLI opt-out explicitly.
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        try
        {
            return new ServerHostProcess(Process.Start(start) ?? throw new ServerHostCaseFailure("The selected child did not start."));
        }
        catch (Exception) { throw new ServerHostCaseFailure("The selected owned child could not start."); }
    }

    private async Task<string> CaptureAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[1024];
        try
        {
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), lifetime.Token);
                if (count == 0) return text.ToString();
                if (text.Length + count > 32_768)
                {
                    overflow = true;
                    throw new ServerHostCaseFailure("Owned child output exceeded its private bound.");
                }
                text.Append(buffer, 0, count);
            }
        }
        catch (Exception error) { throw ServerHostCheck.Safe(error, "Owned child output capture did not settle."); }
    }

    internal async Task<int> WaitForExitAsync(TimeSpan budget)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            bounded.CancelAfter(budget);
            await process.WaitForExitAsync(bounded.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(bounded.Token);
            ServerHostCheck.That(!overflow, "Owned child output was truncated.");
            return process.ExitCode;
        }
        catch (Exception error) { throw ServerHostCheck.Safe(error, "The owned child did not exit within its fixed bound."); }
    }

    internal async Task AssertPrivateOutputAsync(IEnumerable<string> operands)
    {
        ServerHostCheck.That(process.HasExited, "Private output was inspected before child settlement.");
        string first = await stdout, second = await stderr;
        ServerHostCheck.That(!overflow && operands.Where(value => value.Length != 0)
            .All(value => !first.Contains(value, StringComparison.Ordinal) && !second.Contains(value, StringComparison.Ordinal)),
            "A private operand escaped the owned process output boundary.");
    }

    internal async Task<Uri> WaitForLoopbackListenerAsync(string scheme = "http")
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            while (true)
            {
                ServerHostCheck.That(!process.HasExited, "The owned host exited before listener readiness.");
                var endpoints = OwnedIpv4Listeners();
                ServerHostCheck.That(endpoints.Count <= 1, "The owned host opened more than one listener.");
                if (endpoints.Count == 1)
                {
                    ServerHostCheck.That(IPAddress.IsLoopback(endpoints[0].Address) && endpoints[0].Port != 0,
                        "The owned host listener was not the expected assigned loopback endpoint.");
                    return new Uri(scheme + "://" + endpoints[0].Address + ":" + endpoints[0].Port.ToString(CultureInfo.InvariantCulture));
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), bounded.Token);
            }
        }
        catch (Exception error) { throw ServerHostCheck.Safe(error, "The exact owned listener did not become ready."); }
    }

    internal IReadOnlyList<IPEndPoint> OwnedIpv4Listeners()
    {
        try
        {
            var inodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/fd"))
            {
                string? target = new FileInfo(path).LinkTarget;
                if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']'))
                    inodes.Add(target[8..^1]);
            }
            var endpoints = new List<IPEndPoint>();
            foreach (string row in File.ReadLines("/proc/net/tcp").Skip(1))
            {
                string[] cells = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length < 10 || cells[3] != "0A" || !inodes.Contains(cells[9])) continue;
                string[] address = cells[1].Split(':');
                byte[] bytes = Convert.FromHexString(address[0]); Array.Reverse(bytes);
                endpoints.Add(new(new IPAddress(bytes), int.Parse(address[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
            return endpoints;
        }
        catch (Exception error) { throw ServerHostCheck.Safe(error, "Exact owned socket observation failed."); }
    }

    internal Task StopAsync() => stopping ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        if (process.HasExited) { await WaitForExitAsync(TimeSpan.FromSeconds(5)); Settled = true; return; }
        ServerHostCheck.That(SendSignal(process.Id, 15) == 0, "The exact owned child could not receive graceful stop.");
        int code = await WaitForExitAsync(TimeSpan.FromSeconds(10));
        ServerHostCheck.That(code == 0, "The owned host did not complete a successful graceful stop.");
        Settled = true;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try { await StopAsync(); }
        catch (Exception error) { failure = ServerHostCheck.Safe(error, "Owned host graceful cleanup failed."); }
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: false); await WaitForExitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { failure ??= new ServerHostCaseFailure("The exact owned child could not be reaped."); }
        }
        if (process.HasExited)
        {
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { failure ??= new ServerHostCaseFailure("Owned capture tasks did not settle."); }
            Settled = stdout.IsCompleted && stderr.IsCompleted;
            if (Settled) { process.Dispose(); lifetime.Dispose(); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public override string ToString() => nameof(ServerHostProcess);
}

[SupportedOSPlatform("linux")]
internal static class ServerHostHttp
{
    internal static HttpClient Client(Uri origin, X509Certificate2? certificate = null)
    {
        var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
        if (certificate is not null)
        {
            byte[] pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
            handler.ServerCertificateCustomValidationCallback = (request, actual, _, errors) =>
                request.RequestUri?.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority)
                && actual is not null && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                && CryptographicOperations.FixedTimeEquals(pin, actual.GetCertHash(HashAlgorithmName.SHA256));
        }
        return new(handler)
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    internal static async Task<ServerHostReply> SendAsync(HttpClient client, string path,
        HttpMethod? method = null, IReadOnlyDictionary<string, string>? headers = null, byte[]? body = null,
        CancellationToken cancellationToken = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        using var message = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (headers is not null)
            foreach (var pair in headers) message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        if (body is not null) message.Content = new ByteArrayContent(body);
        try
        {
            using var reply = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            await using var stream = await reply.Content.ReadAsStreamAsync(bounded.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[1024];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, bounded.Token);
                if (count == 0) break;
                ServerHostCheck.That(bytes.Length + count <= 65_536, "An owned host response exceeded its private bound.");
                bytes.Write(buffer, 0, count);
            }
            var received = reply.Headers.Concat(reply.Content.Headers)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            ServerHostCheck.That(received.Sum(pair => pair.Key.Length + pair.Value.Sum(value => value.Length)) <= 32_768,
                "Owned response headers exceeded their private bound.");
            return new((int)reply.StatusCode, received, bytes.ToArray());
        }
        catch (Exception error) { throw ServerHostCheck.Safe(error, "The ordinary complete owned HTTP response failed."); }
    }

    internal static async Task<ServerHostReply> StartupStatusAsync(HttpClient client, string? cookie = null)
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var headers = new Dictionary<string, string> { ["X-Weft-Session"] = "1" };
        if (cookie is not null) headers["Cookie"] = cookie;
        while (true)
        {
            startup.Token.ThrowIfCancellationRequested();
            ServerHostReply? reply = null;
            try
            {
                reply = await SendAsync(client, "/api/auth/session", headers: headers, cancellationToken: startup.Token);
            }
            catch (ServerHostCaseFailure) when (!startup.IsCancellationRequested)
            {
                // Startup observation only. No application action is replayed.
            }
            if (reply is not null)
            {
                if (reply.Status == 200) return reply;
                ServerHostCheck.That(reply.Status == 503, "The startup status did not remain categorically closed.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), startup.Token);
        }
    }

    internal static void Mode(ServerHostReply reply, string expected)
    {
        using var document = reply.Json(); var root = document.RootElement;
        ServerHostCheck.That(reply.Status == 200 && root.ValueKind == JsonValueKind.Object
            && root.EnumerateObject().Count() == 2 && root.GetProperty("version").GetInt32() == 1
            && root.GetProperty("mode").GetString() == expected, "The actual host returned an incompatible mode status.");
        PrivateHeaders(reply);
    }

    internal static void PrivateHeaders(ServerHostReply reply) => ServerHostCheck.That(
        reply.Headers.TryGetValue("Cache-Control", out var cache) && cache.Any(value => value.Contains("no-store", StringComparison.OrdinalIgnoreCase))
        && !reply.Headers.ContainsKey("ETag"), "The status privacy/cache headers did not match their contract.");
}
