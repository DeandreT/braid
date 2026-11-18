using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment.Triggers;

/// <summary>How a received broker message is settled. Complete follows only the durable output/outbox handoff.</summary>
public enum QueueSettlement { Complete, DeadLetter, Abandon }

/// <summary>
/// A dead letter carries one fixed reason and, when known, Braid's private work ID; never payload, parameters or provider text.
/// Abandon is a bounded retry of the same identity; the broker's delivery count ends it.
/// </summary>
public sealed record QueueOrderDecision(QueueSettlement Settlement, string? DeadLetterReason = null, Guid? WorkId = null);

/// <summary>One received message: the exact case-sensitive broker message ID, its declared content type and exact body.</summary>
public sealed record QueueMessage(string MessageId, string? ContentType, ReadOnlyMemory<byte> Body)
{
    public override string ToString() => nameof(QueueMessage);
}

/// <summary>
/// Admits one single-order message, or one explicit batch envelope with its complete manifest, durably and runs each order through
/// the pinned invocation core in its own ledger boundary. Nothing is sent here; deliveries are only enqueued. Missing explicit
/// policies, a different validated caller or an unserved route abandon without admitting.
/// </summary>
public sealed class QueueOrderProcessor(ITriggerWorkStore store, TriggerOrderExecutor execute, TriggerDeliveryDispatcher deliveries,
    ITriggerRunStore? batches = null)
{
    public async Task<QueueOrderDecision> ProcessAsync(ReleaseTargetReference target, Guid triggerId, MachineCaller caller, QueueMessage message,
        CancellationToken token, long? receiverVersion = null)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(message);
        var definition = await store.ReadDefinitionAsync(target, triggerId, token).ConfigureAwait(false);
        if (definition?.Configuration is not { Kind: TriggerKind.ServiceBusQueue, Policies: { } policies } configuration || configuration.Caller != caller.Principal ||
            configuration.ApplicationId != caller.ApplicationId || receiverVersion is { } version && definition.Version != version || !configuration.Routes.All(deliveries.Serves) ||
            configuration.Envelope == TriggerEnvelope.Batch && batches is null)
            return new(QueueSettlement.Abandon);
        if (configuration.Envelope == TriggerEnvelope.Batch) return await BatchAsync(definition, caller, message, batches!, token).ConfigureAwait(false);
        QueueOrderIdentity identity;
        try { identity = new(configuration.SourceBindingId, message.MessageId, target, caller.Principal, QueueOrderKind.SingleOrder, null); }
        catch (ArgumentException) { return new(QueueSettlement.DeadLetter, "invalidEnvelope"); }
        // The configured media is the format; a declared content type can only agree with it, never select another.
        if (message.ContentType is { } declared && declared != configuration.InputMediaType) return new(QueueSettlement.DeadLetter, "unsupportedFormat");
        if (message.Body.Length > policies.MaximumMessageBytes) return new(QueueSettlement.DeadLetter, "resourceLimit");
        Sha256Digest digest;
        try
        {
            digest = new MachineInvocationRequest(new PreviewInputDocument(null, message.Body.Span), [], configuration.Parameters,
                [new MachineInputMedia(null, configuration.InputMediaType)]).Digest;
        }
        catch (ArgumentException) { return new(QueueSettlement.DeadLetter, "invalidInput"); }
        var admission = await store.AdmitAsync(definition, identity, caller, message.Body, digest, token).ConfigureAwait(false);
        if (admission.Status == TriggerAdmissionStatus.IdempotencyConflict) return new(QueueSettlement.DeadLetter, "idempotencyConflict");
        // Refused, no current release and backlog pressure admit nothing and are retried by the broker.
        if (admission.WorkId is not { } work) return new(QueueSettlement.Abandon);
        var result = await execute(target, work, caller, token).ConfigureAwait(false);
        return result.Status switch
        {
            TriggeredInvocationStatus.Completed => new(QueueSettlement.Complete, WorkId: work),
            TriggeredInvocationStatus.Failed => new(QueueSettlement.DeadLetter, Reason(result.Outcome), work),
            TriggeredInvocationStatus.Fenced => new(QueueSettlement.DeadLetter, "authorizationRevoked", work),
            TriggeredInvocationStatus.ReleaseSuperseded => new(QueueSettlement.DeadLetter, "releaseSuperseded", work),
            _ => new(QueueSettlement.Abandon, WorkId: work)
        };
    }

    /// <summary>
    /// The complete bounded manifest is admitted first with its parent, then every order runs in its own boundary, so a failure at
    /// the start, middle or end never blocks the others. The message completes only when every order's outcome is durable and every
    /// order succeeded; a partial batch dead-letters with the first failing order's reason and the parent's private ID, keeping the
    /// successful orders and their outboxes. A redelivery recovers the same parent and never reruns a recorded order.
    /// </summary>
    private async Task<QueueOrderDecision> BatchAsync(TriggerDefinition definition, MachineCaller caller, QueueMessage message, ITriggerRunStore runs,
        CancellationToken token)
    {
        var configuration = definition.Configuration;
        if (message.ContentType is { } declared && declared != TriggerBatchEnvelope.MediaType) return new(QueueSettlement.DeadLetter, "unsupportedFormat");
        if (message.MessageId is not { Length: > 0 and <= QueueOrderIdentity.MaximumMessageIdCharacters }) return new(QueueSettlement.DeadLetter, "invalidEnvelope");
        if (message.Body.Length > TriggerBatchEnvelope.MaximumEnvelopeBytes) return new(QueueSettlement.DeadLetter, "resourceLimit");
        if (TriggerBatchEnvelope.Parse(message.Body.Span, configuration.Policies!.MaximumMessageBytes) is not { } orders)
            return new(QueueSettlement.DeadLetter, "invalidEnvelope");
        List<TriggerDiscoveredOrder> children;
        try
        {
            children = [.. orders.Select(order => ScheduledTriggerRunner.Order(configuration, new(configuration.SourceBindingId, message.MessageId,
                configuration.Target, caller.Principal, QueueOrderKind.KeyedChild, order.Key), order.CopyContent()))];
        }
        catch (ArgumentException) { return new(QueueSettlement.DeadLetter, "invalidInput"); }
        var admission = await runs.AdmitBatchAsync(definition, message.MessageId, caller, TriggerBatchEnvelope.ManifestDigest(orders), children, token).ConfigureAwait(false);
        if (admission.Status == TriggerAdmissionStatus.IdempotencyConflict) return new(QueueSettlement.DeadLetter, "idempotencyConflict");
        if (admission is not { RunId: { } parent, Works: { } works }) return new(QueueSettlement.Abandon);
        string? failure = null;
        var retry = false;
        foreach (var work in works)
        {
            var result = await execute(configuration.Target, work, caller, token).ConfigureAwait(false);
            switch (result.Status)
            {
                case TriggeredInvocationStatus.Completed: break;
                case TriggeredInvocationStatus.Failed: failure ??= Reason(result.Outcome); break;
                case TriggeredInvocationStatus.Fenced: failure ??= "authorizationRevoked"; break;
                case TriggeredInvocationStatus.ReleaseSuperseded: failure ??= "releaseSuperseded"; break;
                default: retry = true; break;
            }
        }
        return retry ? new(QueueSettlement.Abandon, WorkId: parent)
            : failure is null ? new(QueueSettlement.Complete, WorkId: parent) : new(QueueSettlement.DeadLetter, failure, parent);
    }

    private static string Reason(MachineInvocationOutcome? outcome) => outcome switch
    {
        MachineInvocationOutcome.InvalidInput => "invalidInput",
        MachineInvocationOutcome.InvalidParameter => "invalidParameter",
        MachineInvocationOutcome.ResourceLimit => "resourceLimit",
        MachineInvocationOutcome.IntegrityFailure => "integrityFailure",
        MachineInvocationOutcome.Unsupported => "unsupportedFormat",
        MachineInvocationOutcome.AttemptsExhausted => "attemptsExhausted",
        MachineInvocationOutcome.Refused => "authorizationRevoked",
        _ => "executionFailed"
    };
}
