using System.Collections.Immutable;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Weft.Ai;

namespace Weft.Tests;

public sealed partial class AiProviderTests
{
    private const string Token = "PRIVATE-fixture-bearer";
    private const string Model = "PRIVATE-fixture-model";
    private static readonly AiProfileKey Key = AiProfileKey.Create("fixture", 1);
    private static string Path(AiDialect dialect) => dialect == AiDialect.ChatCompletionsSseV1 ? "/v1/chat/completions" : "/api/chat";
    private static string Media(AiDialect dialect) => dialect == AiDialect.ChatCompletionsSseV1 ? "text/event-stream" : "application/x-ndjson";
    private static byte[] StopWire(AiDialect dialect) => AiHttpFixture.Utf8(dialect == AiDialect.ChatCompletionsSseV1
        ? "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
        : "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":false}\n{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n");
    private static AiProfileSpec Profile(AiHttpFixture server, AiDialect dialect, AiCapability capabilities = AiCapability.Text | AiCapability.FunctionTools,
        bool mapping = true, bool bearer = false, bool thinkFalse = false) => AiProfileSpec.Create(Key, dialect, Model,
        server.Endpoint(Path(dialect)), true, bearer ? AiAuthentication.Bearer : AiAuthentication.None,
        bearer ? "fixture.credential" : null, capabilities, "fixture.metadata", 1, mapping, thinkFalse);
    private static AiRoundClient Client(AiProfileSpec profile, Resolver resolver, TimeProvider? clock = null) =>
        new(AiProfileCatalog.Create([profile]), resolver, clock ?? TimeProvider.System);
    private static AiRoundRequest Request(Guid? id = null, IEnumerable<AiMessage>? messages = null, IEnumerable<AiToolDefinition>? tools = null) =>
        AiRoundRequest.Create(id ?? Guid.NewGuid(), messages ?? [AiUserMessage.Create("PRIVATE fixture question")], tools ?? [], 128);
    private static AiToolDefinition Tool(string name = "read_mapping") => AiToolDefinition.Create(name, "Read exact metadata",
        AiJsonObject.CopyFrom("{\"type\":\"object\",\"properties\":{\"revision\":{\"type\":\"string\"}},\"additionalProperties\":false}"));
    private static T Require<T>(object value) where T : class
    { Assert.True(value is T, "Unexpected closed AI outcome type."); return (T)value; }
    private static AiTurnLease Turn(AiRoundClient client, AiTurnLimits? limits = null, CancellationToken ct = default) =>
        Require<TurnOpened>(client.OpenTurn(Key, limits ?? AiTurnLimits.Default, ct)).Turn;
    private static async Task<RoundOpened> Open(AiRoundClient client, AiTurnLease turn, AiRoundRequest? request = null,
        CancellationToken ct = default) => Require<RoundOpened>(await client.OpenRoundAsync(turn, request ?? Request(), ct));
    private static async Task<List<AiRoundEvent>> Read(AiRoundStream stream, CancellationToken ct = default)
    {
        var values = new List<AiRoundEvent>();
        await foreach (var value in stream.ReadAllAsync(ct))
        { Assert.True(values.Count < 16385, "Public event bound exceeded."); values.Add(value); }
        return values;
    }
    private static AiRoundFinished Finished(IReadOnlyList<AiRoundEvent> values, AiFinishReason reason)
    {
        Assert.True(values.Count > 0 && values[^1] is AiRoundFinished, "Missing final success event.");
        Assert.True(values.Count(value => value is AiRoundFinished or AiRoundFailed) == 1, "Terminal event cardinality changed.");
        var terminal = (AiRoundFinished)values[^1]; Assert.Equal(reason, terminal.Reason); return terminal;
    }
    private static AiRoundFailed Failed(IReadOnlyList<AiRoundEvent> values, AiFailureCode code, AiDispatchState dispatch = AiDispatchState.Attempted)
    {
        Assert.True(values.Count > 0 && values[^1] is AiRoundFailed, "Missing final failure event.");
        Assert.True(values.Count(value => value is AiRoundFinished or AiRoundFailed) == 1, "Terminal event cardinality changed.");
        Assert.False(values.Any(value => value is AiToolIntentReady), "A failed round released a tool intent.");
        var terminal = (AiRoundFailed)values[^1]; Assert.Equal(code, terminal.Failure.Code); Assert.Equal(dispatch, terminal.Failure.Dispatch); return terminal;
    }
    private static void Same(string expected, string actual, string label) => Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), label);
    private static string Text(IEnumerable<AiRoundEvent> values) => string.Concat(values.OfType<AiTextDelta>().Select(value => value.Text));
    private static AiTurnLimits Limits(TimeSpan? duration = null, int rounds = 4, long requests = 1048576,
        long wire = 4194304, long text = 131072, int tools = 8) => AiTurnLimits.Create(duration ?? TimeSpan.FromSeconds(180), rounds, requests, wire, text, tools);
    private sealed class Resolver(bool available = true, AiBearerSecret? secret = null) : IAiCredentialResolver
    {
        internal int Calls;
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AiCredentialResult>(available
                ? CredentialFound.Create(secret ?? AiBearerSecret.CopyFrom(Token.AsSpan())) : CredentialUnavailable.Instance);
        }
        public override string ToString() => "Fixture credential resolver.";
    }

    [Fact]
    public async Task MetadataAndUnknownProfileDenialsPerformNoCredentialOrNetworkWork()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        var resolver = new Resolver();
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, mapping: false, bearer: true), resolver);
        var denied = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None));
        Assert.Equal(AiFailureCode.MetadataDisallowed, denied.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, denied.Failure.Dispatch);
        var unknown = Require<TurnRejected>(client.OpenTurn(AiProfileKey.Create("absent", 1), AiTurnLimits.Default, CancellationToken.None));
        Assert.Equal(AiFailureCode.ProfileUnavailable, unknown.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, unknown.Failure.Dispatch);
        Assert.Equal(0, resolver.Calls); Assert.Equal(0, server.ConnectionCount); Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task MissingCredentialAndUnadvertisedToolsRefuseBeforeAnyConnection()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        var missing = new Resolver(false);
        await using (var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), missing))
        {
            await using var turn = Turn(client);
            var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
            Assert.Equal(AiFailureCode.CredentialUnavailable, rejected.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
        }
        var resolver = new Resolver();
        await using (var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, capabilities: AiCapability.Text, bearer: true), resolver))
        {
            await using var turn = Turn(client);
            var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(tools: [Tool()]), CancellationToken.None));
            Assert.Equal(AiFailureCode.CapabilityUnavailable, rejected.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
        }
        Assert.Equal(1, missing.Calls); Assert.Equal(0, resolver.Calls); Assert.Equal(0, server.ConnectionCount);
    }

    [Theory]
    [InlineData("http-remote")]
    [InlineData("userinfo")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("hostname-loopback")]
    public void EndpointFactoriesRejectUnadmittedAddressShapes(string shape)
    {
        var endpoint = shape switch
        {
            "http-remote" => new Uri("http://192.0.2.1/v1/chat/completions"),
            "userinfo" => new Uri("https://fixture:private@example.invalid/v1/chat/completions"),
            "query" => new Uri("https://example.invalid/v1/chat/completions?private=value"),
            "fragment" => new Uri("https://example.invalid/v1/chat/completions#private"),
            _ => new Uri("http://localhost:12345/v1/chat/completions")
        };
        Assert.ThrowsAny<ArgumentException>(() => AiProfileSpec.Create(Key, AiDialect.ChatCompletionsSseV1, Model, endpoint, true,
            AiAuthentication.None, null, AiCapability.Text, "fixture.metadata", 1, true, false));
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("wrong-id")]
    [InlineData("wrong-name")]
    [InlineData("duplicate-result")]
    [InlineData("result-before-call")]
    [InlineData("undeclared-historical-tool")]
    [InlineData("assistant-prefill")]
    public async Task InvalidCrossMessageHistoriesCannotResolveCredentialsOrDispatch(string variant)
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        var resolver = new Resolver(); await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), resolver);
        await using var turn = Turn(client);
        var intent = AiToolIntent.Create("PRIVATE-call", "read_mapping", AiJsonObject.CopyFrom("{}"));
        var assistant = AiAssistantMessage.Create("", [intent]);
        var result = AiToolMessage.Create(variant == "wrong-id" ? "PRIVATE-other" : intent.CallId,
            variant == "wrong-name" ? "other_tool" : intent.Name, AiJsonObject.CopyFrom("{\"found\":true}"));
        AiMessage[] messages = variant switch
        {
            "missing-result" => [AiUserMessage.Create("question"), assistant, AiUserMessage.Create("next")],
            "duplicate-result" => [AiUserMessage.Create("question"), assistant, result, result],
            "result-before-call" => [AiUserMessage.Create("question"), result, assistant, result],
            "assistant-prefill" => [AiUserMessage.Create("question"), AiAssistantMessage.Create("prefill", [])],
            _ => [AiUserMessage.Create("question"), assistant, result]
        };
        var factoryRefused = false;
        try
        {
            var request = Request(messages: messages, tools: variant == "undeclared-historical-tool" ? [] : [Tool()]);
            var rejection = Require<RoundRejected>(await client.OpenRoundAsync(turn, request, CancellationToken.None));
            Assert.Equal(AiFailureCode.InvalidRequest, rejection.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejection.Failure.Dispatch);
        }
        catch (ArgumentException error)
        {
            factoryRefused = true;
            Assert.False(error.ToString().Contains("PRIVATE", StringComparison.Ordinal), "Factory refusal leaked history values.");
        }
        Assert.True(factoryRefused || resolver.Calls == 0, "History refusal performed credential work.");
        Assert.Equal(0, resolver.Calls); Assert.Equal(0, server.ConnectionCount); Assert.Equal(0, server.RequestCount);
    }

    [Theory]
    [InlineData(AiDialect.ChatCompletionsSseV1)]
    [InlineData(AiDialect.OllamaChatNdjsonV1)]
    public async Task CompletePostThenPeerCloseHasOneEstablishmentAndOneUncertainDispatch(AiDialect dialect)
    {
        await using var server = new AiHttpFixture((exchange, _) => { exchange.CloseWithoutResponse(); return Task.CompletedTask; });
        var resolver = new Resolver(); await using var client = Client(Profile(server, dialect, bearer: true), resolver);
        await using var turn = Turn(client);
        var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
        Assert.Equal(AiFailureCode.TransportFailure, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
        await server.WaitForCompletedAsync(1);
        Assert.Equal(1, resolver.Calls); Assert.Equal(1, server.ConnectionCount); Assert.Equal(1, server.RequestCount);
        Assert.False(server.HasUnexpectedServerFailure, "The owned HTTP peer failed unexpectedly.");
    }

    [Theory]
    [InlineData(301, AiFailureCode.ProviderRejected)]
    [InlineData(307, AiFailureCode.ProviderRejected)]
    [InlineData(401, AiFailureCode.ProviderAuthentication)]
    [InlineData(403, AiFailureCode.ProviderAuthentication)]
    [InlineData(429, AiFailureCode.ProviderRateLimited)]
    [InlineData(500, AiFailureCode.ProviderUnavailable)]
    [InlineData(503, AiFailureCode.ProviderUnavailable)]
    [InlineData(400, AiFailureCode.ProviderRejected)]
    public async Task HttpFailuresAreClosedAndRedirectsNeverForwardBearer(int status, AiFailureCode expected)
    {
        await using var sink = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("application/json", AiHttpFixture.Utf8("{\"error\":\"PRIVATE upstream failure\"}"), ct,
            status: status, extra: "Location: " + sink.Endpoint("/private-target").AbsoluteUri + "\r\n"));
        var resolver = new Resolver(); await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), resolver);
        await using var turn = Turn(client);
        var rejected = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
        Assert.Equal(expected, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
        Assert.False(rejected.Failure.ToString().Contains("PRIVATE", StringComparison.Ordinal), "HTTP failure leaked provider content.");
        Assert.Equal(1, server.RequestCount); Assert.Equal(0, sink.ConnectionCount); Assert.Equal(0, sink.RequestCount);
    }

    [Fact]
    public async Task BearerCopyOwnsItsInputAndHasNoDiagnosticSecretGetter()
    {
        var source = Token.ToCharArray(); using var secret = AiBearerSecret.CopyFrom(source);
        Array.Fill(source, 'x');
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), new Resolver(secret: secret)); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream; Finished(await Read(stream), AiFinishReason.Stop);
        Assert.True(server.Requests[0].HeaderIs("Authorization", "Bearer " + Token), "Bearer input was not defensively copied.");
        Assert.False(secret.ToString().Contains(Token, StringComparison.Ordinal), "Bearer diagnostic leaked secret material.");
    }

    [Fact]
    public async Task NoWaitTurnCapacityReleasesOnOrdinaryDisposal()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver());
        var turns = new List<AiTurnLease>();
        try
        {
            for (var i = 0; i < 32; i++) turns.Add(Turn(client));
            var rejected = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None));
            Assert.Equal(AiFailureCode.Busy, rejected.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
            await turns[0].DisposeAsync(); turns.RemoveAt(0);
            turns.Add(Turn(client)); Assert.Equal(0, server.ConnectionCount);
        }
        finally { foreach (var turn in turns) await turn.DisposeAsync(); }
    }

    [Fact]
    public async Task OneActiveRoundRejectsConcurrentOpenAndStreamHasOneEnumerator()
    {
        var release = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        { await exchange.HeadersAsync("text/event-stream", ct); await release.Task.WaitAsync(ct); await exchange.WriteAsync(StopWire(AiDialect.ChatCompletionsSseV1), ct); });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        try
        {
            var opened = await Open(client, turn); await using var stream = opened.Stream;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
            var enumerator = stream.ReadAllAsync().GetAsyncEnumerator();
            await using (enumerator)
            {
                var pending = enumerator.MoveNextAsync().AsTask();
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                { await foreach (var _ in stream.ReadAllAsync()) { } });
                release.TrySetResult(); Assert.True(await pending, "First enumerator lost its stream.");
                while (await enumerator.MoveNextAsync()) { }
            }
            Assert.Equal(1, server.RequestCount);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationAndDisposalEndOwnedReadWithoutAnotherRequest()
    {
        var headers = AiHttpFixture.NewSignal(); var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        { await exchange.HeadersAsync("text/event-stream", ct); headers.TrySetResult(); await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult(); });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        using var cancellation = new CancellationTokenSource();
        var read = Read(stream, cancellation.Token); await headers.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        var events = await read.WaitAsync(TimeSpan.FromSeconds(5)); Failed(events, AiFailureCode.Cancelled);
        await stream.DisposeAsync(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.ConnectionCount); Assert.Equal(1, server.RequestCount);
        var closedTurn = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
        Assert.Equal(AiDispatchState.NotAttempted, closedTurn.Failure.Dispatch);
    }

    [Fact]
    public async Task PreCancelledTurnNeverTouchesResolverOrNetwork()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        var resolver = new Resolver(); await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), resolver);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var rejected = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, cancellation.Token));
        Assert.Equal(AiFailureCode.Cancelled, rejected.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
        Assert.Equal(0, resolver.Calls); Assert.Equal(0, server.RequestCount); Assert.Equal(0, server.ConnectionCount);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("wire")]
    [InlineData("text")]
    public async Task ReducedBudgetsFailClosedWithoutRetry(string budget)
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        var resolver = new Resolver(); await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true), resolver);
        await using var turn = Turn(client, Limits(requests: budget == "request" ? 1 : 1048576,
            wire: budget == "wire" ? 1 : 4194304, text: budget == "text" ? 1 : 131072));
        var opened = await client.OpenRoundAsync(turn, Request(), CancellationToken.None);
        if (budget == "request")
        {
            var rejection = Require<RoundRejected>(opened); Assert.Equal(AiFailureCode.BudgetExceeded, rejection.Failure.Code);
            Assert.Equal(AiDispatchState.NotAttempted, rejection.Failure.Dispatch); Assert.Equal(0, resolver.Calls); Assert.Equal(0, server.ConnectionCount);
        }
        else
        {
            await using var stream = Require<RoundOpened>(opened).Stream;
            Failed(await Read(stream), AiFailureCode.BudgetExceeded); Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        }
    }

    [Fact]
    public async Task LogicalTurnDeadlineEndsAnAlreadyDispatchedHeaderWaitWithoutRetry()
    {
        var clock = new FixtureClock(); var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        { await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult(); });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver(), clock);
        await using var turn = Turn(client, Limits(duration: TimeSpan.FromSeconds(10)));
        var opening = client.OpenRoundAsync(turn, Request(), CancellationToken.None).AsTask();
        await server.WaitForRequestsAsync(1); clock.Advance(TimeSpan.FromSeconds(11));
        var rejected = Require<RoundRejected>(await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AiFailureCode.DeadlineExceeded, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task ConsumerCanStopAfterOnePulledDeltaAndDisposeTheUnfinishedResponse()
    {
        var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.WriteAsync(AiHttpFixture.Utf8(ChatText("first")), ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        await using var reader = stream.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "The first pull did not produce a delta.");
        Same("first", Require<AiTextDelta>(reader.Current).Text, "The first pulled text changed.");
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        // No public terminal is invented on explicit disposal. This is a bounded stopped-consumer
        // scenario, not proof about OS buffering or byte-level network backpressure.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TurnCancellationOrDeadlineClosesUnpulledOrPausedTransportBeforeConsumerCleanup(bool paused, bool deadline)
    {
        var clock = new FixtureClock(); var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            if (paused) await exchange.WriteAsync(AiHttpFixture.Utf8(ChatText("first")), ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver(), clock);
        using var cancellation = new CancellationTokenSource();
        await using var turn = Turn(client, Limits(duration: TimeSpan.FromSeconds(10)), cancellation.Token);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        var idle = new List<AiTurnLease>(); var events = new List<AiRoundEvent>();
        IAsyncEnumerator<AiRoundEvent>? reader = null;
        try
        {
            for (var i = 0; i < 31; i++) idle.Add(Turn(client));
            var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None));
            Assert.Equal(AiFailureCode.Busy, busy.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, busy.Failure.Dispatch);
            if (paused)
            {
                reader = stream.ReadAllAsync().GetAsyncEnumerator();
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "The first pull did not produce a delta.");
                Same("first", Require<AiTextDelta>(reader.Current).Text, "Paused public text changed.");
                events.Add(reader.Current);
            }
            // No MoveNext, stream/turn disposal or client disposal is in flight.
            // Logical expiry/cancellation alone must close the actual HTTP peer.
            if (deadline) clock.Advance(TimeSpan.FromSeconds(11)); else cancellation.Cancel();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForReusableCapacityAsync(client);
            idle.Add(Turn(client));
            var stillBusy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None));
            Assert.Equal(AiFailureCode.Busy, stillBusy.Failure.Code);
            if (reader is null) events.AddRange(await Read(stream).WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                while (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                { Assert.True(events.Count < 16385, "Public event bound exceeded."); events.Add(reader.Current); }
            }
            Failed(events, deadline ? AiFailureCode.DeadlineExceeded : AiFailureCode.Cancelled);
            Same(paused ? "first" : "", Text(events), "Cancellation changed previously consumed text.");
            Assert.Equal(1, server.ConnectionCount); Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            if (reader is not null) await reader.DisposeAsync();
            foreach (var lease in idle) await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelledUnenumeratedTurnCannotLeaveTransportOutsideClientDisposalSnapshot()
    {
        var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        { await exchange.HeadersAsync("text/event-stream", ct); await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult(); });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver());
        using var cancellation = new CancellationTokenSource(); await using var turn = Turn(client, ct: cancellation.Token);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        cancellation.Cancel();
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // Neither stream nor turn has been explicitly disposed or enumerated.
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task ExplicitTurnDisposalStillDisposesItsCancelledUnenumeratedStream()
    {
        var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        { await exchange.HeadersAsync("text/event-stream", ct); await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult(); });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver());
        using var cancellation = new CancellationTokenSource(); await using var turn = Turn(client, ct: cancellation.Token);
        var opened = await Open(client, turn); await using var stream = opened.Stream;
        cancellation.Cancel(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await turn.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => stream.ReadAllAsync());
        Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundOrReaderCancellationClosesPausedTransportWithoutAnotherPull(bool readerCancellation)
    {
        var closed = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.WriteAsync(AiHttpFixture.Utf8(ChatText("first")), ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver()); await using var turn = Turn(client);
        using var cancellation = new CancellationTokenSource();
        var opened = await Open(client, turn, ct: readerCancellation ? CancellationToken.None : cancellation.Token);
        await using var stream = opened.Stream;
        await using var reader = stream.ReadAllAsync(readerCancellation ? cancellation.Token : CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "The first pull did not produce a delta.");
        var events = new List<AiRoundEvent> { reader.Current }; Require<AiTextDelta>(reader.Current);
        cancellation.Cancel();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
        { Assert.True(events.Count < 16385, "Public event bound exceeded."); events.Add(reader.Current); }
        Failed(events, AiFailureCode.Cancelled); Same("first", Text(events), "Cancellation changed previously consumed text.");
        Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task RoundCancellationAtAttachmentCannotPublishCleanedOwnershipOrLeakItsCapacity()
    {
        var headers = AiHttpFixture.NewSignal(); var closed = AiHttpFixture.NewSignal();
        var gate = new LifetimeGate(); var clock = new LifetimeClock();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await headers.Task.WaitAsync(ct); await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver(), clock);
        await using var turn = Turn(client); using var cancellation = new CancellationTokenSource();
        var idle = new List<AiTurnLease>();
        try
        {
            for (var i = 0; i < 31; i++) idle.Add(Turn(client));
            var opening = client.OpenRoundAsync(turn, Request(), cancellation.Token).AsTask();
            await server.WaitForRequestsAsync(1);
            // The first supplied-clock timestamp after these owned response
            // headers is the attachment check, before active ownership exists.
            clock.AtNextTimestamp(() => { cancellation.Cancel(); gate.Hold(); });
            headers.TrySetResult(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None));
            Assert.Equal(AiFailureCode.Busy, busy.Failure.Code);
            Assert.False(opening.IsCompleted, "Opening escaped the owned attachment gate.");
            gate.Release.TrySetResult();
            var rejected = Require<RoundRejected>(await opening.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(AiFailureCode.Cancelled, rejected.Failure.Code); Assert.Equal(AiDispatchState.Attempted, rejected.Failure.Dispatch);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Socket closure precedes completion of the turn's owned cancellation cleanup.
            await turn.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            idle.Add(Turn(client));
            Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            headers.TrySetResult(); gate.Release.TrySetResult();
            foreach (var lease in idle) await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task TurnAndClientDisposalJoinTheOriginalOpeningWhileItsHeaderTimerCleanupIsGated()
    {
        var headers = AiHttpFixture.NewSignal(); var closed = AiHttpFixture.NewSignal();
        var gate = new LifetimeGate(); var clock = new LifetimeClock();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await headers.Task.WaitAsync(ct); await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver(), clock); await using var turn = Turn(client);
        try
        {
            var opening = client.OpenRoundAsync(turn, Request(), CancellationToken.None).AsTask();
            await server.WaitForRequestsAsync(1); clock.GateHeaderDisposal(gate); headers.TrySetResult();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var turnDisposal = turn.DisposeAsync().AsTask(); var clientDisposal = client.DisposeAsync().AsTask();
            Assert.Same(turnDisposal, turn.DisposeAsync().AsTask()); Assert.Same(clientDisposal, client.DisposeAsync().AsTask());
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(opening.IsCompleted, "Opening escaped before original header cleanup completed.");
            Assert.False(turnDisposal.IsCompleted, "Turn disposal abandoned original opening cleanup.");
            Assert.False(clientDisposal.IsCompleted, "Client disposal abandoned original opening cleanup.");
            gate.Release.TrySetResult();
            var outcome = await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(outcome is RoundOpened or RoundRejected, "Opening changed its closed outcome family.");
            await Task.WhenAll(turnDisposal, clientDisposal).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(opening.IsCompletedSuccessfully, "Parent shutdown did not join the original opening.");
            Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        }
        finally { headers.TrySetResult(); gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ConcurrentStreamTurnAndClientDisposalsShareAndJoinOriginalCancellationCleanup()
    {
        var closed = AiHttpFixture.NewSignal(); var gate = new LifetimeGate();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        { await exchange.HeadersAsync("text/event-stream", ct); await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult(); });
        using var resolver = new LifetimeResolver(gate);
        await using var client = new AiRoundClient(AiProfileCatalog.Create([Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true)]), resolver, TimeProvider.System);
        await using var turn = Turn(client); var opened = await Open(client, turn); await using var stream = opened.Stream;
        try
        {
            var streamDisposal = stream.DisposeAsync().AsTask();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondStreamDisposal = stream.DisposeAsync().AsTask();
            var turnDisposal = turn.DisposeAsync().AsTask(); var secondTurnDisposal = turn.DisposeAsync().AsTask();
            var clientDisposal = client.DisposeAsync().AsTask(); var secondClientDisposal = client.DisposeAsync().AsTask();
            Assert.Same(streamDisposal, secondStreamDisposal); Assert.Same(turnDisposal, secondTurnDisposal); Assert.Same(clientDisposal, secondClientDisposal);
            Assert.False(streamDisposal.IsCompleted, "Stream disposal abandoned its original cancellation callbacks.");
            Assert.False(turnDisposal.IsCompleted, "Turn disposal did not join stream cleanup.");
            Assert.False(clientDisposal.IsCompleted, "Client disposal did not join turn cleanup.");
            gate.Release.TrySetResult();
            await Task.WhenAll(streamDisposal, secondStreamDisposal, turnDisposal, secondTurnDisposal, clientDisposal, secondClientDisposal).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(streamDisposal, stream.DisposeAsync().AsTask()); Assert.Same(turnDisposal, turn.DisposeAsync().AsTask()); Assert.Same(clientDisposal, client.DisposeAsync().AsTask());
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task FailedOpeningCleanupRetainsOwnershipAndTheOriginalDisposalFailures()
    {
        var headers = AiHttpFixture.NewSignal(); var closed = AiHttpFixture.NewSignal();
        var gate = new LifetimeGate(); var clock = new LifetimeClock();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await headers.Task.WaitAsync(ct); await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver(), clock); var turn = Turn(client);
        var idle = new List<AiTurnLease>();
        try
        {
            for (var i = 0; i < 31; i++) idle.Add(Turn(client));
            var opening = client.OpenRoundAsync(turn, Request(), CancellationToken.None).AsTask();
            await server.WaitForRequestsAsync(1); clock.GateHeaderDisposal(gate, fail: true); headers.TrySetResult();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); gate.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(failure.InnerException); Assert.False(failure.ToString().Contains("PRIVATE", StringComparison.Ordinal), "Cleanup exposed a private failure.");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None)); Assert.Equal(AiFailureCode.Busy, busy.Failure.Code);
            var turnDisposal = turn.DisposeAsync().AsTask(); Assert.Same(turnDisposal, turn.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await turnDisposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(turnDisposal, turn.DisposeAsync().AsTask());
            var clientDisposal = client.DisposeAsync().AsTask(); Assert.Same(clientDisposal, client.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await clientDisposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(clientDisposal, client.DisposeAsync().AsTask());
            Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            headers.TrySetResult(); gate.Release.TrySetResult();
            foreach (var lease in idle) await lease.DisposeAsync();
            try { await client.DisposeAsync(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task RacingOpenTurnCancellationDoesNotHoldTheClientGateDuringInitializationOrRejectedCleanup()
    {
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        var clock = new LifetimeClock(); var initialization = new LifetimeGate(); var rejectedCleanup = new LifetimeGate();
        await using var client = Client(Profile(server, AiDialect.ChatCompletionsSseV1), new Resolver(), clock);
        using var cancellation = new CancellationTokenSource();
        AiTurnLease? probe = null, cleanupProbe = null; var callbackEntered = AiHttpFixture.NewSignal(); var callbackFinished = AiHttpFixture.NewSignal();
        using var registration = cancellation.Token.Register(() =>
        {
            callbackEntered.TrySetResult();
            probe = Turn(client); // A different thread must be able to enter the owner gate.
            callbackFinished.TrySetResult();
        });
        clock.AtNextTimestamp(initialization.Hold);
        Task<AiTurnOpenResult>? opening = null; Task? cancelling = null;
        try
        {
            opening = Task.Run(() => client.OpenTurn(Key, AiTurnLimits.Default, cancellation.Token));
            await initialization.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelling = Task.Run(cancellation.Cancel);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(opening.IsCompleted, "The owned initialization gate was bypassed.");
            if (probe is not null) await probe.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            clock.GateTurnDisposal(rejectedCleanup);
            initialization.Release.TrySetResult();
            await rejectedCleanup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cleanupProbe = Require<TurnOpened>(await Task.Run(() => client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5))).Turn;
            Assert.False(opening.IsCompleted, "Rejected cleanup escaped its owned timer gate.");
            rejectedCleanup.Release.TrySetResult();
            var rejected = Require<TurnRejected>(await opening.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(AiFailureCode.Cancelled, rejected.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
            await cleanupProbe.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, server.ConnectionCount); Assert.Equal(0, server.RequestCount);
        }
        finally
        {
            initialization.Release.TrySetResult(); rejectedCleanup.Release.TrySetResult();
            if (cancelling is not null) await cancelling.WaitAsync(TimeSpan.FromSeconds(5));
            if (opening is not null) await opening.WaitAsync(TimeSpan.FromSeconds(5));
            if (probe is not null) await probe.DisposeAsync();
            if (cleanupProbe is not null) await cleanupProbe.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("round")]
    [InlineData("reader")]
    [InlineData("turn")]
    [InlineData("deadline")]
    [InlineData("idle")]
    public async Task PreexistingCancellationCallbacksAreJoinedByEveryLaterDisposalPath(string origin)
    {
        var closed = AiHttpFixture.NewSignal(); var gate = new LifetimeGate(); var clock = new CancellationClock();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct);
            if (origin != "idle") await exchange.WriteAsync(AiHttpFixture.Utf8(ChatText("first")), ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        using var resolver = new CancellationResolver(gate);
        await using var client = new AiRoundClient(AiProfileCatalog.Create([Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true)]), resolver, clock);
        using var turnCancellation = new CancellationTokenSource(); using var roundCancellation = new CancellationTokenSource(); using var readerCancellation = new CancellationTokenSource();
        await using var turn = Turn(client, Limits(duration: TimeSpan.FromSeconds(60)), turnCancellation.Token);
        var opened = await Open(client, turn, ct: roundCancellation.Token); await using var stream = opened.Stream;
        IAsyncEnumerator<AiRoundEvent>? reader = null; Task<List<AiRoundEvent>>? idleRead = null; Task? trigger = null;
        var events = new List<AiRoundEvent>();
        try
        {
            if (origin == "idle")
            {
                idleRead = Read(stream); await clock.ReadArmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                reader = stream.ReadAllAsync(readerCancellation.Token).GetAsyncEnumerator();
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "Leading text was absent.");
                events.Add(reader.Current); Require<AiTextDelta>(reader.Current);
            }
            trigger = origin switch
            {
                "round" => Task.Run(roundCancellation.Cancel),
                "reader" => Task.Run(readerCancellation.Cancel),
                "turn" => Task.Run(turnCancellation.Cancel),
                "deadline" => Task.Run(() => clock.Advance(TimeSpan.FromSeconds(61))),
                _ => Task.Run(() => clock.Advance(TimeSpan.FromSeconds(31)))
            };
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Cancellation started first and is still executing resolver work.
            // A second CancelAsync would not necessarily join this operation.
            Assert.False(trigger.IsCompleted, "The original callback gate was bypassed.");
            var streamDisposal = stream.DisposeAsync().AsTask(); var turnDisposal = turn.DisposeAsync().AsTask(); var clientDisposal = client.DisposeAsync().AsTask();
            Assert.Same(streamDisposal, stream.DisposeAsync().AsTask()); Assert.Same(turnDisposal, turn.DisposeAsync().AsTask()); Assert.Same(clientDisposal, client.DisposeAsync().AsTask());
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(streamDisposal.IsCompleted, "Stream disposal abandoned preexisting cancellation callbacks.");
            Assert.False(turnDisposal.IsCompleted, "Turn disposal abandoned preexisting cancellation callbacks.");
            Assert.False(clientDisposal.IsCompleted, "Client disposal abandoned preexisting cancellation callbacks.");
            gate.Release.TrySetResult();
            await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(streamDisposal, turnDisposal, clientDisposal).WaitAsync(TimeSpan.FromSeconds(5));
            if (idleRead is not null) events.AddRange(await idleRead.WaitAsync(TimeSpan.FromSeconds(5)));
            else if (reader is not null)
                while (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                { Assert.True(events.Count < 16385, "Public event bound exceeded."); events.Add(reader.Current); }
            Failed(events, origin is "deadline" or "idle" ? AiFailureCode.DeadlineExceeded : AiFailureCode.Cancelled);
            Same(origin == "idle" ? "" : "first", Text(events), "Cancellation changed previously consumed text.");
            Assert.Equal(1, resolver.Calls); Assert.Equal(1, resolver.Callbacks); Assert.Equal(1, server.ConnectionCount); Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (trigger is not null) await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            if (reader is not null) await reader.DisposeAsync();
            if (idleRead is not null) await idleRead.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbackFailureIsRetainedForReaderAndUpstreamOrigins(bool readerOrigin)
    {
        var closed = AiHttpFixture.NewSignal(); var gate = new LifetimeGate();
        await using var server = new AiHttpFixture(async (exchange, ct) =>
        {
            await exchange.HeadersAsync("text/event-stream", ct); await exchange.WriteAsync(AiHttpFixture.Utf8(ChatText("first")), ct);
            await exchange.ObserveClientCloseAsync(ct); closed.TrySetResult();
        });
        using var resolver = new CancellationResolver(gate, fail: true);
        var client = new AiRoundClient(AiProfileCatalog.Create([Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true)]), resolver, TimeProvider.System);
        var turn = Turn(client); using var cancellation = new CancellationTokenSource();
        var opened = await Open(client, turn, ct: readerOrigin ? CancellationToken.None : cancellation.Token); var stream = opened.Stream;
        var reader = stream.ReadAllAsync(readerOrigin ? cancellation.Token : CancellationToken.None).GetAsyncEnumerator();
        var idle = new List<AiTurnLease>(); Task? trigger = null;
        try
        {
            for (var i = 0; i < 31; i++) idle.Add(Turn(client));
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), "Leading text was absent.");
            trigger = Task.Run(cancellation.Cancel); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = stream.DisposeAsync().AsTask(); Assert.Same(disposal, stream.DisposeAsync().AsTask());
            Assert.False(disposal.IsCompleted, "Disposal ignored the gated cancellation callback.");
            gate.Release.TrySetResult(); await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(error.InnerException); Assert.False(error.ToString().Contains("PRIVATE", StringComparison.Ordinal), "Cancellation failure exposed private details.");
            Assert.Same(disposal, stream.DisposeAsync().AsTask());
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None)); Assert.Equal(AiFailureCode.Busy, busy.Failure.Code);
            var retry = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None));
            Assert.Equal(AiDispatchState.NotAttempted, retry.Failure.Dispatch);
            var turnDisposal = turn.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await turnDisposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(turnDisposal, turn.DisposeAsync().AsTask());
            var clientDisposal = client.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await clientDisposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(clientDisposal, client.DisposeAsync().AsTask());
            Assert.Equal(1, resolver.Calls); Assert.Equal(1, resolver.Callbacks); Assert.Equal(1, server.ConnectionCount); Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (trigger is not null) await trigger.WaitAsync(TimeSpan.FromSeconds(5));
            await reader.DisposeAsync();
            foreach (var lease in idle) await lease.DisposeAsync();
            try { await client.DisposeAsync(); } catch (InvalidOperationException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryStopKeepsCapacityThroughRequiredCleanupAndQuarantinesFailure(bool fail)
    {
        var gate = new LifetimeGate(); var stopObserved = AiHttpFixture.NewSignal();
        await using var server = new AiHttpFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", StopWire(AiDialect.ChatCompletionsSseV1), ct));
        using var resolver = new CancellationResolver(gate, fail);
        var client = new AiRoundClient(AiProfileCatalog.Create([Profile(server, AiDialect.ChatCompletionsSseV1, bearer: true)]), resolver, TimeProvider.System);
        var turn = Turn(client); var opened = await Open(client, turn); var stream = opened.Stream;
        var idle = new List<AiTurnLease>(); var events = new List<AiRoundEvent>(); Task? read = null;
        async Task EnumerateAsync()
        {
            await foreach (var value in stream.ReadAllAsync())
            {
                Assert.True(events.Count < 16385, "Public event bound exceeded."); events.Add(value);
                if (value is AiRoundFinished finished)
                { Assert.Equal(AiFinishReason.Stop, finished.Reason); stopObserved.TrySetResult(); }
            }
        }
        try
        {
            for (var i = 0; i < 31; i++) idle.Add(Turn(client));
            read = EnumerateAsync();
            await stopObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.WaitForCompletedAsync(1);
            // Native Stop and real HTTP closure are already observed. No caller
            // cancellation or explicit stream/turn/client disposal caused this.
            Assert.False(read.IsCompleted, "Complete enumeration abandoned required ordinary cleanup.");
            for (var i = 0; i < 3; i++)
            {
                var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None));
                Assert.Equal(AiFailureCode.Busy, busy.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, busy.Failure.Dispatch);
            }
            var cleanup = stream.DisposeAsync().AsTask();
            Assert.Same(cleanup, stream.DisposeAsync().AsTask()); Assert.False(cleanup.IsCompleted, "Cleanup escaped the original resolver gate.");
            gate.Release.TrySetResult();
            if (fail)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Null(error.InnerException); Assert.False(error.ToString().Contains("PRIVATE", StringComparison.Ordinal), "Cleanup exposed private callback details.");
                Assert.Same(cleanup, stream.DisposeAsync().AsTask());
                await read.WaitAsync(TimeSpan.FromSeconds(5));
                var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None)); Assert.Equal(AiFailureCode.Busy, busy.Failure.Code);
                var turnCleanup = turn.DisposeAsync().AsTask();
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await turnCleanup.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Same(turnCleanup, turn.DisposeAsync().AsTask());
                var stillBusy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None)); Assert.Equal(AiFailureCode.Busy, stillBusy.Failure.Code);
                var refused = Require<RoundRejected>(await client.OpenRoundAsync(turn, Request(), CancellationToken.None)); Assert.Equal(AiDispatchState.NotAttempted, refused.Failure.Dispatch);
                var clientCleanup = client.DisposeAsync().AsTask();
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await clientCleanup.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Same(clientCleanup, client.DisposeAsync().AsTask());
            }
            else
            {
                await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
                await read.WaitAsync(TimeSpan.FromSeconds(5));
                idle.Add(Turn(client));
                var busy = Require<TurnRejected>(client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None)); Assert.Equal(AiFailureCode.Busy, busy.Failure.Code);
                await turn.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Finished(events, AiFinishReason.Stop); Same("ok", Text(events), "Ordinary cleanup changed native Stop text.");
            Assert.False(events.Any(value => value is AiToolIntentReady), "Ordinary Stop released a tool.");
            Assert.Equal(1, resolver.Calls); Assert.Equal(1, resolver.Callbacks); Assert.Equal(1, server.RequestCount); Assert.Equal(1, server.ConnectionCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (read is not null) await read.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var lease in idle) await lease.DisposeAsync();
            try { await client.DisposeAsync(); } catch (InvalidOperationException) when (fail) { }
        }
    }

    private static async Task WaitForReusableCapacityAsync(AiRoundClient client)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            bound.Token.ThrowIfCancellationRequested();
            var result = client.OpenTurn(Key, AiTurnLimits.Default, CancellationToken.None);
            if (result is TurnOpened opened)
            {
                await opened.Turn.DisposeAsync().AsTask().WaitAsync(bound.Token); return;
            }
            var rejected = Require<TurnRejected>(result);
            Assert.Equal(AiFailureCode.Busy, rejected.Failure.Code); Assert.Equal(AiDispatchState.NotAttempted, rejected.Failure.Dispatch);
            await Task.Delay(1, bound.Token);
        }
    }

    private sealed class CancellationResolver(LifetimeGate gate, bool fail = false) : IAiCredentialResolver, IDisposable
    {
        private CancellationTokenRegistration registration;
        internal int Calls, Callbacks;
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            registration = ct.Register(() =>
            {
                Interlocked.Increment(ref Callbacks); gate.Hold();
                if (fail) throw new InvalidOperationException("PRIVATE resolver callback failure.");
            });
            return ValueTask.FromResult<AiCredentialResult>(CredentialFound.Create(AiBearerSecret.CopyFrom(Token.AsSpan())));
        }
        public void Dispose() => registration.Dispose();
        public override string ToString() => "Fixture cancellation resolver.";
    }
    private sealed class CancellationClock : TimeProvider
    {
        private readonly FixtureClock inner = new();
        internal TaskCompletionSource ReadArmed { get; } = AiHttpFixture.NewSignal();
        internal void Advance(TimeSpan elapsed) => inner.Advance(elapsed);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new ReadTimer(inner.CreateTimer(callback, state, dueTime, period), ReadArmed);
        private sealed class ReadTimer(ITimer timer, TaskCompletionSource armed) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                bool changed = timer.Change(dueTime, period);
                if (changed && dueTime > TimeSpan.FromSeconds(15) && dueTime <= TimeSpan.FromSeconds(30)) armed.TrySetResult();
                return changed;
            }
            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }

    private sealed class LifetimeGate
    {
        internal TaskCompletionSource Entered { get; } = AiHttpFixture.NewSignal();
        internal TaskCompletionSource Release { get; } = AiHttpFixture.NewSignal();
        internal void Hold()
        { Entered.TrySetResult(); Release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
    }
    private sealed class LifetimeResolver(LifetimeGate gate) : IAiCredentialResolver, IDisposable
    {
        private CancellationTokenRegistration registration;
        public ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
        {
            registration = ct.Register(gate.Hold);
            return ValueTask.FromResult<AiCredentialResult>(CredentialFound.Create(AiBearerSecret.CopyFrom(Token.AsSpan())));
        }
        public void Dispose() => registration.Dispose();
        public override string ToString() => "Fixture lifetime resolver.";
    }
    private sealed class LifetimeClock : TimeProvider
    {
        private readonly FixtureClock inner = new();
        private Action? timestamp;
        private LifetimeGate? headerGate;
        private LifetimeGate? turnDisposalGate;
        private bool failHeaderDispose;
        internal void AtNextTimestamp(Action callback) => Interlocked.Exchange(ref timestamp, callback);
        internal void GateHeaderDisposal(LifetimeGate gate, bool fail = false)
        { failHeaderDispose = fail; Volatile.Write(ref headerGate, gate); }
        internal void GateTurnDisposal(LifetimeGate gate) => Volatile.Write(ref turnDisposalGate, gate);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() { Interlocked.Exchange(ref timestamp, null)?.Invoke(); return inner.GetTimestamp(); }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new LifetimeTimer(this, inner.CreateTimer(callback, state, dueTime, period), dueTime > TimeSpan.FromSeconds(30) && dueTime <= AiRoundClient.HeaderWait);
        private sealed class LifetimeTimer(LifetimeClock owner, ITimer timer, bool header) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose()
            {
                if (header && Volatile.Read(ref owner.headerGate) is { } gate)
                { gate.Hold(); if (owner.failHeaderDispose) throw new InvalidOperationException("PRIVATE fixture cleanup failure."); }
                if (!header && Volatile.Read(ref owner.turnDisposalGate) is { } turnGate) turnGate.Hold();
                timer.Dispose();
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<FixtureTimer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FixtureTimer(this, callback, state);
            lock (gate)
            {
                if (timers.Count >= 128) throw new InvalidOperationException("Fixture clock timer bound exceeded.");
                timers.Add(timer); timer.ChangeCore(dueTime, period);
            }
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            List<(TimerCallback Callback, object? State)> due = [];
            lock (gate)
            {
                if (elapsed < TimeSpan.Zero) throw new ArgumentException("Fixture clock cannot regress.");
                ticks = checked(ticks + elapsed.Ticks);
                foreach (var timer in timers)
                {
                    if (!timer.Disposed && timer.Next <= ticks)
                    {
                        due.Add((timer.Callback, timer.State));
                        timer.Next = timer.Period > 0 ? checked(ticks + timer.Period) : long.MaxValue;
                    }
                }
            }
            foreach (var call in due) call.Callback(call.State);
        }
        private sealed class FixtureTimer(FixtureClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            internal long Next = long.MaxValue;
            internal long Period;
            internal bool Disposed;
            internal void ChangeCore(TimeSpan due, TimeSpan period)
            {
                if (due < Timeout.InfiniteTimeSpan || period < Timeout.InfiniteTimeSpan) throw new ArgumentException("Invalid fixture timer interval.");
                Next = due == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner.ticks + due.Ticks);
                Period = period > TimeSpan.Zero ? period.Ticks : 0;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { if (Disposed) return false; ChangeCore(dueTime, period); return true; } }
            public void Dispose() { lock (owner.gate) { Disposed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public void FactoryLimitsJsonOwnershipAndDiagnosticsRemainCategorical()
    {
        Assert.ThrowsAny<ArgumentException>(() => AiProfileKey.Create("INVALID/private", 1));
        Assert.ThrowsAny<ArgumentException>(() => AiProfileKey.Create("fixture", 0));
        Assert.ThrowsAny<ArgumentException>(() => AiJsonObject.CopyFrom("{\"x\":1,\"x\":2}"));
        Assert.ThrowsAny<ArgumentException>(() => AiJsonObject.CopyFrom("[]"));
        Assert.ThrowsAny<ArgumentException>(() => AiJsonObject.CopyFrom("{\"x\":\"\\ud800\"}"));
        Assert.ThrowsAny<ArgumentException>(() => AiUserMessage.Create("\ud800"));
        Assert.ThrowsAny<ArgumentException>(() => AiUserMessage.Create(new string('x', 65537)));
        Assert.ThrowsAny<ArgumentException>(() => AiRoundRequest.Create(Guid.NewGuid(), Enumerable.Range(0, 65).Select(_ => (AiMessage)AiUserMessage.Create("x")), [], 1));
        Assert.ThrowsAny<ArgumentException>(() => AiBearerSecret.CopyFrom("bad secret"));
        Assert.ThrowsAny<ArgumentException>(() => AiTurnLimits.Create(TimeSpan.FromSeconds(181), 4, 1048576, 4194304, 131072, 8));
        Assert.ThrowsAny<ArgumentException>(() => AiTokenUsage.Create(-1, null, null));
        var tools = new[] { Tool() }; var messages = new AiMessage[] { AiUserMessage.Create("PRIVATE draft") };
        var request = Request(messages: messages, tools: tools); tools[0] = Tool("another_tool"); messages[0] = AiUserMessage.Create("changed");
        Assert.True(request.Tools[0].Name == "read_mapping", "Tool input collection was not owned.");
        Assert.True(Require<AiUserMessage>(request.Messages[0]).Text == "PRIVATE draft", "Message input collection was not owned.");
        foreach (var value in new object[] { request, request.Messages[0], request.Tools[0], request.Tools[0].Parameters,
            AiFailure.Create(AiFailureCode.ProviderRejected, AiDispatchState.Attempted), AiProfileKey.Create("private-profile", 1) })
        {
            var text = value.ToString() ?? "";
            Assert.False(text.Contains("PRIVATE", StringComparison.Ordinal), "AI diagnostic leaked private text.");
            Assert.False(text.Contains("private-profile", StringComparison.Ordinal), "AI diagnostic leaked a profile key.");
            Assert.False(text.Contains("read_mapping", StringComparison.Ordinal), "AI diagnostic leaked a tool name.");
        }
    }
}
