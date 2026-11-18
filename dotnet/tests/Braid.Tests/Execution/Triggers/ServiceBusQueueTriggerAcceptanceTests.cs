using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Braid.Deployment.Triggers;
using Braid.Triggers.Emulators;

namespace Braid.Tests;

/// <summary>The queue trigger against the actual Service Bus emulator, with durable work and the runtime fixture in memory.</summary>
[Collection(TriggerEmulatorCollection.Name)]
public sealed class ServiceBusQueueTriggerAcceptanceTests(TriggerEmulatorFixture emulator)
{
    [TriggerEmulatorFact]
    public async Task Valid_failing_valid_orders_complete_or_dead_letter_independently()
    {
        var test = await TriggerQueueProcessingTests.Harness.CreateAsync();
        const string queue = "braid-orders-1";
        await SendAsync(queue, ("order-0", "{}", null), ("order-1", "{}", "text/plain"), ("order-2", "{}", null));
        await using (var trigger = Trigger(queue, test, test.ProcessAsync))
        {
            await trigger.StartAsync(default);
            var dead = await DeadLettersAsync(queue, 1);
            await WaitAsync(() => test.Runtime.RuntimeCalls == 2 && test.Work.Admitted == 2);
            await trigger.StopAsync(default);
            var message = Assert.Single(dead);
            Assert.Equal(("order-1", "unsupportedFormat", (string?)null), (message.MessageId, message.DeadLetterReason, message.DeadLetterErrorDescription));
            Assert.Equal("{}", message.Body.ToString());
        }
        Assert.Null(await PeekAsync(queue));
    }

    [TriggerEmulatorFact]
    public async Task A_lost_settlement_is_recovered_from_durable_work_on_redelivery_without_running_again()
    {
        var test = await TriggerQueueProcessingTests.Harness.CreateAsync();
        const string queue = "braid-orders-2";
        var deliveries = new ConcurrentQueue<QueueSettlement>();
        async Task<QueueOrderDecision> LoseFirst(QueueMessage message)
        {
            var decision = await test.ProcessAsync(message);
            deliveries.Enqueue(decision.Settlement);
            // The first completion is never acknowledged, as if Complete failed after the durable handoff.
            return deliveries.Count == 1 ? new(QueueSettlement.Abandon) : decision;
        }
        await SendAsync(queue, ("order-r", "{}", null));
        await using (var trigger = Trigger(queue, test, LoseFirst))
        {
            await trigger.StartAsync(default);
            await WaitAsync(() => deliveries.Count >= 2);
            await trigger.StopAsync(default);
        }
        Assert.Equal([QueueSettlement.Complete, QueueSettlement.Complete], deliveries.Take(2));
        Assert.Equal(1, test.Runtime.RuntimeCalls);
        Assert.Null(await PeekAsync(queue));
        Assert.Empty(await DeadLettersAsync(queue, 0));
    }

    [TriggerEmulatorFact]
    public async Task Changed_bytes_under_a_completed_id_and_recorded_failures_dead_letter_with_only_the_work_id()
    {
        var test = await TriggerQueueProcessingTests.Harness.CreateAsync();
        const string queue = "braid-orders-3";
        await using var trigger = Trigger(queue, test, test.ProcessAsync);
        await trigger.StartAsync(default);
        await SendAsync(queue, ("dup", "{}", null));
        await WaitAsync(() => test.Runtime.RuntimeCalls == 1);
        await SendAsync(queue, ("dup", "{\"changed\":true}", null));
        var conflict = Assert.Single(await DeadLettersAsync(queue, 1));
        Assert.Equal(("dup", "idempotencyConflict", (string?)null), (conflict.MessageId, conflict.DeadLetterReason, conflict.DeadLetterErrorDescription));
        test.Work.ForcedClaim = new(TriggerClaimStatus.Recorded, outcome: Braid.Deployment.MachineInvocationOutcome.InvalidInput);
        await SendAsync(queue, ("failed", "{\"secret\":\"payload\"}", null));
        var failed = Assert.Single(await DeadLettersAsync(queue, 1));
        await trigger.StopAsync(default);
        Assert.Equal(("failed", "invalidInput"), (failed.MessageId, failed.DeadLetterReason));
        Assert.True(Guid.TryParseExact(failed.DeadLetterErrorDescription, "D", out _));
        Assert.DoesNotContain("secret", failed.DeadLetterErrorDescription, StringComparison.Ordinal);
        Assert.Equal(1, test.Runtime.RuntimeCalls);
    }

    [TriggerEmulatorFact]
    public async Task A_message_that_keeps_needing_a_retry_is_dead_lettered_by_the_broker_never_looped()
    {
        var test = await TriggerQueueProcessingTests.Harness.CreateAsync();
        const string queue = "braid-orders-4";
        var attempts = 0;
        Task<QueueOrderDecision> Unavailable(QueueMessage message) { Interlocked.Increment(ref attempts); throw new InvalidOperationException("database unavailable"); }
        await SendAsync(queue, ("retry", "{}", null));
        await using (var trigger = Trigger(queue, test, Unavailable))
        {
            await trigger.StartAsync(default);
            var dead = Assert.Single(await DeadLettersAsync(queue, 1));
            await trigger.StopAsync(default);
            Assert.Equal("MaxDeliveryCountExceeded", dead.DeadLetterReason);
            Assert.DoesNotContain("database", dead.DeadLetterErrorDescription ?? "", StringComparison.Ordinal);
        }
        Assert.Equal(5, attempts);
        Assert.Equal(0, test.Work.Admitted);
    }

    private ServiceBusQueueTrigger Trigger(string queue, TriggerQueueProcessingTests.Harness test, Func<QueueMessage, Task<QueueOrderDecision>> process) =>
        new(emulator.Client, new(test.Configuration.Source, queue, 0, 1, TimeSpan.FromMilliseconds(100)), test.Configuration, (message, _) => process(message));

    private async Task SendAsync(string queue, params (string Id, string Body, string? ContentType)[] messages)
    {
        await using var sender = emulator.Client.CreateSender(queue);
        foreach (var (id, body, contentType) in messages)
            await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(body)) { MessageId = id, ContentType = contentType });
    }

    private async Task<ServiceBusReceivedMessage?> PeekAsync(string queue)
    {
        await using var receiver = emulator.Client.CreateReceiver(queue);
        return await receiver.PeekMessageAsync();
    }

    /// <summary>Receives and completes the expected number of dead letters, waiting for them to arrive, then confirms no more.</summary>
    private async Task<List<ServiceBusReceivedMessage>> DeadLettersAsync(string queue, int expected)
    {
        await using var receiver = emulator.Client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var result = new List<ServiceBusReceivedMessage>();
        var timer = Stopwatch.StartNew();
        while (result.Count < expected && timer.Elapsed < TimeSpan.FromSeconds(120))
            foreach (var message in await receiver.ReceiveMessagesAsync(expected - result.Count, TimeSpan.FromSeconds(5)))
            {
                await receiver.CompleteMessageAsync(message);
                result.Add(message);
            }
        result.AddRange(await receiver.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(2)));
        return result;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("The emulator did not reach the expected state.");
            await Task.Delay(100);
        }
    }
}
