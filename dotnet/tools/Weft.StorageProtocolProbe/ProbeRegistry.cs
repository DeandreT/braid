using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.StorageProtocolProbe;

internal sealed class ProbeRegistry(ProbeCommand command, PrivatePublicationOwner? originalOwner,
    ProbeState state) : IAsyncDisposable
{
    private IAsyncDisposable? _database;

    internal IPrivateObjectPublicationRegistry Resolve()
    {
        if (++state.RegistryFactoryResolutions != 1 || command.Operation != "publish" || originalOwner is null)
            throw new InvalidDataException();
        IPrivateObjectPublicationRegistry registry;
        if (command.RegistryMode == "syntheticPhysical")
        {
            registry = new SyntheticPhysicalRegistry();
        }
        else if (command.RegistryMode == "postgres" && command.ConnectionString is { } connection)
        {
            if (originalOwner.Kind == PrivateObjectOwnerKind.NativeIngestion)
            {
                var database = new PostgresIngestionDatabase(connection);
                _database = database;
                registry = new PostgresIngestionPublicationRegistry(database);
            }
            else
            {
                var database = new PostgresRevisionDerivationDatabase(connection);
                _database = database;
                registry = new PostgresRevisionReportPublicationRegistry(database);
            }
        }
        else
        {
            throw new InvalidDataException();
        }
        return new ObservedRegistry(registry, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private sealed class ObservedRegistry(IPrivateObjectPublicationRegistry inner, ProbeState state)
        : IPrivateObjectPublicationRegistry
    {
        public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        {
            state.ReserveCalls++;
            return inner.ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
        }

        public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            state.BindCalls++;
            return inner.BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
        }

        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
        {
            state.GetCalls++;
            return inner.GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken);
        }
    }

    // Explicitly synthetic, in-memory registration for physical protocol tests.
    // It makes no PostgreSQL/authority/retained-history claim and cannot survive
    // this child. Actual PostgreSQL mode above calls the ordinary product ports.
    private sealed class SyntheticPhysicalRegistry : IPrivateObjectPublicationRegistry
    {
        private readonly Dictionary<PrivatePublicationKey, PrivatePublicationRecord> _records = [];

        public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (!Live(owner, now))
                return Task.FromResult(PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.OwnerUnavailable));
            var key = new PrivatePublicationKey(rootId, objectId, tempId);
            if (_records.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing.Reservation.Owner != owner || existing.Reservation.StoreKind != storeKind ||
                    existing.Reservation.AbsenceRecordDigest != absenceRecordDigest
                    ? PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.Conflict)
                    : PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Existing,
                        new(existing, now, true, false, false)));
            }
            if (_records.Count >= 4)
                throw new InvalidDataException();
            var record = new PrivatePublicationRecord(new(Guid.NewGuid(), key, storeKind, owner, absenceRecordDigest, now));
            _records.Add(key, record);
            return Task.FromResult(PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Reserved,
                new(record, now, true, false, false)));
        }

        public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
            PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (!Live(reservation.Owner, now))
                return Task.FromResult(PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.OwnerUnavailable));
            if (!_records.TryGetValue(reservation.Key, out var record) || record.Reservation != reservation ||
                record.Binding is { } old && (old.ActualIdentity != actualIdentity || old.BoundRecordDigest != boundRecordDigest))
                return Task.FromResult(PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.Conflict));
            var outcome = record.Binding is null ? PrivatePublicationBindOutcome.Bound : PrivatePublicationBindOutcome.Existing;
            record = record.Binding is null ? new(record.Reservation, new(actualIdentity, boundRecordDigest, now)) : record;
            _records[reservation.Key] = record;
            return Task.FromResult(PrivatePublicationBindResult.ForSnapshot(outcome, new(record, now, true, false, false)));
        }

        public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
            Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_records.TryGetValue(new(rootId, objectId, tempId), out var record) || record.Reservation.Owner != owner ||
                record.Reservation.StoreKind != storeKind)
                return Task.FromResult<PrivatePublicationSnapshot?>(null);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<PrivatePublicationSnapshot?>(new(record, now, Live(owner, now), false, false));
        }

        private static bool Live(PrivatePublicationOwner owner, DateTimeOffset now)
        {
            var started = owner.ReportClaim?.StartedAtUtc ??
                owner.LeaseExpiresAtUtc.AddSeconds(-IngestionWorkflowLimits.ClaimLeaseSeconds);
            return now >= started && now < owner.LeaseExpiresAtUtc;
        }
    }
}
