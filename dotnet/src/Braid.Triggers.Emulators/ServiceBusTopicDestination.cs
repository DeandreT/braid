using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus;
using Braid.Deployment.Triggers;

namespace Braid.Triggers.Emulators;

/// <summary>
/// One send to one registered topic: the delivery ID is the MessageId and the body, media and properties are frozen. Broker
/// acknowledgement means accepted by the broker, not processed by subscribers. The client must have SDK retries disabled.
/// </summary>
public sealed partial class ServiceBusTopicDestination : IOutputDestination, IAsyncDisposable
{
    internal const string DeliveryProperty = "braidDelivery", ReceiptProperty = "braidReceipt", DigestProperty = "braidSha256";
    private readonly ServiceBusSender sender;

    public ServiceBusTopicDestination(TriggerConnectorReference connector, ServiceBusClient client, string topicName)
    {
        ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(client);
        if (topicName is null || !TopicPattern().IsMatch(topicName)) throw new ArgumentException("Invalid Service Bus topic.");
        Connector = connector;
        sender = client.CreateSender(topicName);
    }

    public TriggerDestinationKind Kind => TriggerDestinationKind.ServiceBusTopic;
    public TriggerConnectorReference Connector { get; }

    public async Task<DeliveryObservationKind> SendAsync(FrozenTriggerDelivery delivery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.DestinationVersion != Connector.Version) return DeliveryObservationKind.PermanentFailure;
        var message = new ServiceBusMessage(delivery.CopyContent()) { MessageId = delivery.DeliveryId.ToString("D"), ContentType = delivery.MediaType };
        message.ApplicationProperties[DeliveryProperty] = delivery.DeliveryId.ToString("D");
        message.ApplicationProperties[ReceiptProperty] = delivery.OutputReceiptId.ToString("D");
        message.ApplicationProperties[DigestProperty] = delivery.ContentDigest.Value;
        try
        {
            await sender.SendMessageAsync(message, token).ConfigureAwait(false);
            return DeliveryObservationKind.Accepted;
        }
        // These are refused when the link or its authorization is established, before any message transfer.
        catch (ServiceBusException error) when (error.Reason is ServiceBusFailureReason.MessagingEntityNotFound or ServiceBusFailureReason.MessagingEntityDisabled)
        { return DeliveryObservationKind.ProvenKnownNotAccepted; }
        catch (UnauthorizedAccessException) { return DeliveryObservationKind.ProvenKnownNotAccepted; }
        catch (ServiceBusException error) when (error.Reason == ServiceBusFailureReason.MessageSizeExceeded) { return DeliveryObservationKind.PermanentFailure; }
        // Timeouts, throttling, communication and lock failures after the send began are ambiguous and held for manual resolution.
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return DeliveryObservationKind.Unknown; }
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,258}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    public ValueTask DisposeAsync() => sender.DisposeAsync();
    public override string ToString() => nameof(ServiceBusTopicDestination);
}
