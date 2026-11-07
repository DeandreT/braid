using System.Text;
using Weft.Application;
using Weft.Domain;

namespace Weft.Deployment.Triggers;

/// <summary>An explicit envelope selection, not a parser or a rule for splitting payloads.</summary>
public enum QueueOrderKind { SingleOrder, KeyedChild }

/// <summary>
/// Stable source identity. Configuration, run, lock, sequence and delivery count are deliberately absent.
/// Message IDs are exact, case-sensitive broker strings, never UUIDs derived by this contract.
/// </summary>
public sealed record QueueOrderIdentity
{
    /// <summary>The Service Bus SDK bound in UTF-16 code units, not Unicode scalar count.</summary>
    public const int MaximumMessageIdCharacters = 128;
    public const int MaximumChildKeyBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public QueueOrderIdentity(Guid sourceBindingId, string messageId, ReleaseTargetReference target, MachinePrincipal principal,
        QueueOrderKind kind, string? childKey)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(principal);
        if (sourceBindingId == Guid.Empty || !Enum.IsDefined(kind) ||
            !ValidText(messageId, MaximumMessageIdCharacters, int.MaxValue) ||
            (kind == QueueOrderKind.SingleOrder ? childKey is not null : !ValidText(childKey, MaximumChildKeyBytes, MaximumChildKeyBytes)))
            throw new ArgumentException("Invalid queue order identity.");
        SourceBindingId = sourceBindingId; MessageId = messageId; Target = target; Principal = principal; Kind = kind; ChildKey = childKey;
    }

    private static bool ValidText(string? value, int maximumCharacters, int maximumBytes)
    {
        if (value is null || value.Length == 0 || value.Length > maximumCharacters) return false;
        try { return StrictUtf8.GetByteCount(value) <= maximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    public Guid SourceBindingId { get; }
    public string MessageId { get; }
    public ReleaseTargetReference Target { get; }
    public MachinePrincipal Principal { get; }
    public QueueOrderKind Kind { get; }
    public string? ChildKey { get; }
    public override string ToString() => nameof(QueueOrderIdentity);
}

/// <summary>A snapshot supplied by durable admission, not evidence that admission or current authority exists.</summary>
public sealed record TriggerWorkPin
{
    public TriggerWorkPin(MachineInvocationProvenance provenance, PrincipalId publisher, long targetGeneration, Guid invocationGrantId)
    {
        ArgumentNullException.ThrowIfNull(provenance); ArgumentNullException.ThrowIfNull(publisher);
        var image = provenance.RuntimeImage;
        if (provenance.ReleaseId == Guid.Empty || provenance.QualificationId == Guid.Empty || provenance.BuildId == Guid.Empty ||
            provenance.RevisionId == Guid.Empty || provenance.BuildIntentDigest is null || provenance.ArtifactDigest is null ||
            image is not { Length: 71 } || !image.StartsWith("sha256:", StringComparison.Ordinal) ||
            image[7..].Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            targetGeneration <= 0 || invocationGrantId == Guid.Empty)
            throw new ArgumentException("Invalid trigger work pin.");
        Provenance = provenance; Publisher = publisher; TargetGeneration = targetGeneration; InvocationGrantId = invocationGrantId;
    }

    public MachineInvocationProvenance Provenance { get; }
    public PrincipalId Publisher { get; }
    public long TargetGeneration { get; }
    public Guid InvocationGrantId { get; }
    public override string ToString() => nameof(TriggerWorkPin);
}

/// <summary>
/// Original durable lookup data. Reuse this request UUID, digest and pin, not a rebuilt request against current configuration.
/// Input/output persistence, complete frozen routing and atomic ledger/outbox admission are not implemented here.
/// </summary>
public sealed class StoredQueueOrderWork
{
    public StoredQueueOrderWork(Guid workId, QueueOrderIdentity identity, Sha256Digest rawSourceDigest, Guid applicationId,
        Guid requestId, Sha256Digest requestDigest, TriggerWorkPin pin)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(rawSourceDigest);
        ArgumentNullException.ThrowIfNull(requestDigest); ArgumentNullException.ThrowIfNull(pin);
        if (workId == Guid.Empty || applicationId == Guid.Empty || requestId == Guid.Empty)
            throw new ArgumentException("Invalid stored queue order work.");
        WorkId = workId; Identity = identity; RawSourceDigest = rawSourceDigest; ApplicationId = applicationId;
        RequestId = requestId; RequestDigest = requestDigest; Pin = pin;
    }

    public Guid WorkId { get; }
    public QueueOrderIdentity Identity { get; }
    public Sha256Digest RawSourceDigest { get; }
    public Guid ApplicationId { get; }
    public Guid RequestId { get; }
    public Sha256Digest RequestDigest { get; }
    public TriggerWorkPin Pin { get; }
    public override string ToString() => nameof(StoredQueueOrderWork);
}

public enum QueueOrderAdmissionKind { NeedsDurableAdmission, ReuseStoredWork, IdempotencyConflict }

/// <summary>A pure lookup decision. NeedsDurableAdmission creates no UUID, work, ledger claim or permission to execute.</summary>
public sealed class QueueOrderAdmission
{
    private QueueOrderAdmission(QueueOrderAdmissionKind kind, StoredQueueOrderWork? storedWork) { Kind = kind; StoredWork = storedWork; }
    public QueueOrderAdmissionKind Kind { get; }
    public StoredQueueOrderWork? StoredWork { get; }

    public static QueueOrderAdmission Inspect(QueueOrderIdentity identity, Sha256Digest rawSourceDigest, Guid applicationId,
        StoredQueueOrderWork? storedWork)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(rawSourceDigest);
        if (applicationId == Guid.Empty) throw new ArgumentException("A source application identifier is required.");
        if (storedWork is null) return new(QueueOrderAdmissionKind.NeedsDurableAdmission, null);
        if (storedWork.Identity != identity) throw new ArgumentException("Stored work does not match the lookup identity.");
        return storedWork.RawSourceDigest == rawSourceDigest && storedWork.ApplicationId == applicationId
            ? new(QueueOrderAdmissionKind.ReuseStoredWork, storedWork)
            : new(QueueOrderAdmissionKind.IdempotencyConflict, null);
    }

    public override string ToString() => nameof(QueueOrderAdmission);
}

/// <summary>An explicit new source identity linked only to a recorded failed order. It does not admit the correction.</summary>
public sealed class CorrectedQueueOrderIdentity
{
    public CorrectedQueueOrderIdentity(QueueOrderIdentity identity, StoredQueueOrderWork failedWork, RetainedTriggerInvocationObservation failedObservation)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(failedWork); ArgumentNullException.ThrowIfNull(failedObservation);
        var original = failedWork.Identity;
        if (identity == original || identity.Target != original.Target || identity.Principal != original.Principal ||
            identity.SourceBindingId != original.SourceBindingId || identity.Kind != original.Kind ||
            failedObservation.ClassifyFor(failedWork) != TriggerOrderAction.RecordedFailure)
            throw new ArgumentException("A correction requires a new explicit identity linked to failed work in the same source scope.");
        Identity = identity; FailedWorkId = failedWork.WorkId; FailedRequestId = failedWork.RequestId;
    }

    public QueueOrderIdentity Identity { get; }
    public Guid FailedWorkId { get; }
    public Guid FailedRequestId { get; }
    public override string ToString() => nameof(CorrectedQueueOrderIdentity);
}
