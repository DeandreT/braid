namespace Braid.Deployment.Triggers;

/// <summary>
/// One operator-registered destination connector. A send uses the exact frozen bytes and reports only what the provider proved:
/// Accepted, ProvenKnownNotAccepted, Unknown or PermanentFailure. It never retries a mutation itself.
/// </summary>
public interface IOutputDestination
{
    TriggerDestinationKind Kind { get; }
    TriggerConnectorReference Connector { get; }
    Task<DeliveryObservationKind> SendAsync(FrozenTriggerDelivery delivery, CancellationToken token);
}

/// <summary>For protocols that persist their own evidence against the live shared-outbox claim before dispatch.</summary>
public interface IClaimedOutputDestination : IOutputDestination
{
    Task<DeliveryObservationKind> SendClaimedAsync(ReleaseTargetReference target, TriggerDeliveryClaim claim, CancellationToken token);
    Task<DeliveryObservationKind> IOutputDestination.SendAsync(FrozenTriggerDelivery delivery, CancellationToken token) =>
        throw new InvalidOperationException("This destination requires a live outbox claim.");
}

/// <summary>
/// Claims one due delivery, sends it within the claim's attempt window and records the observation against that exact claim.
/// Retry eligibility stays in the outbox; a route without a registered connector is refused without a send.
/// </summary>
public sealed class TriggerDeliveryDispatcher
{
    private static readonly TimeSpan RecordBound = TimeSpan.FromSeconds(20);
    private readonly ITriggerDeliveryStore store;
    private readonly IReadOnlyList<IOutputDestination> destinations;
    private readonly TimeProvider time;

    public TriggerDeliveryDispatcher(ITriggerDeliveryStore store, IReadOnlyList<IOutputDestination> destinations, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(destinations); ArgumentNullException.ThrowIfNull(time);
        if (destinations.Any(value => value is null) || destinations.Select(value => (value.Kind, value.Connector)).Distinct().Count() != destinations.Count)
            throw new ArgumentException("Each destination connector is registered once.");
        this.store = store; this.destinations = destinations.ToArray(); this.time = time;
    }

    /// <summary>True when this worker has the exact registered connector version for the route.</summary>
    public bool Serves(TriggerRoute route) => Find(route) is not null;

    /// <summary>True when a due delivery was claimed or settled; false when nothing is due.</summary>
    public async Task<bool> DispatchAsync(ReleaseTargetReference target, Guid destinationBindingId, CancellationToken token)
    {
        var claim = await store.ClaimAsync(target, destinationBindingId, token).ConfigureAwait(false);
        if (claim.Status != TriggerDeliveryClaimStatus.Claimed) return claim.Status == TriggerDeliveryClaimStatus.Settled;
        var observation = Find(claim.Route!) is { } destination
            ? await SendAsync(destination, target, claim, token).ConfigureAwait(false)
            : DeliveryObservationKind.ProvenKnownNotAccepted;
        // The observation is recorded even when the caller is stopping; otherwise the expired claim becomes unknown.
        using var record = new CancellationTokenSource(RecordBound);
        await store.RecordObservationAsync(target, claim.Delivery!.DeliveryId, claim.ClaimId, observation, record.Token).ConfigureAwait(false);
        return true;
    }

    private IOutputDestination? Find(TriggerRoute route) => destinations.FirstOrDefault(value => value.Kind == route.Kind && value.Connector == route.Connector);

    private async Task<DeliveryObservationKind> SendAsync(IOutputDestination destination, ReleaseTargetReference target, TriggerDeliveryClaim claim,
        CancellationToken token)
    {
        var remaining = claim.Deadline - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return DeliveryObservationKind.ProvenKnownNotAccepted;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(remaining);
        try
        {
            var kind = destination is IClaimedOutputDestination claimed
                ? await claimed.SendClaimedAsync(target, claim, window.Token).ConfigureAwait(false)
                : await destination.SendAsync(claim.Delivery!, window.Token).ConfigureAwait(false);
            return kind is DeliveryObservationKind.Accepted or DeliveryObservationKind.ProvenKnownNotAccepted or DeliveryObservationKind.PermanentFailure
                ? kind : DeliveryObservationKind.Unknown;
        }
        // A timeout, cancellation or transport failure after dispatch is not proof that nothing was accepted.
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return DeliveryObservationKind.Unknown; }
    }
}
