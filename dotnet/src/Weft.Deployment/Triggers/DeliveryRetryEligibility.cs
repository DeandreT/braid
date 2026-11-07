using System.Net.Http.Headers;
using Weft.Domain;

namespace Weft.Deployment.Triggers;

/// <summary>Exact immutable delivery material, not an endpoint, send operation or authorization.</summary>
public sealed class FrozenTriggerDelivery
{
    private readonly byte[] content;

    public FrozenTriggerDelivery(Guid deliveryId, Guid outputReceiptId, Guid destinationBindingId, long destinationVersion, DateTimeOffset admittedAt,
        ReadOnlySpan<byte> content, Sha256Digest contentDigest, int contentLength, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(contentDigest);
        if (deliveryId == Guid.Empty || outputReceiptId == Guid.Empty || destinationBindingId == Guid.Empty || destinationVersion <= 0 ||
            contentLength != content.Length || contentLength > MachineInvocationPolicy.MaximumRetainedBytes ||
            mediaType is null || mediaType.Length is 0 or > 256 ||
            mediaType.Any(c => c < ' ' || c > '~') || !MediaTypeHeaderValue.TryParse(mediaType, out var parsedMedia) ||
            parsedMedia.MediaType is not { } type || type.Contains('*', StringComparison.Ordinal))
            throw new ArgumentException("Invalid frozen delivery material.");
        this.content = content.ToArray();
        if (contentDigest != Sha256Digest.FromBytes(this.content)) throw new ArgumentException("Invalid frozen delivery material.");
        DeliveryId = deliveryId; OutputReceiptId = outputReceiptId; DestinationBindingId = destinationBindingId;
        DestinationVersion = destinationVersion; AdmittedAt = admittedAt.ToUniversalTime(); ContentDigest = contentDigest; MediaType = mediaType;
    }

    public Guid DeliveryId { get; }
    public Guid OutputReceiptId { get; }
    public Guid DestinationBindingId { get; }
    public long DestinationVersion { get; }
    /// <summary>The original durable admission instant, not the time of the latest observation or retry.</summary>
    public DateTimeOffset AdmittedAt { get; }
    public Sha256Digest ContentDigest { get; }
    public int ContentLength => content.Length;
    public string MediaType { get; }
    public byte[] CopyContent() => (byte[])content.Clone();
    public bool Matches(FrozenTriggerDelivery other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return DeliveryId == other.DeliveryId && OutputReceiptId == other.OutputReceiptId && DestinationBindingId == other.DestinationBindingId &&
            DestinationVersion == other.DestinationVersion && AdmittedAt == other.AdmittedAt && ContentDigest == other.ContentDigest && MediaType == other.MediaType &&
            content.AsSpan().SequenceEqual(other.content);
    }
    public override string ToString() => nameof(FrozenTriggerDelivery);
}

/// <summary>Technical finite bounds only. Delays, attempt window and maximum elapsed lifetime are all explicit, with no business defaults.</summary>
public sealed class DeliveryRetryPolicy
{
    public const int MaximumTotalSends = 32;

    public DeliveryRetryPolicy(int totalSends, IReadOnlyList<TimeSpan> retryDelays, TimeSpan attemptWindow, TimeSpan maximumElapsed)
    {
        ArgumentNullException.ThrowIfNull(retryDelays);
        if (totalSends is < 1 or > MaximumTotalSends || retryDelays.Count != totalSends - 1 || attemptWindow <= TimeSpan.Zero ||
            maximumElapsed <= TimeSpan.Zero || maximumElapsed < attemptWindow || maximumElapsed.Ticks > DateTimeOffset.MaxValue.UtcTicks)
            throw new ArgumentException("Invalid finite delivery retry policy.");
        var delays = retryDelays.ToArray();
        try
        {
            var ticks = checked(attemptWindow.Ticks * totalSends);
            foreach (var delay in delays)
            {
                if (delay <= TimeSpan.Zero) throw new ArgumentException("Invalid finite delivery retry policy.");
                ticks = checked(ticks + delay.Ticks);
            }
            if (ticks > DateTimeOffset.MaxValue.UtcTicks) throw new ArgumentException("Invalid finite delivery retry policy.");
        }
        catch (OverflowException) { throw new ArgumentException("Invalid finite delivery retry policy."); }
        TotalSends = totalSends; RetryDelays = Array.AsReadOnly(delays); AttemptWindow = attemptWindow; MaximumElapsed = maximumElapsed;
    }

