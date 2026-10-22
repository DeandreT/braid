using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Publication metadata through the existing dedicated ingestion pool and an explicit actual ingestion claim.</summary>
public sealed class PostgresIngestionPublicationRegistry : IPrivateObjectPublicationRegistry
{
    private readonly PostgresPrivateObjectPublicationRegistry _registry;

    public PostgresIngestionPublicationRegistry(PostgresIngestionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _registry = new(PrivateObjectOwnerKind.NativeIngestion, async (organization, token) =>
            new PrivatePublicationTransaction(await database.BeginTransactionAsync(organization, token)));
    }

    public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default) =>
        _registry.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);

    public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
        PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default) =>
        _registry.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);

    public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default) =>
        _registry.GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken);

    internal Task<PrivatePublicationReserveResult> ReserveInTransactionAsync(PostgresIngestionTransaction transaction,
        PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind, Guid rootId, PrivateObjectId objectId, Guid tempId,
        Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default) => _registry.ReserveInTransactionAsync(
            new(transaction), owner, storeKind, new(rootId, objectId, tempId), absenceRecordDigest, cancellationToken);

    internal Task<PrivatePublicationBindResult> BindInTransactionAsync(PostgresIngestionTransaction transaction,
        PrivatePublicationReservation reservation, PrivatePublicationFileIdentity actualIdentity,
        Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default) =>
        _registry.BindInTransactionAsync(new(transaction), reservation, actualIdentity, boundRecordDigest, cancellationToken);

    internal Task<PrivatePublicationSnapshot?> GetInTransactionAsync(PostgresIngestionTransaction transaction,
        PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind, Guid rootId, PrivateObjectId objectId,
        Guid tempId, CancellationToken cancellationToken = default) => _registry.GetInTransactionAsync(new(transaction), owner,
            storeKind, new(rootId, objectId, tempId), cancellationToken);
}
