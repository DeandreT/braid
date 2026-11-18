using Braid.As2;
using Braid.Deployment;
using Braid.Deployment.Triggers;

namespace Braid.Triggers.As2;

public interface IAs2ClaimFenceStore
{
    ValueTask<bool> TryBeginAsync(ReleaseTargetReference target, TriggerDeliveryClaim claim, As2Attempt attempt, CancellationToken token);
}

public sealed class As2OutputDestination : IClaimedOutputDestination
{
    private readonly Guid binding;
    private readonly As2Partner partner;
    private readonly HttpClient client;
    private readonly IAs2ClaimFenceStore fences;

    public As2OutputDestination(TriggerConnectorReference connector, Guid destinationBindingId, As2Partner partner, HttpClient client, IAs2ClaimFenceStore fences)
    {
        ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(partner);
        if (destinationBindingId == Guid.Empty || partner.Version != BindingVersion(connector, destinationBindingId))
            throw new ArgumentException("The partner must match the registered AS2 binding and version.");
        this.binding = destinationBindingId; Connector = connector; this.partner = partner;
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.fences = fences ?? throw new ArgumentNullException(nameof(fences));
    }

    public static string BindingVersion(TriggerConnectorReference connector, Guid destinationBindingId) =>
        FormattableString.Invariant($"{destinationBindingId:D}/{connector.ConnectorId}/{connector.Version}");

    public TriggerDestinationKind Kind => TriggerDestinationKind.As2;
    public TriggerConnectorReference Connector { get; }

    public async Task<DeliveryObservationKind> SendClaimedAsync(ReleaseTargetReference target, TriggerDeliveryClaim claim, CancellationToken token)
    {
        if (claim.Status != TriggerDeliveryClaimStatus.Claimed || claim.ClaimId == Guid.Empty || claim.Send <= 0 ||
            claim.Route is not { } route || route.Connector != Connector || route.Kind != Kind || route.DestinationBindingId != binding ||
            claim.Delivery is not { } delivery || delivery.DestinationBindingId != binding || delivery.DestinationVersion != Connector.Version ||
            !string.Equals(delivery.MediaType, "application/edi-x12", StringComparison.OrdinalIgnoreCase))
            return DeliveryObservationKind.PermanentFailure;

        var pin = new As2Delivery(delivery.DeliveryId, partner.Version, delivery.ContentDigest.Value, delivery.CopyContent());
        var sender = new As2Connector(client, new Fence(fences, target, claim));
        var result = await sender.SendAsync(pin, partner, token);
        return result.Outcome == As2Outcome.VerifiedTransportReceipt ? DeliveryObservationKind.Accepted : DeliveryObservationKind.Unknown;
    }

    private sealed class Fence(IAs2ClaimFenceStore store, ReleaseTargetReference target, TriggerDeliveryClaim claim) : IAs2SendFence
    {
        public ValueTask<bool> TryBeginAsync(As2Attempt attempt, CancellationToken cancellationToken) =>
            store.TryBeginAsync(target, claim, attempt, cancellationToken);
    }
}
