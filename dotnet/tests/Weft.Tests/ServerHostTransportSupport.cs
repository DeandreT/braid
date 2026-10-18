using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Weft.Tests;

/// <summary>Owned HTTPS edge; it strips every supplied forwarding field before adding exactly one hop.</summary>
[SupportedOSPlatform("linux")]
internal sealed class ServerHostHttpsEdge : IAsyncDisposable
{
    private WebApplication? application;
    private HttpClient? upstream;
    private Task? stopTask, disposeTask, disposal;
    private readonly HashSet<Task> work = [];
    private readonly object sync = new();
    internal bool Settled { get; private set; }

    internal static async Task<ServerHostHttpsEdge> StartAsync(Uri origin, int backendPort, X509Certificate2 certificate)
    {
        var edge = new ServerHostHttpsEdge();
        try
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                ActivityHeadersPropagator = null
            };
            edge.upstream = new(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:" + backendPort.ToString(CultureInfo.InvariantCulture)),
                Timeout = TimeSpan.FromSeconds(15),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, origin.Port,
                listener => { listener.Protocols = HttpProtocols.Http1; listener.UseHttps(certificate); }));
            edge.application = builder.Build();
            edge.application.Run(context =>
            {
                Task task = edge.ForwardAsync(context, origin);
                lock (edge.sync) edge.work.Add(task);
                return task;
            });
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await edge.application.StartAsync(start.Token); return edge;
        }
        catch (Exception)
        {
            try { await edge.DisposeAsync(); } catch (Exception) { }
            throw new ServerHostCaseFailure("The exact owned HTTPS edge did not start.");
        }
    }

    private async Task ForwardAsync(HttpContext context, Uri origin)
    {
        using var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.GetEncodedPathAndQuery());
        message.Headers.Host = origin.Authority;
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || header.Key.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
                || header.Key is "Connection" or "Transfer-Encoding" or "Content-Length") continue;
            message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        message.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1");
        message.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        message.Headers.TryAddWithoutValidation("X-Forwarded-Host", origin.Authority);
        if (context.Request.ContentLength is > 0)
        {
            message.Content = new StreamContent(context.Request.Body);
            message.Content.Headers.ContentLength = context.Request.ContentLength;
            if (context.Request.ContentType is { } type) message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        using var response = await upstream!.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers[header.Key] = header.Value.ToArray();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        if (application is not null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            stopTask ??= application.StopAsync(stop.Token);
            try { await stopTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { errors.Add(new ServerHostCaseFailure("Owned edge stop failed.")); }
            disposeTask ??= application.DisposeAsync().AsTask();
            try { await disposeTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { errors.Add(new ServerHostCaseFailure("Owned edge disposal failed.")); }
            Task[] pending; lock (sync) pending = work.ToArray();
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { errors.Add(new ServerHostCaseFailure("Owned edge requests did not settle.")); }
            Settled = stopTask.IsCompleted && disposeTask.IsCompleted && pending.All(task => task.IsCompleted);
        }
        else Settled = true;
        if (Settled) upstream?.Dispose();
        if (errors.Count != 0) throw new AggregateException("Owned HTTPS edge cleanup failed.", errors);
    }
}

internal static class ServerHostRaw
{
    internal static async Task<ServerHostReply> SendAsync(Uri endpoint, X509Certificate2? certificate, string authority,
        IReadOnlyList<KeyValuePair<string, string>> headers, IPAddress? source = null, bool withheldBody = false, string path = "/api/auth/session")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (source is not null) socket.Bind(new IPEndPoint(source, 0));
        try
        {
            await socket.ConnectAsync(IPAddress.Parse(endpoint.Host), endpoint.Port, deadline.Token);
            await using var network = new NetworkStream(socket, ownsSocket: false);
            Stream stream = network;
            SslStream? tls = null;
            try
            {
                if (certificate is not null)
                {
                    byte[] pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
                    tls = new SslStream(network, leaveInnerStreamOpen: true, (_, actual, _, errors) => actual is not null
                        && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                        && CryptographicOperations.FixedTimeEquals(pin, actual.GetCertHash(HashAlgorithmName.SHA256)));
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = endpoint.Host }, deadline.Token);
                    stream = tls;
                }
                var request = new StringBuilder(withheldBody ? "POST " : "GET ").Append(path).Append(" HTTP/1.1\r\nHost: ").Append(authority).Append("\r\nConnection: close\r\n");
                foreach (var header in headers) request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                if (withheldBody) request.Append("Content-Length: 1024\r\nContent-Type: application/octet-stream\r\n");
                request.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), deadline.Token); await stream.FlushAsync(deadline.Token);
                return await ReadRefusalAsync(stream, deadline.Token);
            }
            finally { if (tls is not null) await tls.DisposeAsync(); }
        }
        catch (Exception error) { throw ServerHostCheck.Safe(error, "The exact owned raw transport operation failed."); }
    }

    // All callers probe an application refusal with a completely withheld request body.
    // Its zero-length HTTP frame completes before Kestrel finishes draining that body;
    // waiting for connection EOF would instead observe unrelated drain/abort behavior.
    internal static async Task<ServerHostReply> ReadRefusalAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream(); var buffer = new byte[1024];
        while (true)
        {
            int previous = (int)received.Length;
            int count = await stream.ReadAsync(buffer, cancellationToken);
            ServerHostCheck.That(count != 0, "The raw owned refusal ended before its complete HTTP boundary.");
            ServerHostCheck.That(received.Length + count <= 65_536, "The raw owned response exceeded its bound.");
            received.Write(buffer, 0, count);
            int start = Math.Max(0, previous - 3);
            int match = received.GetBuffer().AsSpan(start, (int)received.Length - start).IndexOf("\r\n\r\n"u8);
            if (match < 0) continue;

            int end = start + match;
            byte[] bytes = received.ToArray();
            ServerHostCheck.That(bytes.Length == end + 4, "The raw owned refusal contained buffered bytes beyond its empty frame.");
            for (int index = 0; index < end; index++)
                ServerHostCheck.That(bytes[index] is 9 or 10 or 13 or >= 32 and <= 126,
                    "The raw owned refusal contained an invalid HTTP header byte.");
            string[] lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n", StringSplitOptions.None);
            ServerHostCheck.That(lines[0].StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal)
                && lines[0].All(character => character is '\t' or >= ' ' and <= '~'),
                "The raw owned refusal did not have the fixed application status.");
            var fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int split = line.IndexOf(':'); ServerHostCheck.That(split > 0, "The raw owned response header was malformed.");
                string name = line[..split], value = line[(split + 1)..].Trim(' ', '\t');
                ServerHostCheck.That(name.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
                    && value.All(character => character is '\t' or >= ' ' and <= '~'),
                    "The raw owned response header was malformed.");
                fields[name] = fields.TryGetValue(name, out var prior) ? [.. prior, value] : [value];
            }
            ServerHostCheck.That(!fields.ContainsKey("Transfer-Encoding")
                && fields.TryGetValue("Content-Length", out var lengths) && lengths.Length == 1 && lengths[0] == "0",
                "The raw owned refusal did not have one unambiguous empty response length.");
            return new(403, fields, bytes[(end + 4)..]);
        }
    }
}
