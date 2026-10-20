using Weft.Application;
using Weft.Domain;
using static Weft.Tests.ChatOrchestrationFixture;

namespace Weft.Tests;

public sealed class ChatOrchestrationLifetimeTests
{
    [Fact]
    public async Task Observer_cancellation_does_not_cancel_the_original_opening_and_actual_cancellation_joins_noncooperative_credentials()
    {
        var opening = new AsyncGate(); var cancelled = AiHttpFixture.NewSignal(); var resolver = new Resolver();
        resolver.Opening = async ct => { using var registration = ct.Register(() => cancelled.TrySetResult()); await opening.Hold(); };
        await using var f = new ChatOrchestrationFixture(credentials: resolver); f.ReleaseWith(opening.Open);
        var execution = f.Begin(); var original = execution.Completion;
        await Bound(opening.Entered.Task); f.FillOtherCapacity(); f.AssertBusy();
        var actualOpening = await Bound(resolver.FirstOriginalOpening.Task);
        Assert.False(actualOpening.IsCompleted);
        using var observer = new CancellationTokenSource(); observer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution.WaitAsync(TimeSpan.FromSeconds(1), observer.Token));
        Assert.False(cancelled.Task.IsCompleted, "An observer token cancelled actual provider work.");
        Assert.Same(original, execution.Completion); Assert.False(original.IsCompleted); Assert.False(execution.ReleaseSafe);
        await Bound(execution.CancelAsync()); await Bound(cancelled.Task);
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(opening.Returned.Task.IsCompleted); Assert.False(original.IsCompleted); f.AssertBusy();
        Assert.False(actualOpening.IsCompleted);
        opening.Open(); await Bound(opening.Returned.Task); var result = await FinishExecution(execution);
        Assert.Equal(ChatOrchestrationOutcome.Cancelled, result.Outcome); Assert.True(execution.ReleaseSafe);
        Assert.Equal(0, f.Server.RequestCount); Assert.Equal(1, resolver.Calls); await f.AssertOneCapacityRecovered();
        Assert.True(actualOpening.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("deadline")]
    [InlineData("caller-token")]
    public async Task Original_tool_task_and_capacity_remain_owned_after_cancellation_request_until_actual_settlement(string trigger)
    {
        await using var f = new ChatOrchestrationFixture((exchange, ct) => exchange.RespondAsync("text/event-stream", AiHttpFixture.Utf8(
            Call(0, "held") + Finish("tool_calls") + Done), ct));
        var tool = f.Gate(); var cancelled = AiHttpFixture.NewSignal(); using var caller = new CancellationTokenSource();
        f.Port.Execute = async (_, _, ct) =>
        {
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await tool.Hold(); return new(Weft.Ai.AiJsonObject.CopyFrom("{\"ok\":true}"), 1);
        };
        var execution = f.Begin(ct: caller.Token); var original = execution.Completion;
        await Bound(tool.Entered.Task); f.FillOtherCapacity(); f.AssertBusy();
        var actualTool = await Bound(f.Port.FirstOriginalExecution.Task); Assert.False(actualTool.IsCompleted);
        if (trigger == "deadline") f.ApplicationClock.Fire(TimeSpan.FromSeconds(61));
        else if (trigger == "caller-token") caller.Cancel();
        else await Bound(execution.CancelAsync());
        await Bound(cancelled.Task); await Bound(execution.CancelAsync());
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.Same(original, execution.Completion); Assert.False(tool.Returned.Task.IsCompleted);
        Assert.False(original.IsCompleted); Assert.False(execution.ReleaseSafe); f.AssertBusy();
        Assert.False(actualTool.IsCompleted);
        tool.Open(); await Bound(tool.Returned.Task); var result = await FinishExecution(execution);
        Assert.NotEqual(ChatOrchestrationOutcome.Completed, result.Outcome); Assert.True(execution.ReleaseSafe);
        Assert.Equal(1, f.Server.RequestCount); await f.AssertOneCapacityRecovered();
        Assert.True(actualTool.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("reserve")]
    [InlineData("append")]
    public async Task Noncooperative_original_store_task_is_not_replaced_by_cancelled_waiter_or_finish_task(string method)
    {
        await using var f = new ChatOrchestrationFixture(); var held = f.Gate(); var one = 0;
        f.Store.Returning = call => call.Method == method && Interlocked.CompareExchange(ref one, 1, 0) == 0 ? held.Hold() : Task.CompletedTask;
        var execution = f.Begin(); var original = execution.Completion;
        await Bound(held.Entered.Task);
        Task<ChatTurnWriteResult>? originalAppend = method == "append" ? await Bound(f.Store.FirstOriginalAppend.Task) : null;
        if (originalAppend is not null) Assert.False(originalAppend.IsCompleted);
        await Bound(execution.CancelAsync());
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.Same(original, execution.Completion); Assert.False(original.IsCompleted); Assert.False(execution.ReleaseSafe);
        Assert.False(held.Returned.Task.IsCompleted); Assert.Equal(0, f.Store.Count("finish"));
        // An application acknowledgement can remain pending after the provider's
        // own cancellation/transport cleanup has released its capacity slot.
        // It is not a provider-cleanup gate and makes no Busy assertion.
        if (originalAppend is not null) Assert.False(originalAppend.IsCompleted);
        held.Open(); await Bound(held.Returned.Task); await FinishExecution(execution);
        Assert.True(execution.ReleaseSafe); Assert.InRange(f.Store.Count("finish"), 0, 1);
        if (originalAppend is not null) Assert.True(originalAppend.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_Stop_is_actually_observed_then_original_provider_cleanup_callbacks_hold_completion_and_capacity(bool fail)
    {
        var cleanup = new AsyncGate(); var resolver = new Resolver();
        resolver.OnCancellation = () => { cleanup.HoldSync(); if (fail) throw new InvalidOperationException("PRIVATE callback exception"); };
        await using var f = new ChatOrchestrationFixture(credentials: resolver);
        f.ReleaseWith(cleanup.Open); f.ExpectCleanupFault = fail;
        var intent = f.Intent(); var execution = f.Begin(intent);
        // No cancellation/disposal is requested by this test before this signal.
        // Resolver cancellation is a real #111 ordinary-terminal cleanup action.
        await Bound(cleanup.Entered.Task); await f.Server.WaitForCompletedAsync(1);
        Assert.Equal(1, resolver.Callbacks);
        Assert.Single(f.Store.Events(intent.TurnId), e => e.Kind == ChatEventKind.TextDelta);
        Assert.DoesNotContain(f.Store.Events(intent.TurnId), e => e.Kind is ChatEventKind.RoundCompleted or ChatEventKind.Completed);
        f.FillOtherCapacity(); f.AssertBusy();
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(execution.Completion.IsCompleted); Assert.False(execution.ReleaseSafe); Assert.False(cleanup.Returned.Task.IsCompleted);
        cleanup.Open(); await Bound(cleanup.Returned.Task); var result = await FinishExecution(execution);
        if (fail)
        {
            Assert.Equal(ChatOrchestrationOutcome.CleanupUnproven, result.Outcome); Assert.False(execution.ReleaseSafe); f.AssertBusy();
            try { await Bound(execution.CancelAsync()); }
            catch (ChatOrchestrationCleanupException error)
            { Assert.Null(error.InnerException); Assert.Equal("Chat cleanup remains unproven.", error.Message); }
            Assert.False(execution.ReleaseSafe);
            Assert.Equal(nameof(ChatOrchestrationResult), result.ToString());
        }
        else
        {
            Assert.Equal(ChatOrchestrationOutcome.Completed, result.Outcome); Assert.True(execution.ReleaseSafe);
            await f.AssertOneCapacityRecovered();
        }
        Assert.Equal(1, f.Server.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Application_timer_original_async_disposal_is_joined_and_failure_permanently_withholds_release(bool fail)
    {
        var cleanup = new AsyncGate(); var clock = new Clock { AsyncDisposal = cleanup, ThrowAsyncDisposal = fail };
        await using var f = new ChatOrchestrationFixture(applicationClock: clock); f.ReleaseWith(cleanup.Open);
        var execution = f.Begin(); await Bound(cleanup.Entered.Task);
        var actualDispose = await Bound(clock.FirstOriginalDisposal.Task); Assert.False(actualDispose.IsCompleted);
        Assert.False(execution.Completion.IsCompleted); Assert.False(execution.ReleaseSafe); Assert.False(cleanup.Returned.Task.IsCompleted);
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        cleanup.Open(); await Bound(cleanup.Returned.Task); var result = await FinishExecution(execution);
        Assert.Equal(fail ? ChatOrchestrationOutcome.CleanupUnproven : ChatOrchestrationOutcome.Completed, result.Outcome);
        Assert.Equal(!fail, execution.ReleaseSafe);
        Assert.True(fail ? actualDispose.IsFaulted : actualDispose.IsCompletedSuccessfully);
        await Bound(execution.CancelAsync()); Assert.Equal(!fail, execution.ReleaseSafe);
    }

    [Fact]
    public async Task Caller_cancellation_during_held_CreateTimer_admission_joins_owned_cleanup_before_safe_release()
    {
        var providerClock = new Clock();
        await using var f = new ChatOrchestrationFixture(providerClock: providerClock);
        using var caller = new CancellationTokenSource();
        var reserve = f.Gate(); var initialization = f.Gate(); var cleanup = f.Gate();
        var policyAllowed = AiHttpFixture.NewSignal();
        var intent = f.Intent();
        providerClock.SynchronousDisposal = cleanup.HoldSync;
        providerClock.TimerCreation = () => { f.Trace.Add("test.provider-initialization-entered"); initialization.HoldSync(); };
        f.Store.Returning = call => call.Method == "reserve" ? reserve.Hold() : Task.CompletedTask;
        f.ProfilePolicy.Check = (actor, conversation, turn, profile, ct) =>
        {
            Assert.Same(f.Actor, actor); Assert.Equal(f.Conversation, conversation); Assert.Same(intent, turn); Assert.Same(f.Profile, profile);
            if (f.Store.Count("reserve") == 1)
            {
                Assert.Contains(f.Trace.Snapshot, mark => mark.Kind == "ack.reserve");
                Assert.False(ct.IsCancellationRequested);
                f.Trace.Add("test.post-reservation-policy-allowed"); policyAllowed.TrySetResult();
            }
            return Task.FromResult(true);
        };
        var execution = f.Begin(intent, ct: caller.Token); var original = execution.Completion;
        await Bound(reserve.Entered.Task); reserve.Open();
        await Bound(policyAllowed.Task); await Bound(initialization.Entered.Task);
        var trace = f.Trace.Snapshot;
        Assert.True(trace.First(mark => mark.Kind == "test.post-reservation-policy-allowed").Sequence <
            trace.Single(mark => mark.Kind == "test.provider-initialization-entered").Sequence);
        Assert.Equal(1, providerClock.TimerCreations);
        // Cancellation is now strictly inside the real network-free admission,
        // not inside the policy callback before the recheck's final token check.
        caller.Cancel(); await Bound(execution.CancelAsync());
        Assert.False(initialization.Returned.Task.IsCompleted); Assert.False(original.IsCompleted); Assert.False(execution.ReleaseSafe);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.RequestCount);
        initialization.Open(); await Bound(cleanup.Entered.Task);
        // The gate is inside the real provider timer's Dispose operation, including
        // its DisposeAsync path. Neither this entry/return checkpoint nor a public
        // waiter is relabeled as the unexposed AiTurnLease disposal Task.
        Assert.False(cleanup.Returned.Task.IsCompleted);
        Assert.Same(original, execution.Completion); Assert.False(original.IsCompleted); Assert.False(execution.ReleaseSafe);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(1, f.Store.Count("reserve")); Assert.Equal(ChatRoundOutcome.Uncertain, Assert.Single(f.Store.Rounds(intent.TurnId)).Outcome);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.RequestCount); Assert.Empty(f.Port.Executions);
        f.FillOtherCapacity(); f.AssertBusy();
        await Assert.ThrowsAsync<TimeoutException>(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(cleanup.Returned.Task.IsCompleted); Assert.False(original.IsCompleted); f.AssertBusy();
        cleanup.Open(); var result = await FinishExecution(execution);
        Assert.Equal(ChatOrchestrationOutcome.Cancelled, result.Outcome); Assert.Equal(ChatTurnPhase.Cancelled, result.Phase);
        Assert.True(cleanup.Returned.Task.IsCompletedSuccessfully); Assert.True(execution.ReleaseSafe);
        Assert.Same(original, execution.Completion); Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.RequestCount);
        Assert.Empty(f.Port.Validations); Assert.Empty(f.Port.Executions);
        await f.AssertOneCapacityRecovered(); // Before the fixture owner's real shared-client shutdown.
    }

    [Fact]
    public async Task Caller_cancellation_inside_policy_recheck_finishes_before_any_provider_admission()
    {
        var providerClock = new Clock();
        await using var f = new ChatOrchestrationFixture(providerClock: providerClock);
        using var caller = new CancellationTokenSource(); var reserve = f.Gate(); var cancelledInPolicy = 0;
        f.Store.Returning = call => call.Method == "reserve" ? reserve.Hold() : Task.CompletedTask;
        ChatTurnExecution? execution = null;
        f.ProfilePolicy.Check = async (_, _, _, _, ct) =>
        {
            if (f.Store.Count("reserve") == 1 && Interlocked.CompareExchange(ref cancelledInPolicy, 1, 0) == 0)
            { caller.Cancel(); await execution!.CancelAsync(); Assert.True(ct.IsCancellationRequested); }
            return true;
        };
        execution = f.Begin(ct: caller.Token); await Bound(reserve.Entered.Task); reserve.Open();
        var result = await FinishExecution(execution);
        Assert.Equal(1, cancelledInPolicy); Assert.Equal(ChatOrchestrationOutcome.Cancelled, result.Outcome);
        Assert.True(execution.ReleaseSafe); Assert.Equal(0, providerClock.TimerCreations);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.RequestCount);
        Assert.Empty(f.Port.Validations); Assert.Empty(f.Port.Executions);
    }

    [Fact]
    public async Task Unreturned_OpenTurn_initialization_fault_reports_permanent_quarantine_not_fabricated_per_turn_settlement()
    {
        var providerClock = new Clock();
        await using var f = new ChatOrchestrationFixture(providerClock: providerClock);
        f.ExpectCleanupFault = true;
        var reserve = f.Gate(); var initialization = f.Gate();
        f.Store.Returning = call => call.Method == "reserve" ? reserve.Hold() : Task.CompletedTask;
        var execution = f.Begin(); await Bound(reserve.Entered.Task);
        // GetTimestamp is the public dependency seam used by #111 initialization;
        // no private lease/task is read or reconstructed.
        providerClock.TimestampGate = initialization; providerClock.ThrowTimestamp = true;
        reserve.Open(); await Bound(initialization.Entered.Task);
        Assert.False(execution.Completion.IsCompleted); Assert.False(execution.ReleaseSafe);
        initialization.Open(); var result = await FinishExecution(execution);
        Assert.Equal(ChatOrchestrationOutcome.CleanupUnproven, result.Outcome); Assert.False(execution.ReleaseSafe);
        Assert.Equal(0, f.Credentials.Calls); Assert.Equal(0, f.Server.RequestCount);
        // The borrowed global client was NOT disposed by the application execution:
        var probe = Assert.IsType<Weft.Ai.TurnOpened>(f.Client.OpenTurn(f.Profile.Key, Weft.Ai.AiTurnLimits.Default, CancellationToken.None)).Turn;
        await probe.DisposeAsync();
        // This is the client's ACTUAL public owner shutdown, not a reflection probe
        // or a waiter mislabeled as an original hidden task. Its completion does
        // not retroactively make the per-execution public proof stronger.
        var ownerCleanup = f.Client.DisposeAsync().AsTask(); f.ExpectedClientCleanupFailure = ownerCleanup;
        Assert.Same(ownerCleanup, f.Client.DisposeAsync().AsTask());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Bound(ownerCleanup));
        AssertProviderCleanupFailure(error);
        var repeated = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Bound(f.Client.DisposeAsync().AsTask()));
        Assert.Same(error, repeated); AssertProviderCleanupFailure(repeated);
        Assert.False(execution.ReleaseSafe); Assert.Same(result, await FinishExecution(execution));
    }
}
