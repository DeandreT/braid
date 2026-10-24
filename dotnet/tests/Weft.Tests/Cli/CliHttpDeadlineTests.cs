using System.CommandLine;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weft.Cli;
using Weft.Cli.Authentication;
using Weft.Cli.Commands;
using Weft.Cli.Http;
using Weft.Contracts.Catalog;
using Weft.Contracts.Help;

namespace Weft.Tests.Cli;

/// <summary>Real loopback Kestrel connections with flushed headers and withheld bodies, not scripted stream exceptions.</summary>
public sealed class CliHttpDeadlineTests
{
    [Theory]
    [InlineData("json", 200)]
    [InlineData("bytes", 200)]
    [InlineData("stream", 200)]
    [InlineData("error", 400)]
    [InlineData("sseError", 400)]
    public async Task ARequestDeadlineRemainsOwnedThroughEveryResponseBodyReader(string reader, int status)
    {
        await using var server = await WithheldBody.StartAsync(status);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = Client(server, TimeSpan.FromMilliseconds(500));
        using var response = await client.SendAsync(reader == "sseError" ? SseRequest() : new ApiRequest(ApiOperations.GetHelpSections),
            caller.Token, streaming: reader == "sseError");
        var failure = await Assert.ThrowsAsync<CliException>(async () =>
        {
            switch (reader)
            {
                case "json": await response.ReadJsonAsync<HelpSectionListV1>(caller.Token); break;
                case "bytes": await response.ReadBytesAsync(4096, caller.Token); break;
                case "error" or "sseError": await response.RequireSuccessAsync(caller.Token); break;
                default:
                    await using (var body = await response.ReadStreamAsync(caller.Token))
                        await body.CopyToAsync(Stream.Null, caller.Token);
                    break;
            }
        });
        Assert.Equal(CliExitCode.Transport, failure.Code);
        Assert.False(caller.IsCancellationRequested);
        response.Dispose();
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    public async Task APollBudgetCancelsAnInFlightJsonOrRetryableErrorBodyAndRestoresItsCaller(int status)
    {
        await using var server = await WithheldBody.StartAsync(status);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = Client(server, TimeSpan.FromSeconds(8));
        var environment = new CliEnvironment
        {
            Out = new StringWriter(),
            Error = new StringWriter(),
            OpenStandardInput = () => Stream.Null,
            OpenStandardOutput = () => Stream.Null,
            ConfigurationDirectory = Path.Combine(Path.GetTempPath(), "unused-cli-deadline-profile"),
            TokenCaches = new CliMemoryTokenCaches()
        };
        var globals = new CliGlobals(environment);
        var root = new RootCommand();
        root.Options.Add(globals.Json);
        using var invocation = new CliInvocation(globals, root.Parse([]), caller.Token);
        var failure = await Assert.ThrowsAsync<CliException>(() => CommandSupport.PollAsync<HelpSectionListV1>(invocation,
            TimeSpan.FromMilliseconds(500), "test report", async () =>
                await client.ReadAsync<HelpSectionListV1>(new ApiRequest(ApiOperations.GetHelpSections), invocation.Cancellation)));
        Assert.Equal(CliExitCode.OperationFailed, failure.Code);
        Assert.Equal("timeout", failure.Category);
        Assert.Equal(caller.Token, invocation.Cancellation);
        Assert.False(caller.IsCancellationRequested);
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SuccessfulSseBodiesRemainLiveBeyondTheRequestHeaderDeadline()
    {
        await using var server = await WithheldBody.StartAsync(200, sse: true);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = Client(server, TimeSpan.FromMilliseconds(500));
        using var response = await client.SendAsync(SseRequest(), caller.Token, streaming: true);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), caller.Token);
        server.Release.TrySetResult();
        await using var body = await response.ReadStreamAsync(caller.Token);
        using var text = new StreamReader(body, leaveOpen: true);
        Assert.Contains("data: done", await text.ReadToEndAsync(caller.Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportEnsureKeepsItsPollBudgetUntilTheTerminalJsonBodyHasBeenRead()
    {
        await using var issuer = await CliTestIssuer.StartAsync();
        var data = new CliStubData(Guid.NewGuid().ToString("D"));
        await using var server = await WithheldBody.StartAsync(200, before: async context =>
        {
            if (context.Request.Path != "/api/v1/catalog/context")
                return false;
            await context.Response.WriteAsJsonAsync(new CatalogContextV1("bearer", Guid.NewGuid().ToString("D"), data.Organization,
                "Deadline fixture", CatalogFeaturesV1.ReadOnly, CatalogOrganizationActionsV1.None, null, null),
                WeftApiClient.Json, context.RequestAborted);
            return true;
        });
        using var cli = new CliHarness(issuer, null, server.Origin) { InvocationTimeout = TimeSpan.FromSeconds(5) };
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        var result = await cli.RunAsync("reports", "projection", "ensure", "--project", data.Project, "--mapping", data.Mapping,
            "--revision", data.Revision, "--timeout", "1s");
        Assert.Equal(CliExitCode.OperationFailed, result.Code);
        Assert.Contains("Timed out waiting for the projection report", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Out);
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposingTheResponseSettlesItsActiveReadAndClosesTheOwnedConnection()
    {
        await using var server = await WithheldBody.StartAsync(200);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = Client(server, TimeSpan.FromSeconds(8));
        using var response = await client.SendAsync(new ApiRequest(ApiOperations.GetHelpSections), caller.Token);
        await using var body = await response.ReadStreamAsync(caller.Token);
        var read = body.CopyToAsync(Stream.Null, caller.Token);
        response.Dispose();
        var failure = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        response.Dispose();
    }

    [Fact]
    public async Task CallerCancellationIsNotMisreportedAsAResponseTimeout()
    {
        await using var server = await WithheldBody.StartAsync(200);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = Client(server, TimeSpan.FromSeconds(8));
        using var response = await client.SendAsync(new ApiRequest(ApiOperations.GetHelpSections), caller.Token);
        using var cancelRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.ReadJsonAsync<HelpSectionListV1>(cancelRead.Token));
        Assert.False(caller.IsCancellationRequested);
        response.Dispose();
        await server.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static WeftApiClient Client(WithheldBody server, TimeSpan timeout) =>
        new(server.Origin, new Tokens(), null, TimeProvider.System, TextWriter.Null, timeout);

    private static ApiRequest SseRequest()
    {
        var request = new ApiRequest(ApiOperations.StreamChatTurnEvents);
        foreach (var name in new[] { "organizationId", "projectId", "mappingId", "revisionId", "conversationId", "turnId" })
            request.Path(name, Guid.NewGuid().ToString("D"));
        return request;
    }

    private sealed class Tokens : IAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult("owned-fixture-token");
    }

    private sealed class WithheldBody : IAsyncDisposable
    {
        private readonly CancellationTokenSource shutdown = new();
        private WebApplication app = null!;
        public Uri Origin { get; private set; } = null!;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<WithheldBody> StartAsync(int status, bool sse = false, Func<HttpContext, Task<bool>>? before = null)
        {
            var server = new WithheldBody();
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
                builder.Configuration.Sources.Clear();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                server.app = builder.Build();
                server.app.Run(async context =>
                {
                    if (before is not null && await before(context))
                        return;
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, server.shutdown.Token);
                    try
                    {
                        context.Response.StatusCode = status;
                        context.Response.ContentType = sse ? "text/event-stream" : "application/json";
                        await context.Response.WriteAsync(sse ? ": ready\n\n" : "{", stop.Token);
                        await context.Response.Body.FlushAsync(stop.Token);
                        if (sse)
                        {
                            await server.Release.Task.WaitAsync(stop.Token);
                            await context.Response.WriteAsync("data: done\n\n", stop.Token);
                        }
                        else
                            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    finally
                    {
                        if (context.RequestAborted.IsCancellationRequested)
                            server.Aborted.TrySetResult();
                    }
                });
                await server.app.StartAsync();
                server.Origin = new Uri(server.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync();
            if (app is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await app.StopAsync(stop.Token); }
                finally { await app.DisposeAsync(); }
            }
            shutdown.Dispose();
        }
    }
}
