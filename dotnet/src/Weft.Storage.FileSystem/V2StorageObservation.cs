using Weft.Domain;

namespace Weft.Storage.FileSystem;

// Internal qualification seam. Each notification follows the completed physical
// phase it identifies. Production factories always use a null observer.
internal enum V2StoragePhase
{
    SealStageWritten = 1,
    SealInstalled = 2,
    ReservationAcknowledged = 3,
    TemporaryCreated = 4,
    BindingAcknowledged = 5,
    PayloadPrepared = 6,
    FinalGateAcquired = 7,
    ReadGateAcquired = 8,
    FenceInstalled = 9,
    TempGateAcquired = 10
}

internal readonly record struct V2StorageObservation(V2StoragePhase Phase,
    V2RecordKind? RecordKind = null, PrivatePublicationKey? Publication = null,
    PrivatePublicationInodeIdentity? FileIdentity = null);

internal interface IV2StorageObserver
{
    ValueTask ObserveAsync(V2StorageObservation observation, CancellationToken cancellationToken);
}