    public int TotalSends { get; }
    public IReadOnlyList<TimeSpan> RetryDelays { get; }
    /// <summary>The explicitly configured entire send-attempt window, which a future connector must enforce.</summary>
    public TimeSpan AttemptWindow { get; }
    /// <summary>A fixed lifetime from the original delivery admission, never from a later observation or retry.</summary>
    public TimeSpan MaximumElapsed { get; }
    public override string ToString() => nameof(DeliveryRetryPolicy);
}

/// <summary>
/// Known non-acceptance requires positive adapter evidence. Dispatch timeout, cancellation, reset, HTTP 5xx and an expired
/// unobserved lease are Unknown. Accepted means provider acceptance, not downstream completion.
/// </summary>
public enum DeliveryObservationKind { NotAttempted, Accepted, ProvenKnownNotAccepted, Unknown, PermanentFailure, Fenced, PayloadUnavailable }

/// <summary>
/// Retained observation. FirstAmbiguousDispatchAt must survive later attempts, including proven non-acceptance of a later send.
/// Only positive acceptance or explicit reconciliation can resolve the earlier ambiguity; this DTO does neither.
/// </summary>
public sealed class DeliveryObservation
{
    public DeliveryObservation(DeliveryObservationKind kind, int sends, DateTimeOffset observedAt,
        DateTimeOffset? lastDispatchAt, DateTimeOffset? firstAmbiguousDispatchAt)
    {
        if (!Enum.IsDefined(kind) || sends is < 0 or > DeliveryRetryPolicy.MaximumTotalSends ||
            (kind == DeliveryObservationKind.NotAttempted ? sends != 0 :
                kind is DeliveryObservationKind.Fenced or DeliveryObservationKind.PayloadUnavailable ? false : sends == 0) ||
            (sends == 0 ? lastDispatchAt is not null : lastDispatchAt is null || lastDispatchAt > observedAt) ||
            (kind == DeliveryObservationKind.Unknown && firstAmbiguousDispatchAt is null) ||
            (firstAmbiguousDispatchAt is not null && (lastDispatchAt is null || firstAmbiguousDispatchAt > lastDispatchAt)))
            throw new ArgumentException("Invalid delivery observation.");
        Kind = kind; Sends = sends; ObservedAt = observedAt.ToUniversalTime(); LastDispatchAt = lastDispatchAt?.ToUniversalTime();
        FirstAmbiguousDispatchAt = firstAmbiguousDispatchAt?.ToUniversalTime();
    }

    public DeliveryObservationKind Kind { get; }
    public int Sends { get; }
    public DateTimeOffset ObservedAt { get; }
    public DateTimeOffset? LastDispatchAt { get; }
    public DateTimeOffset? FirstAmbiguousDispatchAt { get; }

    /// <summary>
    /// Retains earlier ambiguity when recording a later attempt, even if that later send is proven not accepted.
    /// This records an observation, not permission to attempt another send. Restored constructor data must be trusted history.
    /// </summary>
    public DeliveryObservation AfterAttempt(DeliveryObservationKind kind, DateTimeOffset dispatchedAt, DateTimeOffset observedAt)
    {
        if (!Enum.IsDefined(kind) || kind == DeliveryObservationKind.NotAttempted ||
            Kind is DeliveryObservationKind.Accepted or DeliveryObservationKind.PermanentFailure or DeliveryObservationKind.Fenced or DeliveryObservationKind.PayloadUnavailable ||
            dispatchedAt < ObservedAt)
            throw new ArgumentException("Invalid subsequent delivery observation.");
        return new(kind, checked(Sends + 1), observedAt, dispatchedAt,
            FirstAmbiguousDispatchAt ?? (kind == DeliveryObservationKind.Unknown ? dispatchedAt : null));
    }

    public override string ToString() => nameof(DeliveryObservation);
}

