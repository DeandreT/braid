using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Weft.Application;
using Weft.Deployment;
using Weft.MachineEndpoint;

namespace Weft.Tests;

/// <summary>The call bounds of the HTTP contract: the credential's expiry, the work bound and the whole-call deadline.</summary>
public sealed class MappingEndpointRouteTests
{
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
            routes = new(new(data.Target.Reference, 1, new Validator(data), data.Service, TimeProvider.System, work, deadline));
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
        internal byte[] Body { get; }

        internal static async Task<Harness> StartAsync(TimeSpan work, TimeSpan deadline, TimeSpan expiresIn)
        {
            var data = new MachineInvocationServiceTests.Fixture();
            await data.Activate();
            data.Caller = new(data.Caller.Principal, data.Caller.ApplicationId, DateTimeOffset.UtcNow + expiresIn);
            return new(data, work, deadline);
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
            await routes.HandleAsync(context).WaitAsync(bound.Token);
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
