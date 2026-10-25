using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weft.Application;
using Weft.Deployment;
using Weft.MachineEndpoint;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>The call bounds of the HTTP contract: the credential's expiry, the work bound and the whole-call deadline.</summary>
public sealed class MappingEndpointRouteTests
{
    [Fact]
    public async Task An_uncertain_invocation_is_not_completed_and_a_later_attempt_uses_the_reopened_runner()
    {
        var connector = new FerruleServiceRecoveryTests.Connector();
        await using var runner = FerruleServiceRecoveryTests.Runner(connector);
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));
        test.RuntimeAvailable = () => runner.Available;
        test.Data.Execute = async token =>
        {
            var result = await runner.RunAsync(new byte[] { 1 }, token);
            return result.Failure == WorkerProcessFailure.None ? test.Data.Data.Result : new(CompiledBuildOutcome.Unavailable);
        };
        Assert.Equal(503, (await test.CallAsync(new MemoryStream(test.Body))).Status);
        Assert.Equal(MachineInvocationOutcome.Unavailable, Assert.Single(test.Data.Finishes).Outcome);
        Assert.Null(Assert.Single(test.Data.Finishes).Response);
        Assert.Equal(503, await test.ReadyAsync());
        connector.Uncertain = false;
        await FerruleServiceRecoveryTests.Reopened(runner);
        Assert.Equal(204, await test.ReadyAsync());
        var recovered = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(200, recovered.Status);
        Assert.Equal("false", recovered.Context.Response.Headers[MappingEndpointRoutes.ReplayedHeader].ToString());
        Assert.Equal(MachineInvocationOutcome.Completed, test.Data.Finishes.Last().Outcome);
        var replay = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal("true", replay.Context.Response.Headers[MappingEndpointRoutes.ReplayedHeader].ToString());
        Assert.Equal(2, connector.Deliveries);
    }

    [Fact]
    public async Task A_credential_that_expires_while_the_body_is_withheld_claims_and_runs_nothing()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromSeconds(3));
        var timer = Stopwatch.StartNew();
        var call = await test.CallAsync(new WithheldBody(test.Body[..(test.Body.Length / 2)]));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "The credential's expiry, not the deadline, ends the upload.");
        Assert.Equal(401, call.Status); Assert.Equal("Bearer", call.Context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("unauthorized", call.Outcome);
        Assert.Equal(0, test.Data.Claims); Assert.Equal(0, test.Data.RuntimeCalls); Assert.Empty(test.Data.Finishes);
    }

    [Fact]
    public async Task A_credential_that_expires_during_execution_releases_the_attempt_and_delivers_nothing()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromSeconds(5));
        test.Data.BlockAtRuntime = true;
        var timer = Stopwatch.StartNew();
        var call = await test.CallAsync(new MemoryStream(test.Body));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "The credential's expiry, not the deadline, ends the execution.");
        Assert.Equal(401, call.Status);
        Assert.Equal(1, test.Data.RuntimeCalls);
        // The attempt is released without an outcome, so the same request ID can be retried with a valid credential.
        var released = Assert.Single(test.Data.Finishes);
        Assert.Equal(MachineInvocationOutcome.Unavailable, released.Outcome); Assert.Null(released.Response);
        test.Data.BlockAtRuntime = false;
        test.Data.Caller = new(test.Data.Caller.Principal, test.Data.Caller.ApplicationId, DateTimeOffset.UtcNow.AddHours(1));
        var retry = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(200, retry.Status); Assert.Equal(2, test.Data.RuntimeCalls);
    }

    [Fact]
    public async Task A_call_through_the_routed_pipeline_completes_and_its_retry_replays_the_same_bytes()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromHours(1));
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services => services.AddRouting().AddSingleton(test.Routes))
                .Configure(MappingEndpointHost.Compose))
            .Build();
        await host.StartAsync();
        try
        {
            using var client = host.GetTestClient();
            async Task<(HttpResponseMessage Response, byte[] Body)> CallAsync()
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, MappingEndpointRoutes.InvocationsPath) { Content = new ByteArrayContent(test.Body) };
                message.Content.Headers.ContentType = new("application/json");
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer fixture");
                message.Headers.Add(MappingEndpointRoutes.RequestIdHeader, test.Data.RequestId.ToString("D"));
                var response = await client.SendAsync(message);
                return (response, await response.Content.ReadAsByteArrayAsync());
            }
            var (first, completed) = await CallAsync();
            Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("false", Assert.Single(first.Headers.GetValues(MappingEndpointRoutes.ReplayedHeader)));
            Assert.Equal(test.Data.RequestId, JsonSerializer.Deserialize<MappingInvocationResponseV1>(completed, MappingEndpointContract.Json)!.RequestId);
            var (retry, replayed) = await CallAsync();
            Assert.Equal(System.Net.HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal("true", Assert.Single(retry.Headers.GetValues(MappingEndpointRoutes.ReplayedHeader)));
            Assert.Equal(completed, replayed);
            Assert.Equal(1, test.Data.RuntimeCalls);
            first.Dispose();
            retry.Dispose();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_closed_runtime_fails_readiness_while_a_recorded_result_still_replays()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromHours(1));
        Assert.Equal(204, await test.ReadyAsync());
        Assert.Equal(200, (await test.CallAsync(new MemoryStream(test.Body))).Status);
        test.RuntimeAvailable = () => false;
        Assert.Equal(503, await test.ReadyAsync());
        // Readiness alone reports the closed runtime; a retry of a recorded call needs no execution.
        var replay = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(200, replay.Status);
        Assert.Equal("true", replay.Context.Response.Headers[MappingEndpointRoutes.ReplayedHeader].ToString());
        Assert.Equal(1, test.Data.RuntimeCalls);
    }

    [MappingEndpointLinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_stalled_service_execution_settles_within_the_call_deadline_as_an_unknown_outcome()
    {
        // Shortened clocks: the work bound cancels the call at 2 s and its deadline is 4 s.
        var work = TimeSpan.FromSeconds(2);
        var deadline = TimeSpan.FromSeconds(4);
        await using var service = await FerruleServiceTransportTests.ServiceCase.StartAsync(execute: true);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The service takes the frame and then never answers, not even its cancellation.
        service.Runtime.Behavior = async (_, _) =>
        {
            delivered.TrySetResult();
            await release.Task;
            return new WorkerProcessResult([], WorkerProcessFailure.Canceled);
        };
        var root = FerruleServiceTestSupport.TemporaryRoot();
        MappingRuntimeSelection.Owned? owned = null;
        try
        {
            owned = HostedCompiledSettingsTests.ServiceSelection(service, root).CreateRunner(MappingEndpointRoutes.Settlement(work, deadline));
            var runner = owned.Runner;
            var test = await Harness.StartAsync(work, deadline, expiresIn: TimeSpan.FromHours(1));
            test.RuntimeAvailable = () => owned.Available;
            test.Data.Execute = async token =>
            {
                // As the compiled runtime does: a cancellation propagates and any failure is unavailable.
                var reply = await runner.RunAsync(new byte[] { 1 }, token);
                token.ThrowIfCancellationRequested();
                return new(reply.Failure == WorkerProcessFailure.None ? CompiledBuildOutcome.Completed : CompiledBuildOutcome.Unavailable);
            };
            Assert.Equal(204, await test.ReadyAsync());
            var timer = Stopwatch.StartNew();
            var call = await test.CallAsync(new MemoryStream(test.Body));
            var held = timer.Elapsed;
            Assert.True(delivered.Task.IsCompleted);
            Assert.True(held < deadline + TimeSpan.FromSeconds(1), $"The stalled call held its slot for {held}.");
            Assert.Equal(503, call.Status); Assert.Equal("deadline", call.Outcome);

            // The outcome is unknown: the runner closes, readiness fails and the attempt is released for a retry.
            Assert.False(owned.Available);
            Assert.Equal(503, await test.ReadyAsync());
            Assert.Equal(MachineInvocationOutcome.Unavailable, Assert.Single(test.Data.Finishes).Outcome);
            Assert.Single(service.Runtime.Frames);
            // The slot is free again, and the closed runner delivers no second frame.
            var retry = await test.CallAsync(new MemoryStream(test.Body));
            Assert.Equal(503, retry.Status); Assert.Equal("unavailable", retry.Outcome);
            Assert.Single(service.Runtime.Frames);
        }
        finally
        {
            release.TrySetResult();
            if (owned is not null) await Assert.ThrowsAsync<InvalidOperationException>(async () => await owned.DisposeAsync());
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_expired_credential_is_refused_before_any_claim()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromSeconds(-1));
        var call = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(401, call.Status); Assert.Equal(0, test.Data.Claims);
    }

    [Fact]
    public async Task A_credential_that_outlives_the_deadline_runs_under_the_call_bounds_only()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromDays(36500));
        var call = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(200, call.Status); Assert.Equal(1, test.Data.RuntimeCalls);
    }

    [Fact]
    public async Task A_client_that_stops_reading_loses_the_connection_at_the_deadline_and_the_slot_is_admitted_again()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), expiresIn: TimeSpan.FromHours(1));
        var timer = Stopwatch.StartNew();
        var blocked = await test.CallAsync(new MemoryStream(test.Body), response: new BlockedResponse());
        Assert.True(blocked.Lifetime.Aborted, "A response the client does not take within the deadline ends the connection.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(40));
        Assert.Equal(1, test.Data.RuntimeCalls);
        // Concurrency is one: the slot came back, and the recorded response replays without running again.
        var next = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(200, next.Status); Assert.Equal("true", next.Context.Response.Headers[MappingEndpointRoutes.ReplayedHeader].ToString());
        Assert.Equal(1, test.Data.RuntimeCalls);
    }

    [Fact]
    public async Task Credential_expiry_stops_a_blocked_response_and_the_recorded_result_is_recoverable()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60), expiresIn: TimeSpan.FromSeconds(2));
        var timer = Stopwatch.StartNew();
        var blocked = await test.CallAsync(new MemoryStream(test.Body), response: new BlockedResponse());
        Assert.True(blocked.Lifetime.Aborted);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "Credential expiry must stop delivery before the call deadline.");
        Assert.Equal(1, test.Data.RuntimeCalls);
        test.Data.Caller = new(test.Data.Caller.Principal, test.Data.Caller.ApplicationId, DateTimeOffset.UtcNow.AddHours(1));
        var recovered = await test.CallAsync(new MemoryStream(test.Body));
        Assert.Equal(200, recovered.Status);
        Assert.Equal("true", recovered.Context.Response.Headers[MappingEndpointRoutes.ReplayedHeader].ToString());
        Assert.Equal(1, test.Data.RuntimeCalls);
    }

    [Fact]
    public async Task Error_outcomes_written_to_a_client_that_stops_reading_also_end_at_the_deadline()
    {
        var test = await Harness.StartAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), expiresIn: TimeSpan.FromHours(1));
        var refused = await test.CallAsync(new MemoryStream("{}"u8.ToArray()), response: new BlockedResponse());
        Assert.True(refused.Lifetime.Aborted);
        Assert.Equal(400, (await test.CallAsync(new MemoryStream("{}"u8.ToArray()))).Status);
    }

    private sealed record Call(DefaultHttpContext Context, Lifetime Lifetime, int Status, string? Outcome);

    private sealed class Harness
    {
        private readonly MappingEndpointRoutes routes;

        private Harness(MachineInvocationServiceTests.Fixture data, TimeSpan work, TimeSpan deadline)
        {
            Data = data;
            routes = new(new(data.Target.Reference, 1, new Validator(data), data.Service, TimeProvider.System, work, deadline, () => RuntimeAvailable()));
            routes.MarkReady();
            var replay = data.Data.Case.Replay;
            Body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                inputs = new[] { new { name = (string?)null, content = Convert.ToBase64String(replay.Primary.Bytes) } }
                    .Concat(replay.NamedInputs.Select(input => new { name = input.Name, content = Convert.ToBase64String(input.Bytes) })),
                parameters = replay.Parameters.Select(parameter => new { name = parameter.Name, kind = parameter.Kind.ToString().ToLowerInvariant(), value = parameter.Value })
            });
        }

        internal MachineInvocationServiceTests.Fixture Data { get; }
        internal MappingEndpointRoutes Routes => routes;
        internal byte[] Body { get; }
        internal Func<bool> RuntimeAvailable { get; set; } = () => true;

        internal static async Task<Harness> StartAsync(TimeSpan work, TimeSpan deadline, TimeSpan expiresIn)
        {
            var data = new MachineInvocationServiceTests.Fixture();
            await data.Activate();
            data.Caller = new(data.Caller.Principal, data.Caller.ApplicationId, DateTimeOffset.UtcNow + expiresIn);
            return new(data, work, deadline);
        }

        internal async Task<int> ReadyAsync()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = MappingEndpointRoutes.ReadyPath;
            await routes.ReadyAsync(context);
            return context.Response.StatusCode;
        }

        internal async Task<Call> CallAsync(Stream body, Stream? response = null)
        {
            var lifetime = new Lifetime();
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
            context.Request.Method = "POST";
            context.Request.Path = MappingEndpointRoutes.InvocationsPath;
            context.Request.Headers.Authorization = "Bearer fixture";
            context.Request.Headers[MappingEndpointRoutes.RequestIdHeader] = Data.RequestId.ToString("D");
            context.Request.ContentType = "application/json";
            context.Request.Body = body;
            var captured = new MemoryStream();
            context.Response.Body = response ?? captured;
            using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await routes.InvokeAsync(context).WaitAsync(bound.Token);
            string? outcome = null;
            if (response is null && context.Response.StatusCode != 200 && captured.Length > 0)
                using (var document = JsonDocument.Parse(captured.ToArray()))
                    outcome = document.RootElement.GetProperty("outcome").GetString();
            return new(context, lifetime, context.Response.StatusCode, outcome);
        }
    }

    private sealed class Validator(MachineInvocationServiceTests.Fixture data) : IMachineTokenValidator
    {
        public Task<MachineCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken) => Task.FromResult(data.Caller);
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource aborted = new();
        internal bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get => aborted.Token; set { } }
        public void Abort() { Aborted = true; aborted.Cancel(); }
    }

    /// <summary>Sends part of the body, then withholds the rest until the reader gives up.</summary>
    private sealed class WithheldBody(byte[] prefix) : Stream
    {
        private int sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - sent);
                prefix.AsMemory(sent, count).CopyTo(buffer);
                sent += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A client that stops reading: every write waits until it is cancelled.</summary>
    private sealed class BlockedResponse : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