/// <summary>
/// A separately verified receiver guarantee for this exact delivery. Coverage includes the original ambiguous dispatch
/// and the entire next attempt window. This DTO neither verifies receiver configuration nor extends its guarantee.
/// </summary>
public sealed class ReceiverDeduplicationGuarantee
{
    public ReceiverDeduplicationGuarantee(FrozenTriggerDelivery delivery, DateTimeOffset coversFrom, DateTimeOffset coversThrough)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (coversThrough <= coversFrom) throw new ArgumentException("Invalid receiver deduplication coverage.");
        Delivery = delivery; CoversFrom = coversFrom.ToUniversalTime(); CoversThrough = coversThrough.ToUniversalTime();
    }

    public FrozenTriggerDelivery Delivery { get; }
    public DateTimeOffset CoversFrom { get; }
    public DateTimeOffset CoversThrough { get; }
    public override string ToString() => nameof(ReceiverDeduplicationGuarantee);
}

public enum DeliveryRetryDisposition { Eligible, Accepted, Exhausted, PolicyExpired, Fenced, PayloadUnavailable, PermanentFailure, ReconciliationRequired, IdentityConflict }

/// <summary>Pure eligibility, never send authorization. Live authority, durable claims and receipt checks remain required.</summary>
public sealed class DeliveryRetryEligibility
{
    private DeliveryRetryEligibility(DeliveryRetryDisposition disposition, DateTimeOffset? nextAt = null)
    { Disposition = disposition; NextAt = nextAt; }

    public DeliveryRetryDisposition Disposition { get; }
    public DateTimeOffset? NextAt { get; }

    public static DeliveryRetryEligibility Evaluate(FrozenTriggerDelivery original, FrozenTriggerDelivery candidate, DeliveryObservation observation,
        DeliveryRetryPolicy policy, DateTimeOffset now, ReceiverDeduplicationGuarantee? deduplication)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(observation); ArgumentNullException.ThrowIfNull(policy);
        if (!original.Matches(candidate)) return new(DeliveryRetryDisposition.IdentityConflict);
        if (observation.ObservedAt < original.AdmittedAt || observation.LastDispatchAt < original.AdmittedAt ||
            observation.FirstAmbiguousDispatchAt < original.AdmittedAt || now < observation.ObservedAt)
            throw new ArgumentException("Delivery clock precedes the retained admission or observation.");
        DateTimeOffset deadline;
        try { deadline = original.AdmittedAt + policy.MaximumElapsed; }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("Delivery lifetime exceeds the supported clock range."); }
        var terminal = observation.Kind switch
        {
            DeliveryObservationKind.Accepted => DeliveryRetryDisposition.Accepted,
            DeliveryObservationKind.PermanentFailure => DeliveryRetryDisposition.PermanentFailure,
            DeliveryObservationKind.Fenced => DeliveryRetryDisposition.Fenced,
            DeliveryObservationKind.PayloadUnavailable => DeliveryRetryDisposition.PayloadUnavailable,
            _ => (DeliveryRetryDisposition?)null
        };
        if (terminal is { } disposition) return new(disposition);
        if (observation.Sends >= policy.TotalSends) return new(DeliveryRetryDisposition.Exhausted);
        if (now >= deadline) return new(DeliveryRetryDisposition.PolicyExpired);
        DateTimeOffset nextAt, attemptEndsAt;
        try
        {
            var scheduledAt = observation.Sends == 0 ? observation.ObservedAt : observation.ObservedAt + policy.RetryDelays[observation.Sends - 1];
            nextAt = (now > scheduledAt ? now : scheduledAt).ToUniversalTime();
            if (nextAt >= deadline || deadline - nextAt < policy.AttemptWindow) return new(DeliveryRetryDisposition.PolicyExpired);
            attemptEndsAt = nextAt + policy.AttemptWindow;
        }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("Delivery attempt window exceeds the supported clock range."); }
        if (observation.FirstAmbiguousDispatchAt is not null &&
            (deduplication is null || !original.Matches(deduplication.Delivery) ||
                deduplication.CoversFrom > observation.FirstAmbiguousDispatchAt || deduplication.CoversThrough < attemptEndsAt))
            return new(DeliveryRetryDisposition.ReconciliationRequired);
        return new(DeliveryRetryDisposition.Eligible, nextAt);
    }

    public override string ToString() => nameof(DeliveryRetryEligibility);
}
