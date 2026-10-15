using Weft.Application;
using Weft.Domain;

namespace Weft.Server;

/// <summary>Resolves only the original owner's dedicated publication registry, at actual registration.</summary>
internal sealed class LazyPrivateObjectPublicationRegistry : IPrivateObjectPublicationRegistry
{
    private readonly Func<IPrivateObjectPublicationRegistry> ingestionFactory;
    private readonly Func<IPrivateObjectPublicationRegistry> reportFactory;
    private readonly object ingestionLock = new();
    private readonly object reportLock = new();
    private IPrivateObjectPublicationRegistry? ingestion;
    private IPrivateObjectPublicationRegistry? report;

    internal LazyPrivateObjectPublicationRegistry(Func<IPrivateObjectPublicationRegistry> ingestionFactory,
        Func<IPrivateObjectPublicationRegistry> reportFactory)
    {
        this.ingestionFactory = ingestionFactory ?? throw new ArgumentNullException(nameof(ingestionFactory));
        this.reportFactory = reportFactory ?? throw new ArgumentNullException(nameof(reportFactory));
    }

    public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, Guid rootId,
        PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        return Resolve(owner.Kind).ReserveAsync(owner, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
    }

    public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
        PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        cancellationToken.ThrowIfCancellationRequested();
        return Resolve(reservation.Owner.Kind).BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
    }

    public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, Guid rootId,
        PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        return Resolve(owner.Kind).GetAsync(owner, rootId, objectId, tempId, cancellationToken);
    }

    private IPrivateObjectPublicationRegistry Resolve(PrivateObjectOwnerKind kind)
    {
        switch (kind)
        {
            case PrivateObjectOwnerKind.NativeIngestion:
                lock (ingestionLock)
                    return ingestion ??= ingestionFactory() ?? throw Unavailable();
            case PrivateObjectOwnerKind.ReportAttempt:
                lock (reportLock)
                    return report ??= reportFactory() ?? throw Unavailable();
            default:
                throw Unavailable();
        }
    }

    private static InvalidOperationException Unavailable() => new("The publication registry is unavailable.");
}
