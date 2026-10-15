using Weft.Domain;

namespace Weft.Storage.FileSystem;

public enum PrivateStorageV2ActivationOutcome { Activated = 1, AlreadyActive = 2, Busy = 3 }

/// <summary>Private-root activation facts, without a path or a claim of operator quiescence proof.</summary>
public sealed record PrivateStorageV2ActivationReceipt
{
    internal PrivateStorageV2ActivationReceipt(Guid activationId, Guid rootId, Guid controlNamespaceId,
        Sha256Digest originalMarkerDigest, Sha256Digest activeMarkerDigest)
    {
        ActivationId = activationId;
        RootId = rootId;
        ControlNamespaceId = controlNamespaceId;
        OriginalMarkerDigest = originalMarkerDigest;
        ActiveMarkerDigest = activeMarkerDigest;
    }
    public Guid ActivationId { get; }
    public Guid RootId { get; }
    public Guid ControlNamespaceId { get; }
    public Sha256Digest OriginalMarkerDigest { get; }
    public Sha256Digest ActiveMarkerDigest { get; }
}

public sealed record PrivateStorageV2ActivationResult
{
    private PrivateStorageV2ActivationResult(PrivateStorageV2ActivationOutcome outcome,
        PrivateStorageV2ActivationReceipt? receipt)
    { Outcome = outcome; Receipt = receipt; }
    public PrivateStorageV2ActivationOutcome Outcome { get; }
    public PrivateStorageV2ActivationReceipt? Receipt { get; }
    internal static PrivateStorageV2ActivationResult Busy() => new(PrivateStorageV2ActivationOutcome.Busy, null);
    internal static PrivateStorageV2ActivationResult Success(bool alreadyActive, PrivateStorageV2ActivationReceipt receipt) =>
        new(alreadyActive ? PrivateStorageV2ActivationOutcome.AlreadyActive : PrivateStorageV2ActivationOutcome.Activated, receipt);
}

internal enum V2ActivationPhase
{
    PreparationCreated = 1, ReceiptStageWritten = 2, ReceiptSealed = 3, ControlInstalled = 4
}

internal readonly record struct V2ActivationObservation(V2ActivationPhase Phase, Guid ActivationId,
    Guid RootId, Guid ControlNamespaceId);

internal interface IV2ActivationObserver
{
    ValueTask ObserveAsync(V2ActivationObservation observation, CancellationToken cancellationToken);
}
