using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Weft.Api.Chat;

namespace Weft.Tests;

/// <summary>Actual HTTP/1 decoding, with no database, provider, proxy or synthetic body parser.</summary>
public sealed class ChatEmptyBodyFramingTests : IAsyncLifetime
{
    private WebApplication app = null!;
    private Uri origin = null!;
    private int cancellations, sentinels;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        app = builder.Build();
        app.MapPost("/cancel", async context =>
        {
            try
            {
                await ChatRequestParser.RequireNoBodyAsync(context.Request, context.RequestAborted);
                Interlocked.Increment(ref cancellations);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
            catch (ChatRequestException error) { context.Response.StatusCode = error.StatusCode; }
        });
        app.MapPost("/sentinel", context =>
        {
            Interlocked.Increment(ref sentinels);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        origin = new Uri(Assert.Single(app.Urls));
    }

    [Theory]
    [InlineData("Content-Length: 0\r\n", "")]
    [InlineData("Transfer-Encoding: chunked\r\n", "0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "0;empty=yes\r\n\r\n")]
    public async Task Complete_empty_frames_act_once(string headers, string body)
    {
        var response = await ExchangeAsync(Request(headers, body));
        Assert.StartsWith("HTTP/1.1 204", response, StringComparison.Ordinal);
        Assert.Equal(1, cancellations);
        Assert.Equal(0, sentinels);
    }

    [Theory]
    [InlineData("Transfer-Encoding: chunked\r\n", "1\r\nx\r\n0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "0\r\nX-Undeclared: refused\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nTrailer: X-Extra\r\n", "0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "z\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "-1\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "1\r\nx!\r\n0\r\n\r\n")]
    [InlineData("Content-Length: 1\r\n", "x")]
    [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 0\r\n", "0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 5\r\n", "0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nX-Content-Length: 0\r\n", "0\r\n\r\n")]
    public async Task Refused_frames_cannot_execute_an_appended_request(string headers, string body)
    {
        var response = await ExchangeAsync(Request(headers, body, close: false) + Sentinel());
        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP/1.1 204", response, StringComparison.Ordinal);
        Assert.Equal(0, cancellations);
        Assert.Equal(0, sentinels);
    }

    [Theory]
    [InlineData("1\r\n")]
    [InlineData("1\r\nx\r\n")]
    [InlineData("0\r\nX-Truncated: value")]
    public async Task Truncated_chunked_requests_never_act(string body)
    {
        var response = await ExchangeAsync(Request("Transfer-Encoding: chunked\r\n", body), halfClose: true);
        Assert.DoesNotContain("HTTP/1.1 204", response, StringComparison.Ordinal);
        Assert.Equal(0, cancellations);
        Assert.Equal(0, sentinels);
    }

    [Fact]
    public async Task Valid_pipeline_messages_keep_their_own_body_boundaries()
    {
        var response = await ExchangeAsync(Request("Transfer-Encoding: chunked\r\n", "0\r\n\r\n", close: false) + Sentinel());
        Assert.Equal(2, response.Split("HTTP/1.1 204", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, cancellations);
        Assert.Equal(1, sentinels);
    }

    private string Request(string headers, string body, bool close = true) =>
        $"POST /cancel HTTP/1.1\r\nHost: {origin.Authority}\r\n{headers}{(close ? "Connection: close\r\n" : "")}\r\n{body}";

    private string Sentinel() => $"POST /sentinel HTTP/1.1\r\nHost: {origin.Authority}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private async Task<string> ExchangeAsync(string message, bool halfClose = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, origin.Port, deadline.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(message), deadline.Token);
        if (halfClose) client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count;
            try { count = await stream.ReadAsync(buffer, deadline.Token); }
            catch (IOException) when (halfClose) { break; }
            if (count == 0) break;
            Assert.True(response.Length + count <= 65536, "The fixture response exceeded its bound.");
            response.Write(buffer, 0, count);
        }
        return Encoding.ASCII.GetString(response.ToArray());
    }

    public async Task DisposeAsync() => await app.DisposeAsync();
}
