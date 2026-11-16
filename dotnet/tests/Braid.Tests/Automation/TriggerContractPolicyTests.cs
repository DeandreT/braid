using System.Text;
using Braid.Application;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;

namespace Braid.Tests;

public sealed class TriggerContractPolicyTests
{
    private static readonly DateTimeOffset Clock = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Binding = Guid.NewGuid(), Application = Guid.NewGuid();
    private static readonly ReleaseTargetReference Target = NewTarget();
    private static readonly MachinePrincipal Principal = new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid());
    private static readonly Sha256Digest BodyDigest = Sha256Digest.FromBytes("source"u8);

    [Fact]
    public void Duplicate_reuses_original_request_digest_and_pin_without_rebuilding_from_current_configuration()
    {
        var original = Work(Identity());
        var duplicate = new QueueOrderIdentity(Binding, "Message-A", Target, Principal, QueueOrderKind.SingleOrder, null);
        var first = QueueOrderAdmission.Inspect(duplicate, BodyDigest, Application, null);
        Assert.Equal(QueueOrderAdmissionKind.NeedsDurableAdmission, first.Kind);
        Assert.Null(first.StoredWork);
        var result = QueueOrderAdmission.Inspect(duplicate, BodyDigest, Application, original);
        Assert.Equal(QueueOrderAdmissionKind.ReuseStoredWork, result.Kind);
        Assert.Same(original, result.StoredWork);
        Assert.Equal(original.RequestId, result.StoredWork!.RequestId);
        Assert.Same(original.RequestDigest, result.StoredWork.RequestDigest);
        Assert.Same(original.Pin, result.StoredWork.Pin);
        // A config/run change is not an input to source lookup. A newer pin/request must not replace the retained one.
        var newerConfiguration = Work(duplicate);
        Assert.NotEqual(newerConfiguration.RequestId, original.RequestId);
        Assert.NotEqual(newerConfiguration.Pin, original.Pin);
        Assert.Same(original, QueueOrderAdmission.Inspect(duplicate, BodyDigest, Application, original).StoredWork);
    }

    [Fact]
    public void Changed_body_or_application_conflicts_without_mutating_original_work()
    {
        var original = Work(Identity());
        foreach (var result in new[]
        {
            QueueOrderAdmission.Inspect(original.Identity, Sha256Digest.FromBytes("changed"u8), Application, original),
            QueueOrderAdmission.Inspect(original.Identity, BodyDigest, Guid.NewGuid(), original)
        })
        {
            Assert.Equal(QueueOrderAdmissionKind.IdempotencyConflict, result.Kind);
            Assert.Null(result.StoredWork);
        }
        Assert.Equal(BodyDigest, original.RawSourceDigest);
        Assert.Equal(Application, original.ApplicationId);
        Assert.Same(original, QueueOrderAdmission.Inspect(original.Identity, BodyDigest, Application, original).StoredWork);
    }

    [Fact]
    public void Equal_broker_ids_are_independent_across_binding_target_principal_and_explicit_order_key()
    {
        var original = Identity();
        QueueOrderIdentity[] independent =
        [
            new(Guid.NewGuid(), original.MessageId, Target, Principal, QueueOrderKind.SingleOrder, null),
            new(Binding, original.MessageId, NewTarget(), Principal, QueueOrderKind.SingleOrder, null),
            new(Binding, original.MessageId, Target, new(MachinePrincipal.Entra, Principal.TenantId, Guid.NewGuid()), QueueOrderKind.SingleOrder, null),
            Identity("message-a"), Identity(" Message-A"), Identity("Message-A "),
            Identity(child: "A"), Identity(child: "a"), Identity(child: " A"), Identity(child: "A ")
        ];
        Assert.Equal(independent.Length, independent.Distinct().Count());
        foreach (var identity in independent)
        {
            Assert.NotEqual(original, identity);
            Assert.Equal(QueueOrderAdmissionKind.NeedsDurableAdmission, QueueOrderAdmission.Inspect(identity, BodyDigest, Application, null).Kind);
        }
        Assert.Throws<ArgumentException>(() => QueueOrderAdmission.Inspect(independent[0], BodyDigest, Application, Work(original)));
    }

    [Fact]
    public void Identity_bounds_use_exact_valid_unicode_utf16_for_message_id_and_utf8_for_child_key()
    {
        Assert.Equal(128, Identity(new string('a', 128)).MessageId.Length);
        Assert.Equal(128, Identity(string.Concat(Enumerable.Repeat("\U0001F600", 64))).MessageId.Length);
        Assert.Equal(" ", Identity(" ").MessageId);
        Assert.NotEqual(Identity("\u00e9"), Identity("e\u0301"));
        Assert.Equal(256, Encoding.UTF8.GetByteCount(Identity(child: new string('\u00e9', 128)).ChildKey!));
        Assert.Equal(256, Encoding.UTF8.GetByteCount(Identity(child: string.Concat(Enumerable.Repeat("\U0001F600", 64))).ChildKey!));
        foreach (var invalid in new[] { "", new string('a', 129), "private-\ud800", "private-\udfff" })
            Assert.Equal("Invalid queue order identity.", Assert.Throws<ArgumentException>(() => Identity(invalid)).Message);
        foreach (var invalid in new[] { "", new string('a', 257), new string('\u00e9', 129), "private-\ud800" })
            Assert.Equal("Invalid queue order identity.", Assert.Throws<ArgumentException>(() => Identity(child: invalid)).Message);
        Assert.Throws<ArgumentException>(() => new QueueOrderIdentity(Guid.Empty, "id", Target, Principal, QueueOrderKind.SingleOrder, null));
        Assert.Throws<ArgumentException>(() => new QueueOrderIdentity(Binding, "id", Target, Principal, (QueueOrderKind)99, null));
        Assert.Throws<ArgumentException>(() => new QueueOrderIdentity(Binding, "id", Target, Principal, QueueOrderKind.SingleOrder, "child"));
        Assert.Throws<ArgumentException>(() => new QueueOrderIdentity(Binding, "id", Target, Principal, QueueOrderKind.KeyedChild, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Per_order_decisions_keep_successful_siblings_and_correct_only_the_explicit_failed_order(int failingPosition)
    {
        var orders = Enumerable.Range(0, 3).Select(index => Work(Identity(child: "order-" + index))).ToArray();
        var results = orders.Select((work, index) => index == failingPosition ? Failed(work) : Completed(work)).ToArray();
        var dispositions = results.Select((result, index) => TriggerOrderDisposition.Classify(result, orders[index].Pin)).ToArray();
        for (var index = 0; index < orders.Length; index++)
        {
            Assert.Equal(index == failingPosition ? TriggerOrderAction.RecordedFailure : TriggerOrderAction.RecoverDurableOutputs, dispositions[index]);
            Assert.Same(orders[index], QueueOrderAdmission.Inspect(orders[index].Identity, BodyDigest, Application, orders[index]).StoredWork);
            if (index != failingPosition)
                Assert.Throws<ArgumentException>(() => new CorrectedQueueOrderIdentity(Identity("correction", "order-" + index), orders[index], Bound(orders[index], results[index])));
        }
        var failed = orders[failingPosition];
        var correction = new CorrectedQueueOrderIdentity(Identity("correction", failed.Identity.ChildKey), failed, Bound(failed, results[failingPosition]));
        Assert.Equal(failed.WorkId, correction.FailedWorkId);
        Assert.Equal(failed.RequestId, correction.FailedRequestId);
        Assert.NotEqual(failed.Identity, correction.Identity);
        Assert.Equal(QueueOrderAdmissionKind.NeedsDurableAdmission, QueueOrderAdmission.Inspect(correction.Identity, Sha256Digest.FromBytes("fixed"u8), Application, null).Kind);
        Assert.Equal(QueueOrderAdmissionKind.IdempotencyConflict, QueueOrderAdmission.Inspect(failed.Identity, Sha256Digest.FromBytes("fixed"u8), Application, failed).Kind);
        Assert.Throws<ArgumentException>(() => new CorrectedQueueOrderIdentity(failed.Identity, failed, Bound(failed, results[failingPosition])));
        Assert.Throws<ArgumentException>(() => new CorrectedQueueOrderIdentity(Identity("correction", failed.Identity.ChildKey), failed, Bound(failed, new(MachineInvocationStatus.Unavailable))));
    }

    [Fact]
    public void A_failed_siblings_observation_cannot_be_used_to_correct_successful_work_on_the_same_release()
    {
        var successful = Work(Identity(child: "successful"));
        var failed = new StoredQueueOrderWork(Guid.NewGuid(), Identity(child: "failed"), BodyDigest, Application, Guid.NewGuid(),
            successful.RequestDigest, successful.Pin);
        var failedObservation = Bound(failed, Failed(failed));
        var successfulCorrection = Identity("correction", successful.Identity.ChildKey);
        Assert.Equal(TriggerOrderAction.RecoverDurableOutputs, Bound(successful, Completed(successful)).ClassifyFor(successful));
        Assert.Throws<ArgumentException>(() => new CorrectedQueueOrderIdentity(successfulCorrection, successful, failedObservation));
        Assert.Equal(TriggerOrderAction.RecordedFailure, failedObservation.ClassifyFor(failed));
        Assert.Equal(failed.WorkId, new CorrectedQueueOrderIdentity(Identity("correction", failed.Identity.ChildKey), failed, failedObservation).FailedWorkId);
        var changedDigest = new RetainedTriggerInvocationObservation(failed.WorkId, failed.RequestId, Sha256Digest.FromBytes("different"u8), failed.Pin, Failed(failed));
        Assert.Throws<ArgumentException>(() => new CorrectedQueueOrderIdentity(Identity("correction", failed.Identity.ChildKey), failed, changedDigest));
        Assert.Throws<ArgumentException>(() => new RetainedTriggerInvocationObservation(Guid.NewGuid(), failed.RequestId, failed.RequestDigest, failed.Pin, Failed(failed)).ClassifyFor(failed));
        Assert.Throws<ArgumentException>(() => new RetainedTriggerInvocationObservation(failed.WorkId, Guid.NewGuid(), failed.RequestDigest, failed.Pin, Failed(failed)).ClassifyFor(failed));
        Assert.Throws<ArgumentException>(() => new RetainedTriggerInvocationObservation(failed.WorkId, failed.RequestId, failed.RequestDigest, Work(Identity()).Pin, Failed(failed)).ClassifyFor(failed));
        Assert.Throws<ArgumentException>(() => new RetainedTriggerInvocationObservation(Guid.Empty, failed.RequestId, failed.RequestDigest, failed.Pin, Failed(failed)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_or_replayed_mapping_recovers_durable_outputs_and_expired_success_never_becomes_a_fresh_request(bool replayed)
    {
        var work = Work(Identity());
        var response = "private response"u8.ToArray();
        var result = new MachineInvocationResult(MachineInvocationStatus.Completed, replayed, MachineInvocationOutcome.Completed, response, work.Pin.Provenance.ReleaseId);
        Assert.Equal(TriggerOrderAction.RecoverDurableOutputs, TriggerOrderDisposition.Classify(result, work.Pin));
        Assert.Equal("private response"u8.ToArray(), response);
        Assert.Equal(TriggerOrderAction.PayloadUnavailable, TriggerOrderDisposition.Classify(new(MachineInvocationStatus.Expired, replayed,
            MachineInvocationOutcome.Completed, ReleaseId: work.Pin.Provenance.ReleaseId), work.Pin));
    }

    [Theory]
    [InlineData(MachineInvocationStatus.Unavailable, TriggerOrderAction.RecoverOriginal)]
    [InlineData(MachineInvocationStatus.InProgress, TriggerOrderAction.RecoverOriginal)]
    [InlineData(MachineInvocationStatus.NoRelease, TriggerOrderAction.RecoverOriginal)]
    [InlineData(MachineInvocationStatus.Refused, TriggerOrderAction.Refused)]
    [InlineData(MachineInvocationStatus.Conflict, TriggerOrderAction.IdempotencyConflict)]
    public void Unconfirmed_results_recover_original_or_retain_the_honest_refusal(MachineInvocationStatus status, TriggerOrderAction expected)
    {
        var work = Work(Identity());
        Assert.Equal(expected, TriggerOrderDisposition.Classify(new(status), work.Pin));
        Assert.Equal(TriggerOrderAction.RecoverOriginal, TriggerOrderDisposition.Classify(null, work.Pin));
        Assert.Equal(TriggerOrderAction.Refused, TriggerOrderDisposition.Classify(new(MachineInvocationStatus.Failed, Outcome: MachineInvocationOutcome.Refused), work.Pin));
        Assert.Equal(TriggerOrderAction.ReleaseSuperseded, TriggerOrderDisposition.Classify(Completed(work) with { ReleaseId = Guid.NewGuid() }, work.Pin));
        Assert.Equal(TriggerOrderAction.ReleaseSuperseded, TriggerOrderDisposition.ObserveActiveRelease(work.Pin, Guid.NewGuid()));
        Assert.Equal(TriggerOrderAction.RecoverOriginal, TriggerOrderDisposition.ObserveActiveRelease(work.Pin, work.Pin.Provenance.ReleaseId));
        Assert.Equal(TriggerOrderAction.RecoverOriginal, TriggerOrderDisposition.ObserveActiveRelease(work.Pin, null));
    }

    [Theory]
    [InlineData(MachineInvocationOutcome.Unavailable, TriggerOrderAction.RecoverOriginal)]
    [InlineData(MachineInvocationOutcome.Busy, TriggerOrderAction.RecoverOriginal)]
    [InlineData(MachineInvocationOutcome.ReleaseChanged, TriggerOrderAction.ReleaseSuperseded)]
    public void Existing_service_transient_outcomes_recover_original_or_fence_its_release(MachineInvocationOutcome outcome, TriggerOrderAction expected)
    {
        var work = Work(Identity());
        Assert.Equal(expected, TriggerOrderDisposition.Classify(new(MachineInvocationStatus.Unavailable, Outcome: outcome), work.Pin));
    }

    [Fact]
    public void Invalid_invocation_observations_and_unbounded_pins_are_refused_with_fixed_errors()
    {
        var work = Work(Identity());
        MachineInvocationResult[] invalid =
        [
            new((MachineInvocationStatus)99), new(MachineInvocationStatus.Failed, Outcome: (MachineInvocationOutcome)99),
            new(MachineInvocationStatus.Completed, Outcome: MachineInvocationOutcome.Completed),
            new(MachineInvocationStatus.Expired, Outcome: MachineInvocationOutcome.InvalidInput),
            new(MachineInvocationStatus.Unavailable, Response: "private"u8.ToArray()),
            new(MachineInvocationStatus.Unavailable, Replayed: true), new(MachineInvocationStatus.Failed, Outcome: MachineInvocationOutcome.Busy),
            new(MachineInvocationStatus.InProgress, ReleaseId: Guid.Empty)
        ];
        foreach (var result in invalid)
            Assert.Equal("Invalid trigger invocation observation.", Assert.Throws<ArgumentException>(() => TriggerOrderDisposition.Classify(result, work.Pin)).Message);
        Assert.Throws<ArgumentException>(() => TriggerOrderDisposition.ObserveActiveRelease(work.Pin, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TriggerWorkPin(work.Pin.Provenance, work.Pin.Publisher, 0, work.Pin.InvocationGrantId));
        Assert.Throws<ArgumentException>(() => new TriggerWorkPin(work.Pin.Provenance, work.Pin.Publisher, 1, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TriggerWorkPin(work.Pin.Provenance with { ReleaseId = Guid.Empty }, work.Pin.Publisher, 1, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TriggerWorkPin(work.Pin.Provenance with { RuntimeImage = "private" }, work.Pin.Publisher, 1, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new StoredQueueOrderWork(Guid.Empty, work.Identity, BodyDigest, Application, work.RequestId, work.RequestDigest, work.Pin));
        Assert.Throws<ArgumentException>(() => QueueOrderAdmission.Inspect(work.Identity, BodyDigest, Guid.Empty, null));
    }

    [Fact]
    public void Known_non_acceptance_uses_explicit_delay_and_clock_without_mutating_either()
    {
        var delivery = Delivery();
        var delays = new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) };
        var policy = new DeliveryRetryPolicy(3, delays, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
        delays[0] = TimeSpan.Zero;
        var observation = Observation(DeliveryObservationKind.ProvenKnownNotAccepted);
        var decision = Evaluate(delivery, observation, policy);
        Assert.Equal(DeliveryRetryDisposition.Eligible, decision.Disposition);
        Assert.Equal(Clock.AddSeconds(10), decision.NextAt);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.RetryDelays[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<TimeSpan>)policy.RetryDelays)[0] = TimeSpan.Zero);
        Assert.Equal(Clock.AddSeconds(30), Evaluate(delivery, observation, policy, now: Clock.AddSeconds(30)).NextAt);
        Assert.Equal(Clock, observation.ObservedAt);
        Assert.Throws<ArgumentException>(() => Evaluate(delivery, observation, policy, now: Clock.AddTicks(-1)));
        Assert.Equal(Clock, Evaluate(delivery, Observation(DeliveryObservationKind.NotAttempted, 0), Policy()).NextAt);
    }

    [Fact]
    public void Unknown_delivery_requires_matching_coverage_of_original_ambiguity_and_entire_next_attempt_window()
    {
        var delivery = Delivery();
        var unknown = Observation(DeliveryObservationKind.Unknown);
        var policy = Policy(); // The second send starts at +10s and has an explicitly selected 5s window.
        Assert.Equal(DeliveryRetryDisposition.ReconciliationRequired, Evaluate(delivery, unknown, policy).Disposition);
        foreach (var coverage in new[]
        {
            new ReceiverDeduplicationGuarantee(delivery, Clock.AddSeconds(-29), Clock.AddSeconds(15)), // Original dispatch was at -30s.
            new ReceiverDeduplicationGuarantee(delivery, Clock.AddMinutes(-1), Clock.AddSeconds(10)), // Retry instant alone is insufficient.
            new ReceiverDeduplicationGuarantee(delivery, Clock.AddMinutes(-1), Clock.AddSeconds(15).AddTicks(-1)),
            new ReceiverDeduplicationGuarantee(Delivery(delivery, version: 2), Clock.AddMinutes(-1), Clock.AddHours(1))
        })
        {
            var blocked = Evaluate(delivery, unknown, policy, coverage);
            Assert.Equal(DeliveryRetryDisposition.ReconciliationRequired, blocked.Disposition);
            Assert.Null(blocked.NextAt);
        }
        var guarantee = new ReceiverDeduplicationGuarantee(delivery, Clock.AddSeconds(-30), Clock.AddSeconds(15));
        var eligible = Evaluate(delivery, unknown, policy, guarantee);
        Assert.Equal(DeliveryRetryDisposition.Eligible, eligible.Disposition);
        Assert.Equal(Clock.AddSeconds(10), eligible.NextAt);
        Assert.Equal(DeliveryRetryDisposition.ReconciliationRequired, Evaluate(delivery, unknown, policy, guarantee, Clock.AddSeconds(11)).Disposition);
        var laterNonAcceptance = unknown.AfterAttempt(DeliveryObservationKind.ProvenKnownNotAccepted, Clock.AddSeconds(1), Clock.AddSeconds(2));
        Assert.Equal(unknown.FirstAmbiguousDispatchAt, laterNonAcceptance.FirstAmbiguousDispatchAt);
        Assert.Equal(DeliveryRetryDisposition.ReconciliationRequired, Evaluate(delivery, laterNonAcceptance, policy, now: Clock.AddSeconds(2)).Disposition);
    }

    [Fact]
    public void Entire_attempt_window_must_fit_the_fixed_explicit_lifetime_from_original_admission()
    {
        var delivery = Delivery();
        var observation = Observation(DeliveryObservationKind.ProvenKnownNotAccepted);
        var policy = new DeliveryRetryPolicy(3, [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(75));
        Assert.Equal(Clock.AddSeconds(10), Evaluate(delivery, observation, policy).NextAt); // The entire attempt ends exactly at the original deadline, +15s.
        var tooLate = Evaluate(delivery, observation, policy, now: Clock.AddSeconds(10).AddTicks(1));
        Assert.Equal(DeliveryRetryDisposition.PolicyExpired, tooLate.Disposition);
        Assert.Null(tooLate.NextAt);
        Assert.Equal(DeliveryRetryDisposition.PolicyExpired, Evaluate(delivery, observation, policy, now: Clock.AddYears(10)).Disposition);
        Assert.Equal(DeliveryRetryDisposition.PolicyExpired, Evaluate(delivery, observation, policy, now: Clock.AddSeconds(15)).Disposition);
        var laterKnownNotAccepted = Observation(DeliveryObservationKind.Unknown).AfterAttempt(DeliveryObservationKind.ProvenKnownNotAccepted,
            Clock.AddSeconds(1), Clock.AddSeconds(2));
        Assert.Equal(Clock.AddSeconds(-30), laterKnownNotAccepted.FirstAmbiguousDispatchAt);
        Assert.Equal(DeliveryRetryDisposition.PolicyExpired, Evaluate(delivery, laterKnownNotAccepted, policy, now: Clock.AddSeconds(2)).Disposition);
        var resetAdmission = Delivery(delivery, admittedAt: Clock);
        Assert.Equal(DeliveryRetryDisposition.IdentityConflict, DeliveryRetryEligibility.Evaluate(delivery, resetAdmission, observation, policy, Clock, null).Disposition);
        Assert.Equal(Clock.AddMinutes(-1), delivery.AdmittedAt);
    }

    [Theory]
    [InlineData(DeliveryObservationKind.Accepted, DeliveryRetryDisposition.Accepted)]
    [InlineData(DeliveryObservationKind.PermanentFailure, DeliveryRetryDisposition.PermanentFailure)]
    [InlineData(DeliveryObservationKind.Fenced, DeliveryRetryDisposition.Fenced)]
    [InlineData(DeliveryObservationKind.PayloadUnavailable, DeliveryRetryDisposition.PayloadUnavailable)]
    public void Accepted_fenced_missing_or_terminal_delivery_never_retries(DeliveryObservationKind observation, DeliveryRetryDisposition expected)
    {
        var delivery = Delivery();
        var result = Evaluate(delivery, Observation(observation), Policy());
        Assert.Equal(expected, result.Disposition);
        Assert.Null(result.NextAt);
    }

    [Theory]
    [InlineData(DeliveryObservationKind.Unknown)]
    [InlineData(DeliveryObservationKind.ProvenKnownNotAccepted)]
    public void Exhaustion_never_gets_an_extra_send(DeliveryObservationKind kind)
    {
        var delivery = Delivery();
        var result = Evaluate(delivery, Observation(kind, 3), Policy(), new(delivery, Clock.AddHours(-1), Clock.AddHours(1)));
        Assert.Equal(DeliveryRetryDisposition.Exhausted, result.Disposition);
        Assert.Null(result.NextAt);
    }

    [Fact]
    public void Delivery_identity_freezes_all_bytes_ids_binding_version_digest_length_and_media()
    {
        var bytes = "original"u8.ToArray();
        var original = new FrozenTriggerDelivery(Guid.NewGuid(), Guid.NewGuid(), Binding, 1, Clock.AddMinutes(-1), bytes, Sha256Digest.FromBytes(bytes), bytes.Length, "application/json");
        bytes[0] = (byte)'x';
        var copy = original.CopyContent(); copy[0] = (byte)'x';
        Assert.Equal("original"u8.ToArray(), original.CopyContent());
        Assert.True(original.Matches(Delivery(original)));
        FrozenTriggerDelivery[] changed =
        [
            Delivery(original, deliveryId: Guid.NewGuid()), Delivery(original, outputId: Guid.NewGuid()),
            Delivery(original, bindingId: Guid.NewGuid()), Delivery(original, version: 2),
            Delivery(original, bytes: "changed!"u8.ToArray()), Delivery(original, bytes: "longer content"u8.ToArray()),
            Delivery(original, media: "text/plain"), Delivery(original, media: "Application/Json")
        ];
        foreach (var candidate in changed)
        {
            var result = DeliveryRetryEligibility.Evaluate(original, candidate, Observation(DeliveryObservationKind.Unknown), Policy(), Clock,
                new(original, Clock.AddHours(-1), Clock.AddHours(1)));
            Assert.Equal(DeliveryRetryDisposition.IdentityConflict, result.Disposition);
            Assert.Null(result.NextAt);
        }
        Assert.Throws<ArgumentException>(() => new FrozenTriggerDelivery(original.DeliveryId, original.OutputReceiptId, Binding, 1, original.AdmittedAt,
            original.CopyContent(), BodyDigest, original.ContentLength, original.MediaType));
        Assert.Throws<ArgumentException>(() => new FrozenTriggerDelivery(original.DeliveryId, original.OutputReceiptId, Binding, 1, original.AdmittedAt,
            original.CopyContent(), original.ContentDigest, original.ContentLength + 1, original.MediaType));
        Assert.Throws<ArgumentException>(() => Delivery(original, media: "private\r\nheader"));
        Assert.Throws<ArgumentException>(() => Delivery(original, media: "*/*"));
        Assert.Throws<ArgumentException>(() => Delivery(original, version: 0));
        Assert.Throws<ArgumentException>(() => Delivery(original, deliveryId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Delivery(original, outputId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Delivery(original, bindingId: Guid.Empty));
        Assert.Equal(0, Delivery(bytes: []).ContentLength);
        Assert.Equal(MachineInvocationPolicy.MaximumRetainedBytes, Delivery(bytes: new byte[MachineInvocationPolicy.MaximumRetainedBytes]).ContentLength);
        Assert.Throws<ArgumentException>(() => Delivery(bytes: new byte[MachineInvocationPolicy.MaximumRetainedBytes + 1]));
    }

    [Fact]
    public void Retry_policy_is_required_finite_positive_bounded_and_defensively_copied()
    {
        var delivery = Delivery();
        Assert.Throws<ArgumentNullException>(() => DeliveryRetryEligibility.Evaluate(delivery, delivery, Observation(DeliveryObservationKind.Unknown), null!, Clock, null));
        Assert.Throws<ArgumentNullException>(() => new DeliveryRetryPolicy(1, null!, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(0, [], TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(33, Enumerable.Repeat(TimeSpan.FromTicks(1), 32).ToArray(), TimeSpan.FromTicks(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(2, [], TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [TimeSpan.FromSeconds(1)], TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(2, [TimeSpan.Zero], TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(2, [TimeSpan.MinValue], TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [], TimeSpan.Zero, TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [], TimeSpan.MaxValue, TimeSpan.MaxValue));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(32, Enumerable.Repeat(TimeSpan.MaxValue, 31).ToArray(), TimeSpan.MaxValue, TimeSpan.MaxValue));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [], TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [], TimeSpan.FromSeconds(1), TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(999)));
        Assert.Throws<ArgumentException>(() => new DeliveryRetryPolicy(1, [], TimeSpan.FromSeconds(1), TimeSpan.MaxValue));
        // TimeSpan has integer ticks. Non-finite floating inputs fail before they can become policy values.
        Assert.Throws<ArgumentException>(() => TimeSpan.FromSeconds(double.NaN));
        Assert.Throws<OverflowException>(() => TimeSpan.FromSeconds(double.PositiveInfinity));
        Assert.Equal(1, new DeliveryRetryPolicy(1, [], TimeSpan.FromTicks(1), TimeSpan.FromTicks(1)).TotalSends);
        Assert.Equal(31, new DeliveryRetryPolicy(32, Enumerable.Repeat(TimeSpan.FromTicks(1), 31).ToArray(), TimeSpan.FromTicks(1), TimeSpan.FromMinutes(10)).RetryDelays.Count);
    }

    [Fact]
    public void Invalid_observations_deduplication_windows_and_calendar_overflows_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => new DeliveryObservation((DeliveryObservationKind)99, 1, Clock, Clock, null));
        Assert.Throws<ArgumentException>(() => Observation(DeliveryObservationKind.Unknown, 0));
        Assert.Throws<ArgumentException>(() => Observation(DeliveryObservationKind.NotAttempted, 1));
        Assert.Throws<ArgumentException>(() => Observation(DeliveryObservationKind.Accepted, 33));
        Assert.Throws<ArgumentException>(() => new DeliveryObservation(DeliveryObservationKind.Unknown, 1, Clock, Clock, null));
        Assert.Throws<ArgumentException>(() => new DeliveryObservation(DeliveryObservationKind.Unknown, 1, Clock, Clock, Clock.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => new DeliveryObservation(DeliveryObservationKind.ProvenKnownNotAccepted, 1, Clock, Clock.AddTicks(1), null));
        var delivery = Delivery();
        Assert.Throws<ArgumentException>(() => new ReceiverDeduplicationGuarantee(delivery, Clock, Clock));
        var nearEnd = DateTimeOffset.MaxValue.AddSeconds(-1);
        var observation = new DeliveryObservation(DeliveryObservationKind.ProvenKnownNotAccepted, 1, nearEnd, nearEnd, null);
        var nearEndDelivery = Delivery(admittedAt: nearEnd);
        Assert.Equal("Delivery lifetime exceeds the supported clock range.", Assert.Throws<ArgumentException>(() =>
            Evaluate(nearEndDelivery, observation, Policy(), now: nearEnd)).Message);
        var firstSend = new DeliveryObservation(DeliveryObservationKind.NotAttempted, 0, nearEnd, null, null);
        Assert.Throws<ArgumentException>(() => Evaluate(nearEndDelivery, firstSend, Policy(), now: nearEnd));
        Assert.Throws<ArgumentException>(() => Evaluate(Delivery(admittedAt: Clock.AddTicks(1)), Observation(DeliveryObservationKind.NotAttempted, 0), Policy()));
        Assert.Throws<ArgumentException>(() => Evaluate(Delivery(admittedAt: Clock.AddSeconds(-1)), Observation(DeliveryObservationKind.Unknown), Policy()));
        Assert.Throws<ArgumentException>(() => Observation(DeliveryObservationKind.Unknown).AfterAttempt(DeliveryObservationKind.ProvenKnownNotAccepted, Clock.AddTicks(-1), Clock));
        var shortLifetime = new DeliveryRetryPolicy(2, [TimeSpan.FromSeconds(10)], TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1));
        var calendarEdge = Delivery(admittedAt: DateTimeOffset.MaxValue.AddSeconds(-2));
        Assert.Equal("Delivery attempt window exceeds the supported clock range.", Assert.Throws<ArgumentException>(() => Evaluate(calendarEdge,
            new(DeliveryObservationKind.ProvenKnownNotAccepted, 1, DateTimeOffset.MaxValue.AddSeconds(-2), DateTimeOffset.MaxValue.AddSeconds(-2), null),
            shortLifetime, now: DateTimeOffset.MaxValue.AddSeconds(-2))).Message);
        var offsetClock = Clock.ToOffset(TimeSpan.FromHours(7));
        Assert.Equal(Clock.AddSeconds(10), Evaluate(delivery, Observation(DeliveryObservationKind.ProvenKnownNotAccepted), Policy(), now: offsetClock).NextAt);
        Assert.Equal(TimeSpan.Zero, Evaluate(delivery, Observation(DeliveryObservationKind.ProvenKnownNotAccepted), Policy(), now: offsetClock).NextAt!.Value.Offset);
    }

    [Fact]
    public void Contract_diagnostics_do_not_disclose_source_keys_response_content_or_digests()
    {
        var work = Work(Identity("private-source", "private-child"));
        var delivery = Delivery(bytes: "private-output"u8.ToArray());
        object[] contracts =
        [
            work.Identity, work.Pin, work, QueueOrderAdmission.Inspect(work.Identity, BodyDigest, Application, work),
            Bound(work, Failed(work)), new CorrectedQueueOrderIdentity(Identity("private-correction", "private-child"), work, Bound(work, Failed(work))),
            delivery, Policy(), Observation(DeliveryObservationKind.Unknown), new ReceiverDeduplicationGuarantee(delivery, Clock.AddHours(-1), Clock.AddHours(1)),
            Evaluate(delivery, Observation(DeliveryObservationKind.Unknown), Policy())
        ];
        foreach (var contract in contracts) Assert.Equal(contract.GetType().Name, contract.ToString());
    }

    private static QueueOrderIdentity Identity(string message = "Message-A", string? child = null) =>
        new(Binding, message, Target, Principal, child is null ? QueueOrderKind.SingleOrder : QueueOrderKind.KeyedChild, child);

    private static StoredQueueOrderWork Work(QueueOrderIdentity identity) => new(Guid.NewGuid(), identity, BodyDigest, Application, Guid.NewGuid(),
        Sha256Digest.FromBytes("original-request-with-frozen-parameters"u8), new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BodyDigest,
            BodyDigest, Guid.NewGuid(), "sha256:" + new string('a', 64)), new(Guid.NewGuid()), 1, Guid.NewGuid()));

    private static MachineInvocationResult Completed(StoredQueueOrderWork work) => new(MachineInvocationStatus.Completed, Outcome: MachineInvocationOutcome.Completed,
        Response: "retained"u8.ToArray(), ReleaseId: work.Pin.Provenance.ReleaseId);
    private static MachineInvocationResult Failed(StoredQueueOrderWork work) => new(MachineInvocationStatus.Failed, Outcome: MachineInvocationOutcome.InvalidInput,
        ReleaseId: work.Pin.Provenance.ReleaseId);

    private static RetainedTriggerInvocationObservation Bound(StoredQueueOrderWork work, MachineInvocationResult result) =>
        new(work.WorkId, work.RequestId, work.RequestDigest, work.Pin, result);

    private static DeliveryRetryPolicy Policy() => new(3, [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
    private static DeliveryObservation Observation(DeliveryObservationKind kind, int sends = 1) => new(kind, sends, Clock,
        sends == 0 ? null : Clock.AddSeconds(-1), kind == DeliveryObservationKind.Unknown ? Clock.AddSeconds(-30) : null);

    private static FrozenTriggerDelivery Delivery(FrozenTriggerDelivery? original = null, Guid? deliveryId = null, Guid? outputId = null,
        Guid? bindingId = null, long? version = null, byte[]? bytes = null, string? media = null, DateTimeOffset? admittedAt = null)
    {
        bytes ??= original?.CopyContent() ?? "output"u8.ToArray();
        return new(deliveryId ?? original?.DeliveryId ?? Guid.NewGuid(), outputId ?? original?.OutputReceiptId ?? Guid.NewGuid(),
            bindingId ?? original?.DestinationBindingId ?? Binding, version ?? original?.DestinationVersion ?? 1, admittedAt ?? original?.AdmittedAt ?? Clock.AddMinutes(-1),
            bytes, Sha256Digest.FromBytes(bytes), bytes.Length, media ?? original?.MediaType ?? "application/json");
    }

    private static DeliveryRetryEligibility Evaluate(FrozenTriggerDelivery delivery, DeliveryObservation observation, DeliveryRetryPolicy policy,
        ReceiverDeduplicationGuarantee? deduplication = null, DateTimeOffset? now = null) =>
        DeliveryRetryEligibility.Evaluate(delivery, delivery, observation, policy, now ?? Clock, deduplication);

    private static ReleaseTargetReference NewTarget()
    {
        var organization = Guid.NewGuid(); var project = Guid.NewGuid();
        return new(new(organization, project, Guid.NewGuid()), new(new(organization, project), Guid.NewGuid(), 1), Guid.NewGuid());
    }
}
