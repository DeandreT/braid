using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Braid.Server;

namespace Braid.Tests;

public sealed class CatalogRequestLoggingTests
{
    [Fact]
    public async Task ActualHostQueryAndBodyMarkersStayOutOfAggressiveProviderLogsWhileCategoricalLogsRemain()
    {
        var capture = new MarkerLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.Services.SuppressRequestDiagnostics();
        builder.Services.Configure<LoggerFilterOptions>(options =>
        {
            options.MinLevel = LogLevel.Trace;
            // Provider-specific wildcard/descendant/case variants would override
            // an ordinary parent-category filter without protected post-configuration.
            options.Rules.Add(new LoggerFilterRule("MarkerCapture", CatalogRequestLogging.HostingDiagnosticsCategory + "*", LogLevel.Trace, null));
            options.Rules.Add(new LoggerFilterRule(typeof(MarkerLoggerProvider).FullName,
                "microsoft.aspnetcore.hosting.diagnostics.Upload", LogLevel.Trace, null));
        });
        builder.WebHost.ConfigureKestrel(server => DevelopmentListenAddress.ConfigureKestrel(server, new(IPAddress.Loopback, 0)));
        await using var app = builder.Build();
        app.MapPost("/logging-proof", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            _ = await reader.ReadToEndAsync(context.RequestAborted);
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Braid.Catalog.Policy").LogWarning("CategoricalPolicyDenied");
            context.Response.StatusCode = 204;
        });
        await app.StartAsync();
        var origin = new Uri(Assert.Single(app.Urls));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
        var queryMarker = "owned-query-" + Guid.NewGuid().ToString("N");
        var bodyMarker = "owned-body-" + Guid.NewGuid().ToString("N");
        using var content = new StringContent(bodyMarker);
        using var response = await client.PostAsync("/logging-proof?nativeMetadata=" + queryMarker, content);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics.Upload");
        logger.LogWarning("{Marker}", queryMarker);
        await app.StopAsync();
        Assert.Contains(capture.Messages, message => message.Contains("CategoricalPolicyDenied", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Messages, message => message.Contains(queryMarker, StringComparison.Ordinal) || message.Contains(bodyMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Categories, category => category.StartsWith(CatalogRequestLogging.HostingDiagnosticsCategory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActualLocalHttp1ListenerAcceptsLargeRequestLineAndRejectsBeyondItsConfiguredCeiling()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.SuppressRequestDiagnostics();
        builder.WebHost.ConfigureKestrel(server => DevelopmentListenAddress.ConfigureKestrel(server, new(IPAddress.Loopback, 0)));
        await using var app = builder.Build();
        app.MapGet("/request-line", () => Results.NoContent());
        await app.StartAsync();
        Assert.Equal(65_536, app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestLineSize);
        var origin = new Uri(Assert.Single(app.Urls));
        var accepted = await StatusLineAsync(origin, "/request-line?value=" + new string('x', 60_000));
        Assert.StartsWith("HTTP/1.1 204", accepted, StringComparison.Ordinal);
        var oversized = await StatusLineAsync(origin, "/request-line?value=" + new string('x', 65_536));
        Assert.StartsWith("HTTP/1.1 414", oversized, StringComparison.Ordinal);
        await app.StopAsync();
    }

    private static async Task<string> StatusLineAsync(Uri origin, string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, origin.Port, deadline.Token);
        var request = Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\n\r\n");
        await client.GetStream().WriteAsync(request, deadline.Token);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        return await reader.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("The owned HTTP1 proof returned no status line.");
    }

    [ProviderAlias("MarkerCapture")]
    private sealed class MarkerLoggerProvider : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal ConcurrentQueue<string> Categories { get; } = new();
        public ILogger CreateLogger(string categoryName) => new MarkerLogger(this, categoryName);
        public void Dispose() { }
    }

    private sealed class MarkerLogger(MarkerLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        { provider.Categories.Enqueue(category); provider.Messages.Enqueue(formatter(state, exception)); }
    }
}
