using System.Text;
using Azure.Core;
using Azure.Storage.Blobs;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Braid.Triggers.Emulators;

namespace Braid.Tests;

/// <summary>Queue admission, settlement decisions and delivery dispatch through the shared invocation core, without a database or broker.</summary>
public sealed class TriggerQueueProcessingTests
{
    [Fact]
    public async Task A_message_completes_only_after_its_durable_handoff_and_a_redelivery_never_runs_again()
    {
        var test = await Harness.CreateAsync();
        var first = await test.ProcessAsync(Harness.Message("order-1"));
        Assert.Equal(QueueSettlement.Complete, first.Settlement);
        Assert.Equal(1, test.Runtime.RuntimeCalls);
        Assert.Equal(MachineInvocationOutcome.Completed, test.Work.Outcome(first.WorkId!.Value));
        var again = await test.ProcessAsync(Harness.Message("order-1"));
        Assert.Equal((QueueSettlement.Complete, first.WorkId), (again.Settlement, again.WorkId));
        Assert.Equal(1, test.Runtime.RuntimeCalls);
        Assert.Equal(1, test.Work.Admitted);
    }

    [Fact]
    public async Task Envelope_and_identity_refusals_dead_letter_with_fixed_reasons_and_admit_nothing()
    {
        var test = await Harness.CreateAsync();
        Assert.Equal("unsupportedFormat", (await test.ProcessAsync(Harness.Message("a", contentType: "application/xml"))).DeadLetterReason);
        Assert.Equal("resourceLimit", (await test.ProcessAsync(Harness.Message("b", body: new string('x', 2048)))).DeadLetterReason);
        Assert.Equal("invalidEnvelope", (await test.ProcessAsync(Harness.Message(new string('m', 129)))).DeadLetterReason);
        Assert.Equal(0, test.Work.Admitted);
        Assert.Equal(QueueSettlement.Complete, (await test.ProcessAsync(Harness.Message("c"))).Settlement);
        var conflict = await test.ProcessAsync(Harness.Message("c", body: "{\"changed\":true}"));
        Assert.Equal((QueueSettlement.DeadLetter, "idempotencyConflict", (Guid?)null), (conflict.Settlement, conflict.DeadLetterReason, conflict.WorkId));
        Assert.Equal(1, test.Runtime.RuntimeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task One_failing_order_at_the_start_middle_or_end_does_not_block_the_others(int failing)
    {
        var test = await Harness.CreateAsync();
        var decisions = new List<QueueOrderDecision>();
        for (var index = 0; index < 3; index++)
            decisions.Add(await test.ProcessAsync(Harness.Message("order-" + index, contentType: index == failing ? "text/plain" : null)));
        Assert.Equal(Enumerable.Range(0, 3).Select(index => index == failing ? QueueSettlement.DeadLetter : QueueSettlement.Complete), decisions.Select(value => value.Settlement));
        Assert.Equal(2, test.Runtime.RuntimeCalls);
        // A corrected order is a new explicit message identity; the successful ones are not run again.
        Assert.Equal(QueueSettlement.Complete, (await test.ProcessAsync(Harness.Message("order-" + failing + "-corrected"))).Settlement);
        foreach (var index in Enumerable.Range(0, 3).Where(index => index != failing))
            Assert.Equal(QueueSettlement.Complete, (await test.ProcessAsync(Harness.Message("order-" + index))).Settlement);
        Assert.Equal(3, test.Runtime.RuntimeCalls);
    }

    [Fact]
    public async Task Missing_policies_another_caller_an_unserved_route_or_admission_pressure_abandon_without_running()
    {
        var test = await Harness.CreateAsync();
        test.Work.Definition = new(TriggerRuntimeTests.Configuration(test.Target, test.Caller.Principal, test.Caller.ApplicationId, test.Routes, draft: true), 1);
        Assert.Equal(QueueSettlement.Abandon, (await test.ProcessAsync(Harness.Message("a"))).Settlement);
        test.Work.Definition = null;
        Assert.Equal(QueueSettlement.Abandon, (await test.ProcessAsync(Harness.Message("a"))).Settlement);
        test.Work.Definition = new(test.Configuration, 1);
        var other = new MachineCaller(test.Caller.Principal, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(QueueSettlement.Abandon, (await test.Processor.ProcessAsync(test.Target, test.Configuration.TriggerId, other, Harness.Message("a"), default)).Settlement);
        var unserved = await Harness.CreateAsync(registerDestination: false);
        Assert.Equal(QueueSettlement.Abandon, (await unserved.ProcessAsync(Harness.Message("a"))).Settlement);
        foreach (var status in new[] { TriggerAdmissionStatus.Refused, TriggerAdmissionStatus.NoRelease, TriggerAdmissionStatus.CapacityExceeded })
        {
            test.Work.ForcedAdmission = status;
            Assert.Equal(QueueSettlement.Abandon, (await test.ProcessAsync(Harness.Message("a"))).Settlement);
        }
        Assert.Equal(0, test.Work.Admitted + unserved.Work.Admitted);
        Assert.Equal(0, test.Runtime.RuntimeCalls);
    }

    [Theory]
    [InlineData(MachineInvocationOutcome.InvalidInput, "invalidInput")]
    [InlineData(MachineInvocationOutcome.InvalidParameter, "invalidParameter")]
    [InlineData(MachineInvocationOutcome.ExecutionFailed, "executionFailed")]
    [InlineData(MachineInvocationOutcome.ResourceLimit, "resourceLimit")]
    [InlineData(MachineInvocationOutcome.IntegrityFailure, "integrityFailure")]
    [InlineData(MachineInvocationOutcome.Unsupported, "unsupportedFormat")]
    [InlineData(MachineInvocationOutcome.AttemptsExhausted, "attemptsExhausted")]
    [InlineData(MachineInvocationOutcome.Refused, "authorizationRevoked")]
    public async Task Recorded_failures_dead_letter_with_their_fixed_reason_and_private_work_id(MachineInvocationOutcome outcome, string reason)
    {
        var test = await Harness.CreateAsync();
        test.Work.ForcedClaim = new(TriggerClaimStatus.Recorded, outcome: outcome);
        var decision = await test.ProcessAsync(Harness.Message("order"));
        Assert.Equal((QueueSettlement.DeadLetter, reason), (decision.Settlement, decision.DeadLetterReason));
        Assert.NotNull(decision.WorkId);
    }

    [Theory]
    [InlineData(TriggerClaimStatus.Fenced, QueueSettlement.DeadLetter, "authorizationRevoked")]
    [InlineData(TriggerClaimStatus.ReleaseSuperseded, QueueSettlement.DeadLetter, "releaseSuperseded")]
    [InlineData(TriggerClaimStatus.InProgress, QueueSettlement.Abandon, null)]
    [InlineData(TriggerClaimStatus.Refused, QueueSettlement.Abandon, null)]
    public async Task Fenced_superseded_and_unclaimable_work_settle_without_running(TriggerClaimStatus claim, QueueSettlement settlement, string? reason)
    {
        var test = await Harness.CreateAsync();
        test.Work.ForcedClaim = new(claim);
        var decision = await test.ProcessAsync(Harness.Message("order"));
        Assert.Equal((settlement, reason), (decision.Settlement, decision.DeadLetterReason));
        Assert.Equal(0, test.Runtime.RuntimeCalls);
    }

    [Fact]
    public async Task A_batch_completes_only_when_every_order_is_durable_and_a_redelivery_never_runs_again()
    {
        var test = await Harness.CreateAsync(batch: true);
        var message = Harness.Batch("batch-1", ("a", "{}"), ("b", "{\"n\":2}"), ("c", "{\"n\":3}"));
        var first = await test.ProcessAsync(message);
        Assert.Equal(QueueSettlement.Complete, first.Settlement);
        Assert.Equal((3, 3, 1), (test.Runtime.RuntimeCalls, test.Work.Admitted, test.Batches.Parents));
        var again = await test.ProcessAsync(message);
        Assert.Equal((QueueSettlement.Complete, first.WorkId), (again.Settlement, again.WorkId));
        Assert.Equal(3, test.Runtime.RuntimeCalls);
        var changed = await test.ProcessAsync(Harness.Batch("batch-1", ("a", "{}"), ("b", "{\"n\":2}"), ("c", "{\"n\":4}")));
        Assert.Equal((QueueSettlement.DeadLetter, "idempotencyConflict"), (changed.Settlement, changed.DeadLetterReason));
        Assert.Equal(3, test.Work.Admitted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_failing_order_at_the_start_middle_or_end_of_a_batch_keeps_the_others_successful(int failing)
    {
        var test = await Harness.CreateAsync(batch: true);
        test.Work.FailingInput = "{\"bad\":true}";
        var orders = Enumerable.Range(0, 3).Select(index => ("k" + index, index == failing ? "{\"bad\":true}" : "{}")).ToArray();
        var decision = await test.ProcessAsync(Harness.Batch("batch", orders));
        Assert.Equal((QueueSettlement.DeadLetter, "invalidInput"), (decision.Settlement, decision.DeadLetterReason));
        Assert.Equal(2, test.Runtime.RuntimeCalls);
        Assert.NotNull(decision.WorkId);
        Assert.Equal(2, Enumerable.Range(0, 3).Count(index => test.Work.Outcome(test.Work.WorkFor("batch", "k" + index)) == MachineInvocationOutcome.Completed));
        // The redelivered batch recovers the recorded outcomes; nothing runs again.
        var calls = test.Runtime.RuntimeCalls;
        Assert.Equal(decision, await test.ProcessAsync(Harness.Batch("batch", orders)));
        Assert.Equal(calls, test.Runtime.RuntimeCalls);
    }

    [Fact]
    public async Task A_batch_envelope_that_is_not_exactly_the_contract_dead_letters_and_admits_nothing()
    {
        var test = await Harness.CreateAsync(batch: true);
        Assert.Equal("invalidEnvelope", (await test.ProcessAsync(Harness.Message("x", "{\"orders\":[]}"))).DeadLetterReason);
        Assert.Equal("invalidEnvelope", (await test.ProcessAsync(Harness.Batch("y", ("k", "{}"), ("k", "{}")))).DeadLetterReason);
        Assert.Equal("unsupportedFormat", (await test.ProcessAsync(Harness.Message("z", "{}", "application/json"))).DeadLetterReason);
        Assert.Equal(0, test.Work.Admitted);
        // A single-order trigger never splits a message, even one shaped like a batch.
        var single = await Harness.CreateAsync();
        Assert.Equal(QueueSettlement.Complete, (await single.ProcessAsync(Harness.Batch("b", ("k", "{}")))).Settlement);
        Assert.Equal(1, single.Work.Admitted);
    }

    [Fact]
    public async Task A_route_without_its_exact_registered_connector_is_refused_without_a_send()
    {
        var store = new DeliveryStore(Route(new("blob-out", 2)));
        var destination = new Destination(new("blob-out", 1), (_, _) => Task.FromResult(DeliveryObservationKind.Accepted));
        var dispatcher = new TriggerDeliveryDispatcher(store, [destination], TimeProvider.System);
        Assert.False(dispatcher.Serves(store.Route));
        Assert.True(await dispatcher.DispatchAsync(TriggerRuntimeTests.Target(), store.Route.DestinationBindingId, default));
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, Assert.Single(store.Observations));
        Assert.Equal(0, destination.Sends);
        Assert.Throws<ArgumentException>(() => new TriggerDeliveryDispatcher(store, [destination, destination], TimeProvider.System));
    }

    [Theory]
    [InlineData("accepted", DeliveryObservationKind.Accepted)]
    [InlineData("permanent", DeliveryObservationKind.PermanentFailure)]
    [InlineData("throws", DeliveryObservationKind.Unknown)]
    [InlineData("hangs", DeliveryObservationKind.Unknown)]
    [InlineData("fenced", DeliveryObservationKind.Unknown)]
    public async Task Only_proven_outcomes_are_recorded_and_failures_after_dispatch_are_unknown(string behavior, DeliveryObservationKind expected)
    {
        var connector = new TriggerConnectorReference("blob-out", 2);
        var store = new DeliveryStore(Route(connector), behavior == "hangs" ? TimeSpan.FromMilliseconds(300) : null);
        var destination = new Destination(connector, async (_, token) =>
        {
            if (behavior == "hangs") await Task.Delay(Timeout.Infinite, token);
            return behavior switch
            {
                "accepted" => DeliveryObservationKind.Accepted,
                "permanent" => DeliveryObservationKind.PermanentFailure,
                "fenced" => DeliveryObservationKind.Fenced,
                _ => throw new IOException("reset")
            };
        });
        Assert.True(await new TriggerDeliveryDispatcher(store, [destination], TimeProvider.System).DispatchAsync(TriggerRuntimeTests.Target(), store.Route.DestinationBindingId, default));
        Assert.Equal(expected, Assert.Single(store.Observations));
        Assert.Equal(1, destination.Sends);
    }

    [Fact]
    public async Task A_blob_destination_never_redirects_a_delivery_frozen_for_another_connector_version()
    {
        var container = new BlobContainerClient(new Uri("http://127.0.0.1:9/devstoreaccount1/unused"));
        Assert.Throws<ArgumentException>(() => new BlobOutputDestination(new("blob-out", 1), container, "Upper/"));
        Assert.Throws<ArgumentException>(() => new BlobOutputDestination(new("blob-out", 1), container, "no-slash"));
        Assert.Throws<ArgumentException>(() => new BlobOutputDestination(new("blob-out", 1), container, "../"));
        var destination = new BlobOutputDestination(new("blob-out", 1), container, "orders/out/");
        var delivery = Delivery(version: 2);
        Assert.Equal("orders/out/" + delivery.DeliveryId.ToString("D"), destination.Key(delivery));
        Assert.Equal(DeliveryObservationKind.PermanentFailure, await destination.SendAsync(delivery, default));
    }

    [Fact]
    public void Queue_connectors_and_receivers_require_explicit_bounds_and_the_registered_source()
    {
        var source = new TriggerConnectorReference("orders-in", 1);
        Assert.Throws<ArgumentException>(() => new ServiceBusQueueConnector(source, "orders", 0, 5, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new ServiceBusQueueConnector(source, "orders", 65, 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new ServiceBusQueueConnector(source, "orders", 0, 1, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new ServiceBusQueueConnector(source, "a/b", 0, 1, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => ServiceBusQueueTrigger.CreateEmulatorClient("Endpoint=sb://10.0.0.4;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true"));
        Assert.Throws<ArgumentException>(() => ServiceBusQueueTrigger.CreateEmulatorClient("Endpoint=sb://127.0.0.1;SharedAccessKeyName=k;SharedAccessKey=v"));
        var client = ServiceBusQueueTrigger.CreateEmulatorClient("Endpoint=sb://127.0.0.1;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true");
        var connector = new ServiceBusQueueConnector(source, "orders", 0, 1, TimeSpan.FromSeconds(1));
        Task<QueueOrderDecision> Never(QueueMessage message, CancellationToken token) => throw new InvalidOperationException();
        var target = TriggerRuntimeTests.Target();
        Assert.Throws<ArgumentException>(() => new ServiceBusQueueTrigger(client, connector, TriggerRuntimeTests.Configuration(target, draft: true), Never));
        Assert.Throws<ArgumentException>(() => new ServiceBusQueueTrigger(client, connector, TriggerRuntimeTests.Configuration(target, source: "other-in"), Never));
        _ = new ServiceBusQueueTrigger(client, connector, TriggerRuntimeTests.Configuration(target), Never);
    }

    internal static FrozenTriggerDelivery Delivery(long version = 1, string content = "{\"ok\":true}", Guid? deliveryId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new(deliveryId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), version, DateTimeOffset.UtcNow, bytes, Sha256Digest.FromBytes(bytes), bytes.Length,
            "application/json");
    }

    private static TriggerRoute Route(TriggerConnectorReference connector) => new(null, Guid.NewGuid(), TriggerDestinationKind.Blob, connector);

    /// <summary>A pinned trigger over the in-memory runtime fixture with durable admission, claims and outcomes held in memory.</summary>
    internal sealed class Harness
    {
        private Harness() { }
        internal MachineInvocationServiceTests.Fixture Runtime { get; private init; } = null!;
        internal MemoryTriggerWork Work { get; private init; } = null!;
        internal QueueOrderProcessor Processor { get; private init; } = null!;
        internal TriggerConfiguration Configuration { get; private init; } = null!;
        internal IReadOnlyList<TriggerRoute> Routes { get; private init; } = null!;
        internal ReleaseTargetReference Target => Runtime.Target.Reference;
        internal MachineCaller Caller { get; private init; } = null!;

        internal MemoryBatches Batches { get; private init; } = null!;

        internal static async Task<Harness> CreateAsync(bool registerDestination = true, bool batch = false)
        {
            var runtime = new MachineInvocationServiceTests.Fixture(format: "json");
            await runtime.Activate();
            var caller = new MachineCaller(new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
            var connector = new TriggerConnectorReference("blob-out", 1);
            IReadOnlyList<TriggerRoute> routes = [new(null, Guid.NewGuid(), TriggerDestinationKind.Blob, connector)];
            var policies = new TriggerPolicies(new DeliveryRetryPolicy(2, [TimeSpan.FromMilliseconds(1)], TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)),
                TimeSpan.FromSeconds(60), 1024, TimeSpan.FromHours(2), 100, 1024 * 1024);
            var single = TriggerRuntimeTests.Configuration(runtime.Target.Reference, caller.Principal, caller.ApplicationId, routes, policies: policies);
            var configuration = !batch ? single : new TriggerConfiguration(single.TriggerId, single.Target, single.Kind, single.SourceBindingId, single.Source,
                single.Caller, single.ApplicationId, single.InputMediaType, single.Parameters, single.Routes, single.Policies, TriggerEnvelope.Batch);
            var work = new MemoryTriggerWork(runtime, configuration);
            var batches = new MemoryBatches(work);
            IOutputDestination[] destinations = registerDestination
                ? [new Destination(connector, (_, _) => Task.FromResult(DeliveryObservationKind.Accepted))] : [];
            return new()
            {
                Runtime = runtime,
                Work = work,
                Configuration = configuration,
                Routes = routes,
                Caller = caller,
                Batches = batches,
                Processor = new(work, (target, id, caller, token) => runtime.Service.InvokeTriggeredAsync(work, target, id, caller, token), new TriggerDeliveryDispatcher(new DeliveryStore(routes[0]), destinations, TimeProvider.System), batches)
            };
        }

        internal static QueueMessage Batch(string id, params (string Key, string Content)[] orders) => new(id, null, Encoding.UTF8.GetBytes(
            "{\"schema\":\"braid.trigger-batch.v1\",\"orders\":[" + string.Join(",", orders.Select(order =>
                "{\"key\":\"" + order.Key + "\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(order.Content)) + "\"}")) + "]}"));

        internal static QueueMessage Message(string id, string body = "{}", string? contentType = null) => new(id, contentType, Encoding.UTF8.GetBytes(body));
        internal Task<QueueOrderDecision> ProcessAsync(QueueMessage message) => Processor.ProcessAsync(Target, Configuration.TriggerId, Caller, message, default);
    }

    /// <summary>Durable trigger work in memory: admission by exact message ID, the stored pin and request, and recorded outcomes.</summary>
    internal sealed class MemoryTriggerWork(MachineInvocationServiceTests.Fixture runtime, TriggerConfiguration configuration) : ITriggerWorkStore, ITriggeredInvocationLedger
    {
        private readonly object sync = new();
        private readonly Dictionary<string, Entry> bySource = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Entry> byWork = [];
        internal TriggerDefinition? Definition { get; set; } = new(configuration, 1);
        internal TriggerAdmissionStatus? ForcedAdmission { get; set; }
        internal TriggerWorkClaim? ForcedClaim { get; set; }
        /// <summary>An order with exactly this input is recorded as an invalid-input failure when claimed.</summary>
        internal string? FailingInput { get; set; }
        internal int Admitted { get { lock (sync) return byWork.Count; } }
        internal MachineInvocationOutcome? Outcome(Guid work) { lock (sync) return byWork[work].Outcome; }
        internal Guid WorkFor(string message, string? key) { lock (sync) return bySource[message + "\n" + key].Work; }

        public Task<TriggerDefinition?> ReadDefinitionAsync(ReleaseTargetReference target, Guid triggerId, CancellationToken token) => Task.FromResult(Definition);

        public Task<TriggerAdmissionResult> AdmitAsync(TriggerDefinition definition, QueueOrderIdentity identity, MachineCaller caller, ReadOnlyMemory<byte> source,
            Sha256Digest requestDigest, CancellationToken token)
        {
            if (ForcedAdmission is { } forced) return Task.FromResult(new TriggerAdmissionResult(forced));
            lock (sync)
            {
                var raw = Sha256Digest.FromBytes(source.Span);
                if (bySource.TryGetValue(identity.MessageId + "\n" + identity.ChildKey, out var existing))
                    return Task.FromResult(existing.Raw == raw && existing.Application == caller.ApplicationId
                        ? new TriggerAdmissionResult(TriggerAdmissionStatus.Existing, existing.Work) : new(TriggerAdmissionStatus.IdempotencyConflict));
                var entry = new Entry(Guid.NewGuid(), Guid.NewGuid(), raw, caller.ApplicationId, requestDigest, source.ToArray(),
                    new(MachineInvocationProvenance.From(runtime.Release!), runtime.Release!.Actor, 1, Guid.NewGuid()));
                bySource[identity.MessageId + "\n" + identity.ChildKey] = byWork[entry.Work] = entry;
                return Task.FromResult(new TriggerAdmissionResult(TriggerAdmissionStatus.Admitted, entry.Work));
            }
        }

        public Task<TriggerWorkClaim> ClaimPinnedAsync(ReleaseTargetReference target, Guid workId, MachineCaller caller, CancellationToken token)
        {
            if (ForcedClaim is { } forced) return Task.FromResult(forced);
            lock (sync)
            {
                var entry = byWork[workId];
                if (FailingInput is { } failing && Encoding.UTF8.GetString(entry.Input) == failing) entry.Outcome = MachineInvocationOutcome.InvalidInput;
                return Task.FromResult(entry.Outcome is { } outcome
                    ? new TriggerWorkClaim(TriggerClaimStatus.Recorded, outcome: outcome)
                    : new TriggerWorkClaim(TriggerClaimStatus.Claimed, 1, Guid.NewGuid(), entry.Pin, entry.Request, entry.Digest, configuration, entry.Input));
            }
        }

        public Task<TriggerFinishStatus> FinishAndEnqueueAsync(ReleaseTargetReference target, Guid workId, Guid claimId, Guid applicationId,
            MachineInvocationOutcome outcome, MachineInvocationProvenance? provenance, string? clock, byte[]? response, RegressionOutputSet? outputs,
            CancellationToken token)
        {
            lock (sync)
                if (outcome is not (MachineInvocationOutcome.Unavailable or MachineInvocationOutcome.Busy or MachineInvocationOutcome.ReleaseChanged))
                    byWork[workId].Outcome = outcome;
            return Task.FromResult(TriggerFinishStatus.Recorded);
        }

        private sealed record Entry(Guid Work, Guid Request, Sha256Digest Raw, Guid Application, Sha256Digest Digest, byte[] Input, TriggerWorkPin Pin)
        {
            public MachineInvocationOutcome? Outcome { get; set; }
        }
    }

    /// <summary>Batch parents in memory: the manifest and its orders, each admitted as its own keyed work.</summary>
    internal sealed class MemoryBatches(MemoryTriggerWork work) : ITriggerRunStore
    {
        private readonly Dictionary<string, (Sha256Digest Manifest, Guid Application, Guid Run, Guid[] Works)> parents = new(StringComparer.Ordinal);
        internal int Parents => parents.Count;

        public async Task<TriggerBatchAdmission> AdmitBatchAsync(TriggerDefinition definition, string messageId, MachineCaller caller, Sha256Digest manifestDigest,
            IReadOnlyList<TriggerDiscoveredOrder> orders, CancellationToken token)
        {
            if (parents.TryGetValue(messageId, out var parent))
                return parent.Manifest == manifestDigest && parent.Application == caller.ApplicationId
                    ? new(TriggerAdmissionStatus.Existing, parent.Run, parent.Works) : new(TriggerAdmissionStatus.IdempotencyConflict);
            var works = new List<Guid>();
            foreach (var order in orders)
                works.Add((await work.AdmitAsync(definition, order.Identity, caller, order.CopyContent(), order.RequestDigest, token)).WorkId!.Value);
            parents[messageId] = (manifestDigest, caller.ApplicationId, Guid.NewGuid(), [.. works]);
            return new(TriggerAdmissionStatus.Admitted, parents[messageId].Run, works);
        }

        public Task<TriggerScheduleState?> ReadScheduleAsync(ReleaseTargetReference target, Guid triggerId, CancellationToken token) => throw new NotSupportedException();
        public Task<TriggerRunResumption> ResumeRunAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, CancellationToken token) => throw new NotSupportedException();
        public Task<TriggerOccurrenceResult> RecordOccurrencesAsync(TriggerDefinition definition, MachineCaller caller, IReadOnlyList<ScheduledOccurrence> missed,
            ScheduledOccurrence? run, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ReadKnownSourcesAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, IReadOnlyList<string> sources,
            CancellationToken token) => throw new NotSupportedException();
        public Task<TriggerPageResult> RecordPageAsync(TriggerDefinition definition, MachineCaller caller, TriggerRunClaim claim, string? cursorBefore, string? cursorAfter,
            bool complete, TriggerScanCounts counts, IReadOnlyList<TriggerDiscoveredOrder> orders, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> ReadPendingWorkAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, int maximum, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class DeliveryStore(TriggerRoute route, TimeSpan? window = null) : ITriggerDeliveryStore
    {
        internal TriggerRoute Route => route;
        internal List<DeliveryObservationKind> Observations { get; } = [];
        public Task<TriggerDeliveryClaim> ClaimAsync(ReleaseTargetReference target, Guid destinationBindingId, CancellationToken token) =>
            Task.FromResult(new TriggerDeliveryClaim(TriggerDeliveryClaimStatus.Claimed, Guid.NewGuid(), 1, DateTimeOffset.UtcNow + (window ?? TimeSpan.FromSeconds(30)),
                Delivery(route.Connector.Version), route));
        public Task<TriggerObservationStatus> RecordObservationAsync(ReleaseTargetReference target, Guid deliveryId, Guid claimId, DeliveryObservationKind kind,
            CancellationToken token)
        {
            Observations.Add(kind);
            return Task.FromResult(TriggerObservationStatus.Recorded);
        }
    }

    private sealed class Destination(TriggerConnectorReference connector, Func<FrozenTriggerDelivery, CancellationToken, Task<DeliveryObservationKind>> send) : IOutputDestination
    {
        internal int Sends;
        public TriggerDestinationKind Kind => TriggerDestinationKind.Blob;
        public TriggerConnectorReference Connector => connector;
        public Task<DeliveryObservationKind> SendAsync(FrozenTriggerDelivery delivery, CancellationToken token) { Interlocked.Increment(ref Sends); return send(delivery, token); }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan by) => now += by;
    }

}
