using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using static Braid.Persistence.Postgres.PrivateObjectMaintenancePersistence;

namespace Braid.Persistence.Postgres;

/// <summary>Shared metadata mechanics, selected only by the two explicit role-specific adapters.</summary>
internal sealed partial class PostgresPrivateObjectPublicationRegistry
{
    private readonly PrivateObjectOwnerKind _kind;
    private readonly Func<Guid, CancellationToken, Task<PrivatePublicationTransaction>> _begin;

    internal PostgresPrivateObjectPublicationRegistry(PrivateObjectOwnerKind kind,
        Func<Guid, CancellationToken, Task<PrivatePublicationTransaction>> begin)
    {
        _kind = kind;
        _begin = begin;
    }

    internal Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absence, CancellationToken cancellationToken)
    {
        var key = new PrivatePublicationKey(rootId, objectId, tempId);
        Validate(owner, key);
        _ = StorageKind(storeKind);
        ArgumentNullException.ThrowIfNull(absence);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            var committing = false;
            PrivatePublicationReserveResult? staged = null;
            try { return await CommitAsync(); }
            catch (NpgsqlException) when (committing && !cancellationToken.IsCancellationRequested &&
                staged?.Snapshot is not null)
            {
                // The original inner scope has disposed before exact natural-key recovery.
                var expected = staged?.Snapshot ?? throw InvalidFacts();
                var retained = await RecoverAsync(owner, storeKind, key, cancellationToken);
                if (retained is null || retained.Record.Reservation != expected.Record.Reservation) throw Unavailable();
                if (!retained.OwnerIsLive) return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.OwnerUnavailable);
                if (!retained.CanWrite) return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.Retired);
                return PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Existing, retained);
            }
            async Task<PrivatePublicationReserveResult> CommitAsync()
            {
                await using var transaction = await _begin(owner.Project.OrganizationId, cancellationToken);
                var result = await ReserveInTransactionAsync(transaction, owner, storeKind, key, absence, cancellationToken);
                staged = result;
                committing = true;
                await transaction.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
        }, cancellationToken);
    }

    internal Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
        PrivatePublicationFileIdentity identity, Sha256Digest digest, CancellationToken cancellationToken)
    {
        ValidateBinding(reservation, identity, digest);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            var committing = false;
            PrivatePublicationBindResult? staged = null;
            try { return await CommitAsync(); }
            catch (NpgsqlException) when (committing && !cancellationToken.IsCancellationRequested && staged?.Snapshot is not null)
            {
                var expected = staged?.Snapshot ?? throw InvalidFacts();
                var retained = await RecoverAsync(reservation.Owner, reservation.StoreKind, reservation.Key, cancellationToken);
                if (retained is null || retained.Record.Reservation != reservation ||
                    retained.Record.Binding != expected.Record.Binding) throw Unavailable();
                if (!retained.OwnerIsLive) return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.OwnerUnavailable);
                if (!retained.CanWrite) return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.Retired);
                return PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Existing, retained);
            }
            async Task<PrivatePublicationBindResult> CommitAsync()
            {
                await using var transaction = await _begin(reservation.Owner.Project.OrganizationId, cancellationToken);
                var result = await BindInTransactionAsync(transaction, reservation, identity, digest, cancellationToken);
                staged = result;
                committing = true;
                await transaction.CommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
        }, cancellationToken);
    }

    internal Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken)
    {
        var key = new PrivatePublicationKey(rootId, objectId, tempId);
        Validate(owner, key);
        _ = StorageKind(storeKind);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _begin(owner.Project.OrganizationId, cancellationToken);
            var result = await GetInTransactionAsync(transaction, owner, storeKind, key, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }

    private async Task<PrivatePublicationSnapshot?> RecoverAsync(PrivatePublicationOwner owner,
        PrivatePublicationStoreKind storeKind, PrivatePublicationKey key, CancellationToken caller)
    {
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await GetAsync(owner, storeKind, key.RootId, key.Object, key.TempId, recovery.Token);
            caller.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw Unavailable(); }
    }

    internal async Task<PrivatePublicationReserveResult> ReserveInTransactionAsync(PrivatePublicationTransaction transaction,
        PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind, PrivatePublicationKey key, Sha256Digest absence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(absence);
        Validate(owner, key);
        var storageKind = StorageKind(storeKind);
        await PrepareAsync(transaction, owner, key, cancellationToken);
        var observed = await ObserveAsync(transaction, owner, key, cancellationToken);
        if (!observed.OwnerLive) return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.OwnerUnavailable);
        if (observed.FinalRetired || observed.TempRetired) return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.Retired);
        if (observed.Record is { } existing)
        {
            if (existing.Reservation.Owner != owner || existing.Reservation.Key != key || existing.Reservation.StoreKind != storeKind ||
                existing.Reservation.AbsenceRecordDigest != absence)
                return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.Conflict);
            return PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Existing, observed.Snapshot());
        }
        // The guard stamps the retained format from this explicit store kind; it is never inferred.
        await using (var insert = transaction.CreateCommand("""
            INSERT INTO braid.private_object_publications
                (organization_id,project_id,mapping_id,revision_id,object_class,object_id,root_id,temp_id,
                 owner_kind,actor_principal_id,ingestion_operation_id,cache_entry_id,derivation_id,
                 owner_generation,owner_instance_id,owner_token,owner_lease_expires_at,absence_record_digest,storage_kind)
            VALUES (@organizationId,@projectId,@mappingId,@revisionId,@objectClass,@objectId,@rootId,@tempId,
                 @ownerKind,@actorId,@operationId,@cacheEntryId,@derivationId,@ownerGeneration,@instanceId,@leaseToken,@expiresAt,@absence,@storageKind)
            """))
        {
            Parameters(insert, owner, key);
            Add(insert, "absence", NpgsqlDbType.Text, absence.Value);
            Add(insert, "storageKind", NpgsqlDbType.Text, storageKind);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) throw InvalidFacts();
        }
        var reserved = await ObserveAsync(transaction, owner, key, cancellationToken);
        if (!reserved.OwnerLive) return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.OwnerUnavailable);
        if (reserved.FinalRetired || reserved.TempRetired) return PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.Retired);
        var snapshot = reserved.Snapshot();
        if (snapshot.Record.Reservation.Owner != owner || snapshot.Record.Reservation.Key != key ||
            snapshot.Record.Reservation.StoreKind != storeKind || snapshot.Record.Reservation.AbsenceRecordDigest != absence ||
            snapshot.Record.Binding is not null) throw InvalidFacts();
        return PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Reserved, snapshot);
    }

    internal async Task<PrivatePublicationBindResult> BindInTransactionAsync(PrivatePublicationTransaction transaction,
        PrivatePublicationReservation reservation, PrivatePublicationFileIdentity identity, Sha256Digest digest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateBinding(reservation, identity, digest);
        await PrepareAsync(transaction, reservation.Owner, reservation.Key, cancellationToken);
        var observed = await ObserveAsync(transaction, reservation.Owner, reservation.Key, cancellationToken);
        if (!observed.OwnerLive) return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.OwnerUnavailable);
        if (observed.FinalRetired || observed.TempRetired) return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.Retired);
        if (observed.Record is not { } record || record.Reservation != reservation)
            return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.Conflict);
        if (record.Binding is { } existing)
            return existing.ActualIdentity == identity && existing.BoundRecordDigest == digest
                ? PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Existing, observed.Snapshot())
                : PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.Conflict);
        await using (var bind = transaction.CreateCommand("""
            UPDATE braid.private_object_publications SET
                control_namespace_id=@control,linux_boot_id=@boot,mount_namespace_device=@namespaceDevice,
                mount_namespace_inode=@namespaceInode,effective_user_id=@euid,
                file_inode=@fileInode,file_device_major=@fileMajor,file_device_minor=@fileMinor,file_mount_id=@fileMount,
                directory_inode=@directoryInode,directory_device_major=@directoryMajor,directory_device_minor=@directoryMinor,
                directory_mount_id=@directoryMount,bound_record_digest=@boundDigest
            WHERE organization_id=@organizationId AND project_id=@projectId AND mapping_id=@mappingId AND revision_id=@revisionId
              AND object_class=@objectClass AND object_id=@objectId AND root_id=@rootId AND temp_id=@tempId
              AND publication_id=@publicationId AND owner_kind=@ownerKind AND actor_principal_id=@actorId
              AND ingestion_operation_id IS NOT DISTINCT FROM @operationId::uuid
              AND cache_entry_id IS NOT DISTINCT FROM @cacheEntryId::uuid AND derivation_id IS NOT DISTINCT FROM @derivationId::uuid
              AND owner_generation=@ownerGeneration AND owner_instance_id=@instanceId AND owner_token=@leaseToken
              AND owner_lease_expires_at=@expiresAt AND absence_record_digest=@absence AND registered_at=@registeredAt AND bound_at IS NULL
            """))
        {
            Parameters(bind, reservation.Owner, reservation.Key);
            Add(bind, "publicationId", NpgsqlDbType.Uuid, reservation.PublicationId);
            Add(bind, "absence", NpgsqlDbType.Text, reservation.AbsenceRecordDigest.Value);
            Add(bind, "registeredAt", NpgsqlDbType.TimestampTz, reservation.RegisteredAtUtc);
            AddIdentity(bind, identity, digest);
            if (await bind.ExecuteNonQueryAsync(cancellationToken) != 1) throw InvalidFacts();
        }
        var bound = await ObserveAsync(transaction, reservation.Owner, reservation.Key, cancellationToken);
        if (!bound.OwnerLive) return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.OwnerUnavailable);
        if (bound.FinalRetired || bound.TempRetired) return PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.Retired);
        var result = bound.Snapshot();
        if (result.Record.Reservation != reservation || result.Record.Binding is not { } binding ||
            binding.ActualIdentity != identity || binding.BoundRecordDigest != digest)
            throw InvalidFacts();
        return PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Bound, result);
    }

    internal async Task<PrivatePublicationSnapshot?> GetInTransactionAsync(PrivatePublicationTransaction transaction,
        PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind, PrivatePublicationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Validate(owner, key);
        _ = StorageKind(storeKind);
        await PrepareAsync(transaction, owner, key, cancellationToken);
        var observed = await ObserveAsync(transaction, owner, key, cancellationToken);
        // Another store kind's registration is never this store's record.
        return observed.Record is { } record && record.Reservation.Owner == owner && record.Reservation.Key == key &&
            record.Reservation.StoreKind == storeKind ? observed.Snapshot() : null;
    }

    private void Validate(PrivatePublicationOwner owner, PrivatePublicationKey key)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(key);
        if (owner.Kind != _kind) throw new ArgumentException("Publication requires the matching dedicated owner pool.", nameof(owner));
        _ = owner.OriginFor(key.Object);
    }

    private void ValidateBinding(PrivatePublicationReservation reservation, PrivatePublicationFileIdentity identity, Sha256Digest digest)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(digest);
        Validate(reservation.Owner, reservation.Key);
        if (identity.RootId != reservation.Key.RootId) throw new ArgumentException("Binding requires the exact reserved root.", nameof(identity));
    }

    private static void AddIdentity(NpgsqlCommand command, PrivatePublicationFileIdentity identity, Sha256Digest digest)
    {
        Add(command, "control", NpgsqlDbType.Uuid, identity.ControlNamespaceId);
        Add(command, "boot", NpgsqlDbType.Uuid, identity.LinuxBootId);
        Add(command, "namespaceDevice", NpgsqlDbType.Numeric, (decimal)identity.MountNamespaceDevice);
        Add(command, "namespaceInode", NpgsqlDbType.Numeric, (decimal)identity.MountNamespaceInode);
        Add(command, "euid", NpgsqlDbType.Bigint, (long)identity.EffectiveUserId);
        Add(command, "fileInode", NpgsqlDbType.Numeric, (decimal)identity.FileIdentity.Inode);
        Add(command, "fileMajor", NpgsqlDbType.Bigint, (long)identity.FileIdentity.DeviceMajor);
        Add(command, "fileMinor", NpgsqlDbType.Bigint, (long)identity.FileIdentity.DeviceMinor);
        Add(command, "fileMount", NpgsqlDbType.Numeric, (decimal)identity.FileIdentity.MountId);
        Add(command, "directoryInode", NpgsqlDbType.Numeric, (decimal)identity.DirectoryIdentity.Inode);
        Add(command, "directoryMajor", NpgsqlDbType.Bigint, (long)identity.DirectoryIdentity.DeviceMajor);
        Add(command, "directoryMinor", NpgsqlDbType.Bigint, (long)identity.DirectoryIdentity.DeviceMinor);
        Add(command, "directoryMount", NpgsqlDbType.Numeric, (decimal)identity.DirectoryIdentity.MountId);
        Add(command, "boundDigest", NpgsqlDbType.Text, digest.Value);
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try { return await operation(); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException) { throw Unavailable(); }
    }
}

/// <summary>Adapts only already-admitted ordinary transactions, without taking ownership in friend methods.</summary>
internal sealed class PrivatePublicationTransaction : IAsyncDisposable
{
    private readonly Func<string, NpgsqlCommand> _command;
    private readonly Func<Guid, CancellationToken, Task> _require;
    private readonly Func<CancellationToken, Task> _commit;
    private readonly Func<ValueTask> _dispose;

    internal PrivatePublicationTransaction(PostgresIngestionTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _command = transaction.CreateCommand;
        _require = transaction.RequireOrganizationAsync;
        _commit = transaction.CommitAsync;
        _dispose = transaction.DisposeAsync;
    }
    internal PrivatePublicationTransaction(PostgresTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _command = transaction.CreateCommand;
        _require = transaction.RequireOrganizationAsync;
        _commit = transaction.CommitAsync;
        _dispose = transaction.DisposeAsync;
    }
    internal NpgsqlCommand CreateCommand(string sql) => _command(sql);
    internal Task RequireOrganizationAsync(Guid organization, CancellationToken token) => _require(organization, token);
    internal Task CommitAsync(CancellationToken token) => _commit(token);
    public ValueTask DisposeAsync() => _dispose();
}
