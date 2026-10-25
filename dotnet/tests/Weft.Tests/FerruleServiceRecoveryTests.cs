using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class FerruleServiceRecoveryTests
{
    private const string Identity = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    [Theory]
    [InlineData("inspection")]
    [InlineData("preview")]
    [InlineData("compile")]
    [InlineData("execute")]
    public void RecoveryIncludesBufferingAdmissionExecutionReapingAndMargin(string profile)
    {
        var options = new FerruleServiceRunnerOptions(new Connector(), profile, Identity,
            profile == "compile" ? Identity : null, profile == "execute" ? Identity : null);
        Assert.Equal(FerruleServiceRunnerOptions.MaximumServiceAdmissionWait + options.Profile.MaximumWallTimeout * 2 +
            ProcessWorkerOptions.KillTimeout + TimeSpan.FromSeconds(5), options.QuarantineBound);
        Assert.True(options.QuarantineBound > options.Profile.MaximumWallTimeout + ProcessWorkerOptions.KillTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), options.UncertaintyWindow);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RecoveryProbeDelay);
    }

    [Fact]
    public async Task UncertainCallIsNeverRetriedAndLaterCallsRecoverWithoutRestart()
    {
        var connector = new Connector();
        await using var runner = Runner(connector);
        var timer = Stopwatch.StartNew();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        Assert.False(runner.Available);
        connector.Uncertain = false;
        for (var index = 0; index < 20; index++)
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 2 }, default)).Failure);
        Assert.Equal(1, connector.Deliveries);
        await Reopened(runner);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, connector.Deliveries); // Recovery probes carry no frame.
        Assert.Equal(2, connector.Channels);
        Assert.Equal(new byte[] { 3 }, (await runner.RunAsync(new byte[] { 3 }, default)).Output);
        Assert.Equal(2, connector.Deliveries);
        Assert.Equal(3, connector.Channels); // Recovery is discarded, next operation has its own fresh channel.
        await runner.StopAsync();
    }

    [Theory]
    [InlineData("build")]
    [InlineData("protocol")]
    [InlineData("unavailable")]
    [InlineData("throw")]
    public async Task AFailedFreshProbeKeepsAdmissionClosedAndNeverSendsAFrame(string refusal)
    {
        var connector = new Connector();
        var runner = Runner(connector);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        connector.Refusal = refusal;
        await Task.Delay(700);
        Assert.False(runner.Available);
        Assert.InRange(connector.Channels, 2, 5);
        Assert.Equal(1, connector.Deliveries);
        connector.Uncertain = false;
        connector.Refusal = null;
        await Reopened(runner);
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task FlappingWithinTheWindowExtendsQuarantineAndDoesNotProbeOrDispatchEarly()
    {
        var connector = new Connector();
        await using var runner = Runner(connector);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
            await Reopened(runner);
        }
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        var channels = connector.Channels;
        await Task.Delay(500);
        Assert.False(runner.Available);
        Assert.Equal(channels, connector.Channels);
        Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(new byte[] { 2 }, default)).Failure);
        Assert.Equal(3, connector.Deliveries);
        connector.Uncertain = false;
        await Reopened(runner);
        Assert.Equal(new byte[] { 3 }, (await runner.RunAsync(new byte[] { 3 }, default)).Output);
    }

    [Theory]
    [InlineData("compile")]
    [InlineData("execute")]
    public async Task RecoveryAlsoRequiresTheConfiguredToolchainOrRuntime(string profile)
    {
        var connector = new Connector();
        var runner = new FerruleServiceRunner(new(connector, profile, Identity,
            profile == "compile" ? Identity : null, profile == "execute" ? Identity : null)
        {
            QuarantineBound = TimeSpan.FromMilliseconds(250),
            RecoveryProbeDelay = TimeSpan.FromMilliseconds(200)
        });
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        connector.Refusal = "runtime";
        await Task.Delay(500);
        Assert.False(runner.Available);
        Assert.Equal(1, connector.Deliveries);
        connector.Refusal = null;
        connector.Uncertain = false;
        await Reopened(runner);
        await runner.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownCancelsRecoveryAndCannotReopenOrHideUncertainty()
    {
        var connector = new Connector();
        var runner = Runner(connector);
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await runner.RunAsync(new byte[] { 1 }, default)).Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(runner.StopAsync);
        connector.Uncertain = false;
        await Task.Delay(400);
        Assert.False(runner.Available);
        Assert.Equal(1, connector.Channels);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.DisposeAsync());
    }

    [Fact]
    public async Task RecoveryCannotReopenWhileAnOlderAdmittedCallIsStillOutstanding()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector
        {
            BeforeAnswer = async (delivery, token) =>
            {
                if (delivery == 2) entered.TrySetResult();
                await (delivery == 1 ? first.Task : second.Task).WaitAsync(token);
            }
        };
        await using var runner = Runner(connector);
        var a = runner.RunAsync(new byte[] { 1 }, default);
        var b = runner.RunAsync(new byte[] { 2 }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.TrySetResult();
        Assert.Equal(WorkerProcessFailure.CleanupFailed, (await a).Failure);
        await Task.Delay(500);
        Assert.False(runner.Available);
        connector.Uncertain = false;
        second.TrySetResult();
        Assert.Equal(WorkerProcessFailure.None, (await b).Failure);
        await Reopened(runner);
        Assert.Equal(2, connector.Deliveries);
    }

    [Fact]
    public async Task HostHealthTracksQuarantineAndReopening()
    {
        var connector = new Connector();
        await using var runner = Runner(connector);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IInspectionWorkerRunner>(runner);
        await using var app = builder.Build();
        app.MapWeftHealth();
        await app.StartAsync();
        using var http = app.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/health/ready")).StatusCode);
        await runner.RunAsync(new byte[] { 1 }, default);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/v1/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/health")).StatusCode);
        connector.Uncertain = false;
        await Reopened(runner);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/health/ready")).StatusCode);
    }

    internal static async Task Reopened(FerruleServiceRunner runner)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!runner.Available) await Task.Delay(10, deadline.Token);
    }

    internal static FerruleServiceRunner Runner(Connector connector) => new(new(connector, "inspection", Identity)
    {
        QuarantineBound = TimeSpan.FromMilliseconds(250),
        RecoveryProbeDelay = TimeSpan.FromMilliseconds(200),
        UncertaintyWindow = TimeSpan.FromSeconds(2)
    });

    internal sealed class Connector : IFerruleServiceConnector
    {
        internal volatile bool Uncertain = true;
        internal volatile string? Refusal;
        internal int Channels;
        internal int Deliveries;
        internal Func<int, CancellationToken, Task>? BeforeAnswer;
        public IFerruleServiceChannel Open()
        {
            Interlocked.Increment(ref Channels);
            return new Channel(this);
        }

        private sealed class Channel(Connector owner) : IFerruleServiceChannel
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (owner.Refusal == "throw") throw new HttpRequestException();
                    var ready = new HttpResponseMessage(owner.Refusal == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
                    ready.Headers.Add(InspectionTransport.ImageHeader, owner.Refusal == "build" ? "sha256:" + new string('2', 64) : Identity);
                    ready.Headers.Add(FerruleServiceProtocol.ProtocolHeader, owner.Refusal == "protocol" ? "other" : FerruleServiceProtocol.Version);
                    ready.Headers.Add(FerruleServiceProtocol.CompileToolchainHeader, owner.Refusal == "runtime" ? "sha256:" + new string('2', 64) : Identity);
                    ready.Headers.Add(FerruleServiceProtocol.CompiledRuntimeHeader, owner.Refusal == "runtime" ? "sha256:" + new string('2', 64) : Identity);
                    return ready;
                }
                var frame = await request.Content!.ReadAsByteArrayAsync(token);
                var delivery = Interlocked.Increment(ref owner.Deliveries);
                if (owner.BeforeAnswer is { } beforeAnswer) await beforeAnswer(delivery, token);
                if (owner.Uncertain) throw new IOException("Connection lost after frame delivery.");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(frame) };
                response.Content.Headers.ContentType = new(InspectionTransport.MediaType);
                response.Headers.Add(InspectionTransport.ImageHeader, Identity);
                response.Headers.Add(FerruleServiceProtocol.ProtocolHeader, FerruleServiceProtocol.Version);
                response.Headers.Add(InspectionTransport.ResultHeader, "none");
                return response;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
