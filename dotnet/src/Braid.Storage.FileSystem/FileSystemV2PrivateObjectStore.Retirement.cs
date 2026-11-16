using Microsoft.Win32.SafeHandles;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    public async Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateRoot();
            return claim.Retirement.Target.Kind == CleanupTargetKind.FinalObject
                ? await RetireFinalAsync(claim, cancellationToken).ConfigureAwait(false)
                : await RetireTemporaryAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException error)
        {
            return error.Error switch
            {
                PrivateObjectStorageError.Unsupported => PrivateObjectRetirementOutcome.Unsupported,
                PrivateObjectStorageError.UnsafeStorage or PrivateObjectStorageError.Missing or
                    PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Integrity => PrivateObjectRetirementOutcome.UnsafeStorage,
                _ => PrivateObjectRetirementOutcome.Io
            };
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return PrivateObjectRetirementOutcome.Unsupported; }
        catch (ArgumentException) { return PrivateObjectRetirementOutcome.UnsafeStorage; }
        catch { return PrivateObjectRetirementOutcome.Io; }
    }

    private async Task<PrivateObjectRetirementOutcome> RetireFinalAsync(ObjectCleanupClaim claim, CancellationToken token)
    {
        var id = claim.Retirement.Target.Object;
        using var control = OpenObjectControl(id, true);
        using var gate = LinuxFileSystem.TryLockGate(control, "object.gate", true, _userId);
        if (gate is null) return PrivateObjectRetirementOutcome.Busy;
        await ObserveAsync(V2StoragePhase.FinalGateAcquired, token).ConfigureAwait(false);
        ValidateRoot();
        ValidateObjectControl(id, control);
        gate.Validate();
        token.ThrowIfCancellationRequested();
        EstablishFence(control, "object.retired");
        await ObserveAsync(V2StoragePhase.FenceInstalled, token).ConfigureAwait(false);
        SafeFileHandle data;
        try { data = OpenDataParent(id, false); }
        catch (PrivateObjectStorageException error) when (error.Error == PrivateObjectStorageError.Missing)
        {
            ValidateRoot();
            ValidateObjectControl(id, control);
            gate.Validate();
            token.ThrowIfCancellationRequested();
            return PrivateObjectRetirementOutcome.Missing;
        }
        using (data)
        {
            if (!LinuxFileSystem.TryInspectChild(data, FinalName(id), out var stat))
            {
                ValidateRoot();
                ValidateDataParent(id, data);
                ValidateObjectControl(id, control);
                gate.Validate();
                token.ThrowIfCancellationRequested();
                return PrivateObjectRetirementOutcome.Missing;
            }
            LinuxFileSystem.RequireFile(stat, LinuxFileSystem.FinalFileMode, _userId);
            ValidateRoot();
            ValidateDataParent(id, data);
            ValidateObjectControl(id, control);
            gate.Validate();
            using var sync = LinuxFileSystem.OpenSyncDirectory(data);
            token.ThrowIfCancellationRequested();
            var deleted = LinuxFileSystem.UnlinkExact(data, FinalName(id), stat.Identity, _userId,
                LinuxFileSystem.FinalFileMode, stat.Size);
            LinuxFileSystem.SyncDirectory(data, sync);
            ValidateRoot();
            ValidateDataParent(id, data);
            ValidateObjectControl(id, control);
            gate.Validate();
            // No post-unlink cancellation check hides an observed physical result.
            return deleted ? PrivateObjectRetirementOutcome.Deleted : PrivateObjectRetirementOutcome.Missing;
        }
    }

    private async Task<PrivateObjectRetirementOutcome> RetireTemporaryAsync(ObjectCleanupClaim claim, CancellationToken token)
    {
        var publication = claim.Retirement.Publication ?? throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var reservation = publication.Reservation;
        var key = reservation.Key;
        // A publication registered by another store kind is never this store's temporary.
        if (reservation.StoreKind != PublicationStoreKind || key.RootId != _marker.RootId || key.Object != claim.Retirement.Target.Object ||
            reservation.PublicationId != claim.Retirement.Target.PublicationAttemptId)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var objectControl = OpenObjectControl(key.Object, false);
        using var tempControl = OpenTempControl(objectControl, key.TempId);
        using var tempGate = LinuxFileSystem.TryLockGate(tempControl, "temp.gate", true, _userId);
        if (tempGate is null) return PrivateObjectRetirementOutcome.Busy;
        await ObserveAsync(V2StoragePhase.TempGateAcquired, token, key).ConfigureAwait(false);
        var absence = ReadSeal(tempControl, V2RecordKind.Absence);
        RequireNoStage(tempControl, V2RecordKind.Absence);
        var context = absence.Record.Context;
        if (context.RootId != key.RootId || context.ControlNamespaceId != _marker.ControlId ||
            context.Reference.Id != key.Object || context.TempId != key.TempId ||
            context.Owner != reservation.Owner || absence.Digest != reservation.AbsenceRecordDigest ||
            context.EffectiveUserId != _userId)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var current = LinuxFileSystem.ReadIncarnation();
        if (context.BootId != current.BootId || context.MountNamespaceDevice != current.NamespaceDevice ||
            context.MountNamespaceInode != current.NamespaceInode)
            throw Failure(PrivateObjectStorageError.Unsupported);
        if (LinuxFileSystem.FileIdentity(context.RootIdentity) != _rootIdentity ||
            LinuxFileSystem.FileIdentity(context.ControlIdentity) != _controlIdentity)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var data = OpenDataParent(key.Object, false);
        ValidatePublicationPaths(context, data, objectControl, tempControl, tempGate);
        var reserved = ReadOptionalSeal(tempControl, V2RecordKind.Reservation);
        var bound = ReadOptionalSeal(tempControl, V2RecordKind.Bound);
        var prepared = ReadOptionalSeal(tempControl, V2RecordKind.Prepared);
        RequireNoStage(tempControl, V2RecordKind.Reservation);
        var present = LinuxFileSystem.TryInspectChild(data, TempName(key.TempId), out var actual);

        if (reserved is null)
        {
            // Genuine death after Reserve committed, before local acknowledgement seal.
            // The exact captured row + complete absence seal prove only an ABSENT target.
            RequireNoStage(tempControl, V2RecordKind.Bound);
            RequireNoStage(tempControl, V2RecordKind.Prepared);
            if (present || bound is not null || prepared is not null || publication.Binding is not null)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        else
        {
            if (reserved.Record is not V2ReservationRecord acknowledged || acknowledged.Context != context ||
                acknowledged.AbsenceRecordDigest != absence.Digest ||
                acknowledged.AbsenceRecordIdentity != LinuxFileSystem.PublicationIdentity(absence.Identity) ||
                acknowledged.PublicationId != reservation.PublicationId || acknowledged.RegisteredAtUtc != reservation.RegisteredAtUtc)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            if (bound is not null)
            {
                RequireNoStage(tempControl, V2RecordKind.Bound);
                if (bound.Record is not V2BoundRecord localBound || localBound.Context != context ||
                    localBound.ReservationRecordDigest != reserved.Digest ||
                    localBound.ReservationRecordIdentity != LinuxFileSystem.PublicationIdentity(reserved.Identity) ||
                    localBound.PublicationId != reservation.PublicationId)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                if (publication.Binding is { } binding && (binding.BoundRecordDigest != bound.Digest ||
                    binding.ActualIdentity != context.CreateFileIdentity(localBound.DataFileIdentity)))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                if (present && actual.Identity != LinuxFileSystem.FileIdentity(localBound.DataFileIdentity))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
            else
            {
                if (publication.Binding is not null) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                ValidateUnacceptedStage(tempControl, V2RecordKind.Bound);
            }

            if (prepared is not null)
            {
                RequireNoStage(tempControl, V2RecordKind.Prepared);
                if (publication.Binding is not { } binding || bound is null ||
                    prepared.Record is not V2PreparedRecord ready || ready.Context != context ||
                    ready.BoundRecordDigest != bound.Digest || ready.BoundRecordIdentity != LinuxFileSystem.PublicationIdentity(bound.Identity) ||
                    ready.PublicationId != reservation.PublicationId || ready.BoundAtUtc != binding.BoundAtUtc ||
                    ready.DataFileIdentity != binding.ActualIdentity.FileIdentity ||
                    ready.VerifiedDigest != context.Reference.Digest || ready.VerifiedByteCount != context.Reference.ByteCount)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                if (present && ((actual.Mode & 0xfff) != LinuxFileSystem.FinalFileMode ||
                    actual.Size != (ulong)ready.VerifiedByteCount))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
            else
            {
                ValidateUnacceptedStage(tempControl, V2RecordKind.Prepared);
                if (publication.Binding is null && LinuxFileSystem.TryInspectChild(tempControl, "prepared.stage", out _))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
        }

        if (present)
        {
            if (publication.Binding is null)
            {
                LinuxFileSystem.RequireFile(actual, LinuxFileSystem.TemporaryFileMode, _userId);
                if (actual.Size != 0 || reserved is null) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
            else
            {
                var mode = (uint)(actual.Mode & 0xfff);
                if (mode is not (LinuxFileSystem.TemporaryFileMode or LinuxFileSystem.FinalFileMode) ||
                    actual.Size > (ulong)(key.Object.Class == PrivateObjectClass.NativeArtifact
                        ? FileSystemStorageOptions.NativeHardMaximum : FileSystemStorageOptions.DerivedHardMaximum) ||
                    actual.Identity != LinuxFileSystem.FileIdentity(publication.Binding.ActualIdentity.FileIdentity))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                LinuxFileSystem.RequireFile(actual, mode, _userId);
            }
        }
        ValidatePublicationPaths(context, data, objectControl, tempControl, tempGate);
        token.ThrowIfCancellationRequested();
        EstablishFence(tempControl, "temp.retired");
        await ObserveAsync(V2StoragePhase.FenceInstalled, token, key).ConfigureAwait(false);
        ValidatePublicationPaths(context, data, objectControl, tempControl, tempGate);
        if (!present)
        {
            if (LinuxFileSystem.TryInspectChild(data, TempName(key.TempId), out _))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            token.ThrowIfCancellationRequested();
            return PrivateObjectRetirementOutcome.Missing;
        }
        using var directorySync = LinuxFileSystem.OpenSyncDirectory(data);
        token.ThrowIfCancellationRequested();
        var removed = LinuxFileSystem.UnlinkExact(data, TempName(key.TempId), actual.Identity, _userId,
            (uint)(actual.Mode & 0xfff), actual.Size);
        LinuxFileSystem.SyncDirectory(data, directorySync);
        ValidatePublicationPaths(context, data, objectControl, tempControl, tempGate);
        return removed ? PrivateObjectRetirementOutcome.Deleted : PrivateObjectRetirementOutcome.Missing;
    }
}
