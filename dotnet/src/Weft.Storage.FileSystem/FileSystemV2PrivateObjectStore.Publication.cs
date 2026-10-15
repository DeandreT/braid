using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner,
        RevisionScope expectedScope, PrivateObjectReference reference, Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateReference(expectedScope, reference);
        ArgumentNullException.ThrowIfNull(source);
        _ = owner.OriginFor(reference.Id);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_registryFactory is null) throw Failure(PrivateObjectStorageError.Unsupported);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRoot();
            using var objectControl = OpenObjectControl(reference.Id, true);
            using var dataParent = OpenDataParent(reference.Id, true);
            var tempId = Guid.NewGuid();
            var key = new PrivatePublicationKey(_marker.RootId, reference.Id, tempId);
            using var tempControl = CreateTempControl(objectControl, tempId);
            using var tempGate = LinuxFileSystem.TryLockGate(tempControl, "temp.gate", true, _userId) ??
                throw Failure(PrivateObjectStorageError.Io);
            await ObserveAsync(V2StoragePhase.TempGateAcquired, cancellationToken, key).ConfigureAwait(false);
            LinuxFileSystem.Identity objectGateIdentity;
            using (var initialGate = LinuxFileSystem.TryLockGate(objectControl, "object.gate", false, _userId) ??
                throw Failure(PrivateObjectStorageError.Io))
            {
                if (HasFence(objectControl, "object.retired") || HasFence(tempControl, "temp.retired"))
                    throw Failure(PrivateObjectStorageError.Missing);
                initialGate.Validate();
                objectGateIdentity = initialGate.Identity;
            }
            if (LinuxFileSystem.TryInspectChild(dataParent, TempName(tempId), out _))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            var incarnation = LinuxFileSystem.ReadIncarnation();
            var context = new V2PublicationContext(_marker.RootId, _marker.ControlId, reference, tempId, owner,
                incarnation.BootId, incarnation.NamespaceDevice, incarnation.NamespaceInode, _userId,
                LinuxFileSystem.PublicationIdentity(_rootIdentity), LinuxFileSystem.PublicationIdentity(_controlIdentity),
                LinuxFileSystem.PublicationIdentity(LinuxFileSystem.Inspect(objectControl).Identity),
                LinuxFileSystem.PublicationIdentity(LinuxFileSystem.Inspect(tempControl).Identity),
                LinuxFileSystem.PublicationIdentity(LinuxFileSystem.Inspect(dataParent).Identity),
                LinuxFileSystem.PublicationIdentity(tempGate.Identity));
            ValidatePublicationPaths(context, dataParent, objectControl, tempControl, tempGate);
            var absence = await InstallSealAsync(tempControl, new V2AbsenceRecord(context), cancellationToken).ConfigureAwait(false);
            var registry = GetRegistry();
            var reservation = await ReserveAsync(registry, context, key, absence.Digest, cancellationToken).ConfigureAwait(false);
            await ObserveAsync(V2StoragePhase.ReservationAcknowledged, cancellationToken, key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var reserved = await InstallSealAsync(tempControl, new V2ReservationRecord(context, absence.Digest,
                LinuxFileSystem.PublicationIdentity(absence.Identity), reservation.PublicationId,
                reservation.RegisteredAtUtc), cancellationToken).ConfigureAwait(false);
            ValidatePublicationPaths(context, dataParent, objectControl, tempControl, tempGate);
            if (HasFence(tempControl, "temp.retired")) throw Failure(PrivateObjectStorageError.Missing);
            LinuxFileSystem.Identity? ownedTemp = null;
            try
            {
                Seal bound;
                PrivatePublicationBinding binding;
                using (var file = LinuxFileSystem.CreateTemporary(dataParent, TempName(tempId)))
                {
                    var initial = LinuxFileSystem.Inspect(file);
                    LinuxFileSystem.RequireFile(initial, LinuxFileSystem.TemporaryFileMode, _userId);
                    if (initial.Size != 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                    ownedTemp = initial.Identity;
                    await ObserveAsync(V2StoragePhase.TemporaryCreated, cancellationToken, key, file: initial.Identity).ConfigureAwait(false);
                    bound = await InstallSealAsync(tempControl, new V2BoundRecord(context, reserved.Digest,
                        LinuxFileSystem.PublicationIdentity(reserved.Identity), reservation.PublicationId,
                        LinuxFileSystem.PublicationIdentity(initial.Identity)), cancellationToken).ConfigureAwait(false);
                    binding = await BindAsync(registry, reservation, context.CreateFileIdentity(
                        LinuxFileSystem.PublicationIdentity(initial.Identity)), bound.Digest, cancellationToken).ConfigureAwait(false);
                    await ObserveAsync(V2StoragePhase.BindingAcknowledged, cancellationToken, key, file: initial.Identity).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidatePublicationPaths(context, dataParent, objectControl, tempControl, tempGate);
                    RequireNamedTemp(context, dataParent, initial.Identity, LinuxFileSystem.TemporaryFileMode, 0);
                    using var output = new FileStream(file, FileAccess.Write, ChunkSize, isAsync: false);
                    await WriteVerifiedAsync(reference, source, output, cancellationToken).ConfigureAwait(false);
                    output.Flush();
                    LinuxFileSystem.MakeReadOnly(file);
                    LinuxFileSystem.SyncFile(file);
                    var complete = LinuxFileSystem.Inspect(file);
                    LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, _userId);
                    if (complete.Identity != initial.Identity || complete.Size != (ulong)reference.ByteCount)
                        throw Failure(PrivateObjectStorageError.UnsafeStorage);
                }
                // The writable descriptor is actually closed before Prepared and final flock.
                var preparedIdentity = LinuxFileSystem.FileIdentity(((V2BoundRecord)bound.Record).DataFileIdentity);
                var prepared = await InstallSealAsync(tempControl, new V2PreparedRecord(context, bound.Digest,
                    LinuxFileSystem.PublicationIdentity(bound.Identity), reservation.PublicationId, binding.BoundAtUtc,
                    LinuxFileSystem.PublicationIdentity(preparedIdentity), reference.Digest, reference.ByteCount), cancellationToken).ConfigureAwait(false);
                await ObserveAsync(V2StoragePhase.PayloadPrepared, cancellationToken, key, file: preparedIdentity).ConfigureAwait(false);
                using var finalGate = LinuxFileSystem.TryLockGate(objectControl, "object.gate", true, _userId) ??
                    throw Failure(PrivateObjectStorageError.Io);
                await ObserveAsync(V2StoragePhase.FinalGateAcquired, cancellationToken, key).ConfigureAwait(false);
                ValidatePublicationPaths(context, dataParent, objectControl, tempControl, tempGate);
                finalGate.Validate();
                if (finalGate.Identity != objectGateIdentity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                if (HasFence(objectControl, "object.retired") || HasFence(tempControl, "temp.retired"))
                    throw Failure(PrivateObjectStorageError.Missing);
                RequireNamedTemp(context, dataParent, preparedIdentity, LinuxFileSystem.FinalFileMode, reference.ByteCount);
                var actualPrepared = ReadSeal(tempControl, V2RecordKind.Prepared);
                var actualBound = ReadSeal(tempControl, V2RecordKind.Bound);
                if (actualPrepared != prepared || actualBound != bound || binding.ActualIdentity != context.CreateFileIdentity(
                    ((V2PreparedRecord)prepared.Record).DataFileIdentity))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                cancellationToken.ThrowIfCancellationRequested();
                if (LinuxFileSystem.PublishNoReplace(dataParent, TempName(tempId), FinalName(reference.Id)))
                {
                    ownedTemp = null;
                    return PrivateObjectPublishResult.Created; // no fallible post-rename operation/observer
                }
                try
                {
                    _ = await ReadVerifiedFileAsync(reference, dataParent, cancellationToken).ConfigureAwait(false);
                    ValidatePublicationPaths(context, dataParent, objectControl, tempControl, tempGate);
                    finalGate.Validate();
                    cancellationToken.ThrowIfCancellationRequested();
                    return PrivateObjectPublishResult.AlreadyPresent;
                }
                catch (PrivateObjectStorageException error) when
                    (error.Error is PrivateObjectStorageError.Integrity or PrivateObjectStorageError.Missing)
                { throw Failure(PrivateObjectStorageError.Conflict); }
            }
            finally
            {
                if (ownedTemp is { } identity)
                    _ = LinuxFileSystem.UnlinkExact(dataParent, TempName(tempId), identity, _userId);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (Exception error) { throw NormalizeUnexpected(error); }
    }

    private async Task<PrivatePublicationReservation> ReserveAsync(IPrivateObjectPublicationRegistry registry,
        V2PublicationContext context, PrivatePublicationKey key, Sha256Digest absenceDigest, CancellationToken token)
    {
        PrivatePublicationReserveResult result;
        try
        {
            result = await registry.ReserveAsync(context.Owner, key.RootId, key.Object, key.TempId,
                absenceDigest, token).ConfigureAwait(false);
        }
        catch
        {
            // The original Task has settled. Recover only its exact natural key, never a new TempId.
            token.ThrowIfCancellationRequested();
            return Accept(await RecoverAsync(registry, context.Owner, key, token).ConfigureAwait(false));
        }
        if (result.Outcome == PrivatePublicationReserveOutcome.Retired) throw Failure(PrivateObjectStorageError.Missing);
        if (result.Outcome == PrivatePublicationReserveOutcome.Conflict) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (result.Outcome == PrivatePublicationReserveOutcome.OwnerUnavailable) throw Failure(PrivateObjectStorageError.Io);
        return Accept(result.Snapshot);

        PrivatePublicationReservation Accept(PrivatePublicationSnapshot? snapshot)
        {
            RequireWritable(snapshot);
            var reservation = snapshot!.Record.Reservation;
            if (reservation.Key != key || reservation.Owner != context.Owner ||
                reservation.AbsenceRecordDigest != absenceDigest || snapshot.Record.Binding is not null)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            token.ThrowIfCancellationRequested();
            return reservation;
        }
    }

    private async Task<PrivatePublicationBinding> BindAsync(IPrivateObjectPublicationRegistry registry,
        PrivatePublicationReservation reservation, PrivatePublicationFileIdentity identity, Sha256Digest digest,
        CancellationToken token)
    {
        PrivatePublicationBindResult result;
        try
        {
            result = await registry.BindAsync(reservation, identity, digest, token).ConfigureAwait(false);
        }
        catch
        {
            token.ThrowIfCancellationRequested();
            return Accept(await RecoverAsync(registry, reservation.Owner, reservation.Key, token).ConfigureAwait(false));
        }
        if (result.Outcome == PrivatePublicationBindOutcome.Retired) throw Failure(PrivateObjectStorageError.Missing);
        if (result.Outcome == PrivatePublicationBindOutcome.Conflict) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (result.Outcome == PrivatePublicationBindOutcome.OwnerUnavailable) throw Failure(PrivateObjectStorageError.Io);
        return Accept(result.Snapshot);

        PrivatePublicationBinding Accept(PrivatePublicationSnapshot? snapshot)
        {
            RequireWritable(snapshot);
            var binding = snapshot!.Record.Binding;
            if (snapshot.Record.Reservation != reservation || binding is null || binding.ActualIdentity != identity ||
                binding.BoundRecordDigest != digest)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            token.ThrowIfCancellationRequested();
            return binding;
        }
    }

    private static async Task<PrivatePublicationSnapshot?> RecoverAsync(IPrivateObjectPublicationRegistry registry,
        PrivatePublicationOwner owner, PrivatePublicationKey key, CancellationToken token)
    {
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
        recovery.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await registry.GetAsync(owner, key.RootId, key.Object, key.TempId, recovery.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Failure(PrivateObjectStorageError.Io); }
    }

    private static void RequireWritable(PrivatePublicationSnapshot? snapshot)
    {
        if (snapshot is null) throw Failure(PrivateObjectStorageError.Io);
        if (snapshot.FinalObjectRetired || snapshot.TemporaryRetired) throw Failure(PrivateObjectStorageError.Missing);
        if (!snapshot.CanWrite) throw Failure(PrivateObjectStorageError.Io);
    }

    private void RequireNamedTemp(V2PublicationContext context, SafeFileHandle parent,
        LinuxFileSystem.Identity expected, uint mode, long count)
    {
        if (!LinuxFileSystem.TryInspectChild(parent, TempName(context.TempId), out var stat))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        LinuxFileSystem.RequireFile(stat, mode, _userId);
        if (stat.Identity != expected || stat.Size != (ulong)count)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private async Task WriteVerifiedAsync(PrivateObjectReference reference, Stream source, FileStream output,
        CancellationToken token)
    {
        var buffer = new byte[ChunkSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read is < 0 or > ChunkSize) throw Failure(PrivateObjectStorageError.Io);
            if (read == 0) break;
            count += read;
            if (count > LimitFor(reference.Id.Class)) throw Failure(PrivateObjectStorageError.Limit);
            if (count > reference.ByteCount) throw Failure(PrivateObjectStorageError.Integrity);
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (count != reference.ByteCount ||
            Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Digest.Value)
            throw Failure(PrivateObjectStorageError.Integrity);
        token.ThrowIfCancellationRequested();
    }
}
