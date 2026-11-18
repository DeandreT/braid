using Azure.Messaging.ServiceBus;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.MachineEndpoint;
using Braid.Persistence.Postgres;
using Braid.Triggers.Emulators;

namespace Braid.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalBrokerCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<TriggerEmulatorFixture>
{
    public const string Name = "Owned PostgreSQL and local broker";
}

public sealed class LocalBrokerTheoryAttribute : TheoryAttribute
{
    public LocalBrokerTheoryAttribute()
    {
        if (!PostgresFixture.IsConfigured || !TriggerEmulatorFixture.IsConfigured) Skip = "Requires owned PostgreSQL and an approved local broker fixture.";
    }
}

[Collection(LocalBrokerCollection.Name)]
public sealed class LocalBrokerRecoveryTests(PostgresFixture postgres, TriggerEmulatorFixture emulator)
{
    [LocalBrokerTheory]
    [InlineData("braid-orders-5", null)]
    [InlineData("braid-input-topic", "braid-in")]
    public async Task Fresh_worker_recovers_unsettled_order_and_publishes_one_topic_message(string entity, string? subscription)
    {
        await using var test = await TriggerPersistenceTests.Context.CreateAsync(postgres);
        Assert.Equal(MigrationRunner.EmbeddedMigrations[^1].Version, await test.Case.ScalarAsync<int>("SELECT max(version) FROM braid.schema_migrations"));
        var initial = test.Configuration(); var output = new TriggerConnectorReference("topic-out", 1);
        var config = new TriggerConfiguration(test.TriggerId, test.Target, initial.Kind, test.Binding, initial.Source, initial.Caller, initial.ApplicationId,
            initial.InputMediaType, initial.Parameters, [new(null, test.Blob, TriggerDestinationKind.ServiceBusTopic, output)], initial.Policies);
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 0, config)));
        await test.EnableAsync(created: true);
        var bytes = "{\"qualified\":true}"u8.ToArray();
        var outputs = RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, bytes, mediaType: "application/json")]);
        var source = new ServiceBusQueueConnector(initial.Source, entity, 0, 1, TimeSpan.FromMilliseconds(25), subscription);
        var id = Guid.NewGuid().ToString("D");
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = bound.Token;
        // First consumer commits the real ledger and outbox, then loses the completion. Explicit abandon makes
        // redelivery deterministic without waiting for a lease. No output has been sent yet.
        await using (var first = ServiceBusQueueTrigger.CreateEmulatorClient(emulator.ConnectionString))
        {
            await using var dest = new ServiceBusTopicDestination(output, first, "braid-deliveries");
            var processor = new QueueOrderProcessor(test.Worker, async (_, work, caller, ct) =>
            {
                var claim = await test.Worker.ClaimPinnedAsync(test.Target, work, caller, ct);
                Assert.Equal(TriggerClaimStatus.Claimed, claim.Status);
                Assert.Equal(TriggerFinishStatus.Recorded, await test.FinishAsync(work, claim, outputs));
                return new(TriggeredInvocationStatus.Completed);
            }, new(test.Worker, [dest], TimeProvider.System));
            await using var sender = first.CreateSender(entity);
            await sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString("{}")) { MessageId = id }, token);
            await using var receiver = subscription is null ? first.CreateReceiver(entity) : first.CreateReceiver(entity, subscription);
            var incoming = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(45), token);
            Assert.NotNull(incoming); Assert.Equal(id, incoming.MessageId);
            var decision = await processor.ProcessAsync(test.Target, test.TriggerId, test.Caller, new(id, incoming.ContentType, incoming.Body.ToMemory()), token);
            Assert.Equal(QueueSettlement.Complete, decision.Settlement);
            Assert.Equal("pending", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries"));
            await receiver.AbandonMessageAsync(incoming, cancellationToken: token);
        }
        await using var bus = ServiceBusQueueTrigger.CreateEmulatorClient(emulator.ConnectionString);
        await using var store = new PostgresTriggerWorkStore(test.Case.RegressionConnectionString("braid_trigger_worker"));
        await using var destination = new ServiceBusTopicDestination(output, bus, "braid-deliveries");
        var replay = new TaskCompletionSource<TriggerClaimStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new TriggerWorkerLoop(test.Target, store, bus, [(test.TriggerId, source)], [], [destination], [test.Blob], async (_, work, caller, ct) =>
        {
            var claim = await store.ClaimPinnedAsync(test.Target, work, caller, ct);
            replay.TrySetResult(claim.Status);
            return new(claim.Status == TriggerClaimStatus.Recorded ? TriggeredInvocationStatus.Completed : TriggeredInvocationStatus.Retry);
        }, _ => Task.FromResult(test.Caller), () => null, TimeProvider.System);
        var running = loop.RunAsync(token);
        try
        {
            Assert.Equal(TriggerClaimStatus.Recorded, await replay.Task.WaitAsync(token));
            await using var observer = bus.CreateReceiver("braid-deliveries", "braid-receipts");
            var receipt = await observer.ReceiveMessageAsync(TimeSpan.FromSeconds(60), token);
            Assert.NotNull(receipt); Assert.Equal(bytes, receipt.Body.ToArray());
            var delivery = await test.Case.ScalarAsync<Guid>("SELECT delivery_id FROM braid.trigger_deliveries");
            Assert.Equal(delivery.ToString("D"), receipt.MessageId); Assert.Equal(receipt.MessageId, receipt.ApplicationProperties["braidDelivery"]);
            await observer.CompleteMessageAsync(receipt, token);
            Assert.Null(await observer.ReceiveMessageAsync(TimeSpan.FromSeconds(2), token));
        }
        finally { await bound.CancelAsync(); await running; }
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocations WHERE attempt=1 AND state='completed'"));
        Assert.Equal("accepted", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE sends=1"));
        await using var drained = subscription is null ? bus.CreateReceiver(entity) : bus.CreateReceiver(entity, subscription);
        Assert.Null(await drained.PeekMessageAsync());
    }
}
