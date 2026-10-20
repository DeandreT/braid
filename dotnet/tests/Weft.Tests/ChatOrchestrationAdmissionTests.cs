using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

public sealed class ChatOrchestrationAdmissionTests
{
    [Fact]
    public async Task Original_exact_reservation_acknowledgement_precedes_the_first_billable_request()
    {
        await using var f = new ChatOrchestrationFixture();
        var ack = f.Gate(); var intent = f.Intent();
        f.Store.Returning = call => call.Method == "reserve" ? ack.Hold() : Task.CompletedTask;
        var execution = f.Begin(intent);
        await Bound(ack.Entered.Task);
        var reservation = Assert.Single(f.Store.Rounds(intent.TurnId));
        Assert.Equal(ChatRoundOutcome.Uncertain, reservation.Outcome);
        Assert.Equal(f.InstanceId, reservation.Intent.InstanceId);
        Assert.Equal(0, f.Server.RequestCount); Assert.Equal(0, f.Credentials.Calls);
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(execution.Completion.IsCompleted); Assert.False(execution.ReleaseSafe);
        ack.Open(); var result = await FinishExecution(execution);
        Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome); Assert.True(execution.ReleaseSafe);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
        var trace = f.Trace.Snapshot;
        Assert.True(trace.Single(m => m.Kind == "ack.reserve").Sequence < trace.Single(m => m.Kind == "wire.request").Sequence);
        using var exact = JsonDocument.Parse(reservation.Intent.ExactRequest);
        var canonical = exact.RootElement;
        Assert.Equal(reservation.Intent.RoundId, canonical.GetProperty("round").GetGuid());
        Assert.Equal(128, canonical.GetProperty("outputTokens").GetInt32());
        Assert.Equal(Instructions, canonical.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal(intent.OriginalRequest, canonical.GetProperty("messages")[1].GetProperty("text").GetString());
        using var sent = f.Server.Requests[0].Json();
        Assert.Equal(intent.OriginalRequest, sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(128, sent.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("conflict")]
    [InlineData("missing-turn")]
    [InlineData("missing-round")]
    [InlineData("round-id")]
    [InlineData("fence")]
    [InlineData("instance")]
    [InlineData("request")]
    [InlineData("version")]
    [InlineData("intent")]
    [InlineData("event-count")]
    [InlineData("event-bytes")]
    [InlineData("ordinal")]
    [InlineData("completed-round")]
    [InlineData("ack-lost")]
    public async Task Nonexact_or_lost_reservation_never_grants_dispatch_even_when_committed_facts_are_readable(string fault)
    {
        await using var f = new ChatOrchestrationFixture(); var intent = f.Intent();
        f.Store.ReserveReply = result =>
        {
            var t = result.Turn!; var r = result.Round!; var i = r.Intent;
            ChatRoundIntent Changed(Guid? round = null, Guid? fence = null, Guid? instance = null, string? request = null) =>
                new(round ?? i.RoundId, fence ?? i.Fence, instance ?? i.InstanceId, request ?? i.ExactRequest);
            return fault switch
            {
                "replay" => result with { Outcome = ChatStoreOutcome.Replayed },
                "conflict" => result with { Outcome = ChatStoreOutcome.Conflict },
                "missing-turn" => result with { Turn = null },
                "missing-round" => result with { Round = null },
                "round-id" => result with { Round = new(Changed(round: Guid.NewGuid()), r.Ordinal, r.Outcome) },
                "fence" => result with { Round = new(Changed(fence: Guid.NewGuid()), r.Ordinal, r.Outcome) },
                "instance" => result with { Round = new(Changed(instance: Guid.NewGuid()), r.Ordinal, r.Outcome) },
                "request" => result with { Round = new(Changed(request: i.ExactRequest + " "), r.Ordinal, r.Outcome) },
                "version" => result with { Turn = Ledger.Copy(t, version: t.Version + 1) },
                "intent" => result with { Turn = Ledger.Copy(t, intent: f.Intent("different admitted intent")) },
                "event-count" => result with { Turn = Ledger.Copy(t, eventCount: t.EventCount + 1) },
                "event-bytes" => result with { Turn = Ledger.Copy(t, eventBytes: t.EventBytes + 1) },
                "ordinal" => result with { Round = new(i, 2, r.Outcome), Turn = Ledger.Copy(t, roundCount: 2) },
                "completed-round" => result with { Round = new(i, r.Ordinal, ChatRoundOutcome.Stopped) },
                _ => throw new InvalidOperationException("PRIVATE reservation acknowledgement lost")
            };
        };
        var execution = f.Begin(intent); var result = await FinishExecution(execution);
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(1, f.Store.Count("reserve"));
        Assert.Equal(0, f.Server.RequestCount); Assert.Equal(0, f.Credentials.Calls);
        Assert.Equal(ChatRoundOutcome.Uncertain, Assert.Single(f.Store.Rounds(intent.TurnId)).Outcome);
        // A public retry of the ORIGINAL intent is duplicate admission, not recovery permission.
        var duplicate = await FinishExecution(f.Begin(intent));
        Assert.Equal(ChatOrchestrationOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(1, f.Store.Count("reserve")); Assert.Equal(0, f.Server.ConnectionCount);
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("conflict")]
    [InlineData("missing")]
    [InlineData("wrong-intent")]
    [InlineData("wrong-version")]
    [InlineData("wrong-events")]
    [InlineData("ack-lost")]
    public async Task Only_exact_fresh_start_facts_admit_provider_work(string fault)
    {
        await using var f = new ChatOrchestrationFixture(); var intent = f.Intent();
        var other = f.Intent("different immutable request");
        f.Store.StartReply = result => fault switch
        {
            "replay" => result with { Outcome = ChatStoreOutcome.Replayed },
            "conflict" => result with { Outcome = ChatStoreOutcome.Conflict },
            "missing" => result with { Facts = null },
            "wrong-intent" => result with { Facts = Ledger.Copy(result.Facts!, intent: other) },
            "wrong-version" => result with { Facts = Ledger.Copy(result.Facts!, version: 2) },
            "wrong-events" => result with { Facts = Ledger.Copy(result.Facts!, eventCount: 2) },
            _ => throw new InvalidOperationException("PRIVATE start acknowledgement lost")
        };
        var execution = f.Begin(intent); var result = await FinishExecution(execution);
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(0, f.Store.Count("reserve")); Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.ConnectionCount);
    }

    [Fact]
    public async Task Concurrent_same_intent_has_one_original_admission_and_one_dispatch_not_two_waiter_owned_runs()
    {
        await using var f = new ChatOrchestrationFixture(); var intent = f.Intent(); var firstAck = f.Gate();
        var first = 0;
        f.Store.Returning = call => call.Method == "start" && Interlocked.Increment(ref first) == 1 ? firstAck.Hold() : Task.CompletedTask;
        var original = f.Begin(intent); await Bound(firstAck.Entered.Task);
        var duplicate = f.Begin(intent); var duplicateResult = await FinishExecution(duplicate);
        Assert.Equal(ChatOrchestrationOutcome.Duplicate, duplicateResult.Outcome);
        Assert.False(original.Completion.IsCompleted); Assert.Equal(0, f.Server.RequestCount);
        firstAck.Open(); Assert.Equal(ChatOrchestrationOutcome.Completed, (await FinishExecution(original)).Outcome);
        Assert.Equal(2, f.Store.Count("start")); Assert.Equal(1, f.Store.Count("reserve"));
        Assert.Equal(1, f.Credentials.Calls); Assert.Equal(1, f.Server.RequestCount);
        Assert.True(original.ReleaseSafe); Assert.True(duplicate.ReleaseSafe);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("policy")]
    [InlineData("turn-version")]
    [InlineData("round-fence")]
    public async Task Revocation_or_changed_facts_after_reservation_ack_are_rechecked_before_dispatch(string boundary)
    {
        await using var f = new ChatOrchestrationFixture();
        f.Store.Returning = call =>
        {
            if (call.Method == "reserve")
            {
                if (boundary == "owner") f.Store.Authorized = false;
                if (boundary == "policy") f.ProfilePolicy.Check = (_, _, _, _, _) => Task.FromResult(false);
                if (boundary == "turn-version") f.Store.TurnRead = t => t is null ? null : Ledger.Copy(t, version: t.Version + 1);
                if (boundary == "round-fence") f.Store.RoundRead = r => r is null ? null : new(new(r.Intent.RoundId, Guid.NewGuid(), r.Intent.InstanceId, r.Intent.ExactRequest), r.Ordinal, r.Outcome);
            }
            return Task.CompletedTask;
        };
        var result = await FinishExecution(f.Begin());
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(1, f.Store.Count("reserve")); Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.ConnectionCount);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("profile-policy")]
    [InlineData("metadata-destination")]
    [InlineData("profile-fingerprint")]
    public async Task Initial_private_authority_and_metadata_destination_refusal_precede_all_provider_and_tool_work(string refusal)
    {
        await using var f = new ChatOrchestrationFixture(metadataAllowed: refusal != "metadata-destination");
        var intent = f.Intent();
        if (refusal == "owner") f.Store.Authorized = false;
        if (refusal == "profile-policy") f.ProfilePolicy.Check = (_, _, _, _, _) => Task.FromResult(false);
        if (refusal == "profile-fingerprint") intent = new(intent.TurnId, intent.Envelope,
            new(intent.Profile.ProfileId, intent.Profile.Version, intent.Profile.ProviderId, intent.Profile.ModelId,
                intent.Profile.MetadataPolicyId, intent.Profile.MetadataPolicyVersion, Sha256Digest.FromBytes([9, 9])), intent.Budget, intent.DeadlineUtc);
        var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Empty(f.Port.Validations); Assert.Empty(f.Port.Executions);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.ConnectionCount);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("complete-round")]
    [InlineData("finish")]
    public async Task Committed_publication_with_lost_ack_is_not_retried_or_used_to_continue(string lostMethod)
    {
        await using var f = new ChatOrchestrationFixture(); var intent = f.Intent();
        var injected = 0;
        f.Store.Returning = call =>
        {
            if (call.Method == lostMethod && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
                throw new InvalidOperationException("PRIVATE committed publication lost acknowledgement");
            return Task.CompletedTask;
        };
        var result = await FinishExecution(f.Begin(intent));
        Assert.Equal(1, injected); Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Store.Count("reserve"));
        Assert.Equal(1, f.Store.Count(lostMethod));
        Assert.InRange(f.Store.Count("finish"), 0, 1);
        var writes = f.Store.Calls.Where(c => c.Method is "append" or "complete-round" or "finish").ToArray();
        var lost = Array.FindIndex(writes, c => c.Method == lostMethod);
        Assert.All(writes.Skip(lost + 1), c => { Assert.Equal("finish", c.Method); Assert.Equal(writes[lost].Version, c.Version); });
        if (lostMethod == "finish") Assert.Null(result.Phase); // Commit is not an acknowledgement.
        var again = await FinishExecution(f.Begin(intent));
        Assert.Equal(ChatOrchestrationOutcome.Duplicate, again.Outcome); Assert.Equal(1, f.Server.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_dispatch_or_output_started_then_broken_transport_is_never_automatically_resent(bool outputStarted)
    {
        var published = AiHttpFixture.NewSignal();
        await using var f = new ChatOrchestrationFixture(async (exchange, ct) =>
        {
            if (!outputStarted) { exchange.CloseWithoutResponse(); return; }
            await exchange.HeadersAsync("text/event-stream", ct);
            await exchange.WriteAsync(AiHttpFixture.Utf8(Text("partial")), ct);
            // Wait for an actual application event, not just a server write.
            await Bound(published.Task);
        });
        f.Store.Returning = call => { if (call.Event?.Kind == ChatEventKind.TextDelta) published.TrySetResult(); return Task.CompletedTask; };
        var intent = f.Intent(); var result = await FinishExecution(f.Begin(intent));
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(1, f.Server.RequestCount); Assert.Equal(1, f.Credentials.Calls); Assert.Equal(1, f.Store.Count("reserve"));
        var failure = f.Store.Events(intent.TurnId).Select(e => e.ProviderFailure).FirstOrDefault(p => p is not null);
        Assert.NotNull(failure); Assert.Equal(ChatDispatchObservation.Attempted, failure.Dispatch);
        Assert.Empty(f.Port.Executions);
        Assert.Equal(ChatOrchestrationOutcome.Duplicate, (await FinishExecution(f.Begin(intent))).Outcome);
        Assert.Equal(1, f.Server.RequestCount);
    }
}
