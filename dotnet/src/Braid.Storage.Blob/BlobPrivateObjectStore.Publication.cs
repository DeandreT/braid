using System.Buffers.Binary;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.Blob;

public sealed partial class BlobPrivateObjectStore
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
            var id = reference.Id;
            var tempId = Guid.NewGuid();
            var key = new PrivatePublicationKey(_rootId, id, tempId);
            var objectGateName = BlobLayout.ObjectGate(_controlId, id);
            var tempGateName = BlobLayout.TempGate(_controlId, id, tempId);
            var finalName = BlobLayout.FinalName(id);
            var attemptName = BlobLayout.AttemptName(id, tempId);
            await EnsureGateAsync(objectGateName, cancellationToken, finalObject: true).ConfigureAwait(false);
            // An in-progress retirement holds the object gate; refuse before any registration or source read.
            await using (var initialGate = await TryAcquireGateAsync(objectGateName, cancellationToken, finalObject: true).ConfigureAwait(false))
                if (initialGate is null) throw Failure(PrivateObjectStorageError.Io);
            // The TempID is fresh, so its gate must not already exist.
            if (!await TryCreateAsync(BlockBlob(tempGateName), [], BlobFacts.Metadata(BlobForm.Gate), cancellationToken)
                .ConfigureAwait(false))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            await using var tempGate = await TryAcquireGateAsync(tempGateName, cancellationToken).ConfigureAwait(false) ??
                throw Failure(PrivateObjectStorageError.Io);
            await ObserveAsync(BlobStoragePhase.TempGateAcquired, cancellationToken, key).ConfigureAwait(false);
            if (await InspectAsync(Blob(finalName), cancellationToken).ConfigureAwait(false) is { Form: BlobForm.Retired })
                throw Failure(PrivateObjectStorageError.Missing);
            if (await InspectAsync(Blob(attemptName), cancellationToken).ConfigureAwait(false) is not null)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            var context = BlobPublicationContext.For(_rootId, _controlId, reference, tempId, owner);
            var absence = await InstallSealAsync(new(BlobRecordKind.Absence, context), cancellationToken).ConfigureAwait(false);
            var registry = GetRegistry();
            var reservation = await ReserveAsync(registry, owner, key, absence.Digest, cancellationToken).ConfigureAwait(false);
            await ObserveAsync(BlobStoragePhase.ReservationAcknowledged, cancellationToken, key).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var reserved = await InstallSealAsync(new(BlobRecordKind.Reservation, context, absence.Digest,
                reservation.PublicationId, reservation.RegisteredAtUtc), cancellationToken).ConfigureAwait(false);
            tempGate.Validate();

            var incarnation = BlobIdentity.NewIncarnation(_rootId, id.Class, id.Scope);
            ETag? ownedAttempt = null;
            try
            {
                // The attempt marker is the tracked temporary: an empty create-only blob whose
                // incarnation is bound before any source byte is read or staged.
                if (!await TryCreateAsync(BlockBlob(attemptName), [], BlobFacts.AttemptMetadata(incarnation), cancellationToken)
                    .ConfigureAwait(false))
                    throw Failure(await InspectAsync(Blob(attemptName), cancellationToken).ConfigureAwait(false) is { Form: BlobForm.Retired }
                        ? PrivateObjectStorageError.Missing : PrivateObjectStorageError.UnsafeStorage);
                var created = await InspectAsync(Blob(attemptName), cancellationToken).ConfigureAwait(false);
                if (created is not { Form: BlobForm.Attempt } attempt || attempt.Incarnation != incarnation)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                ownedAttempt = attempt.ETag;
                await using var attemptGate = await BlobGate.TryAcquireAsync(Blob(attemptName), cancellationToken).ConfigureAwait(false) ??
                    throw Failure(PrivateObjectStorageError.Io);
                await ObserveAsync(BlobStoragePhase.AttemptCreated, cancellationToken, key).ConfigureAwait(false);
                var bound = await InstallSealAsync(new(BlobRecordKind.Bound, context, reserved.Digest, reservation.PublicationId,
                    Incarnation: incarnation, AttemptETag: attempt.ETag.ToString()), cancellationToken).ConfigureAwait(false);
                var identity = BlobIdentity.Publication(_rootId, _controlId, id.Class, id.Scope, incarnation);
                var binding = await BindAsync(registry, reservation, identity, bound.Digest, cancellationToken).ConfigureAwait(false);
                await ObserveAsync(BlobStoragePhase.BindingAcknowledged, cancellationToken, key).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                tempGate.Validate();
                await RequireAttemptAsync(attemptName, attempt, cancellationToken).ConfigureAwait(false);

                // All payload writes carry the lease of their actual destination. A stale
                // publisher is therefore rejected by storage, even after a process pause.
                await using var finalGate = await TryAcquireGateAsync(objectGateName, cancellationToken, finalObject: true).ConfigureAwait(false) ??
                    throw Failure(PrivateObjectStorageError.Io);
                await ObserveAsync(BlobStoragePhase.FinalGateAcquired, cancellationToken, key).ConfigureAwait(false);
                await attemptGate.RenewAsync(cancellationToken).ConfigureAwait(false);
                await RequireAttemptAsync(attemptName, attempt, cancellationToken).ConfigureAwait(false);
                var destination = await InspectAsync(Blob(finalName), cancellationToken).ConfigureAwait(false);
                if (destination is { Form: BlobForm.Retired })
                    throw Failure(PrivateObjectStorageError.Missing);
                if (destination is not { Form: BlobForm.Gate } &&
                    (destination is not { Form: BlobForm.Object } data || data.Length != data.DeclaredLength || data.Length > LimitFor(id.Class)))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                var blocks = await StageVerifiedAsync(reference, tempId, source, finalGate.LeaseId, cancellationToken).ConfigureAwait(false);
                await ObserveAsync(BlobStoragePhase.PayloadStaged, cancellationToken, key).ConfigureAwait(false);
                var prepared = await InstallSealAsync(new(BlobRecordKind.Prepared, context, bound.Digest, reservation.PublicationId,
                    binding.BoundAtUtc, incarnation, null, reference.Digest, reference.ByteCount, blocks.Count), cancellationToken)
                    .ConfigureAwait(false);
                await ObserveAsync(BlobStoragePhase.PayloadPrepared, cancellationToken, key).ConfigureAwait(false);

                await tempGate.RenewAsync(cancellationToken).ConfigureAwait(false);
                await RequireAttemptAsync(attemptName, attempt, cancellationToken).ConfigureAwait(false);
                await attemptGate.RenewAsync(cancellationToken).ConfigureAwait(false);
                var actualBound = await ReadSealAsync(id, tempId, BlobRecordKind.Bound, cancellationToken).ConfigureAwait(false);
                var actualPrepared = await ReadSealAsync(id, tempId, BlobRecordKind.Prepared, cancellationToken).ConfigureAwait(false);
                if (actualBound?.Digest != bound.Digest || actualPrepared?.Digest != prepared.Digest || binding.ActualIdentity != identity)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                var existing = await InspectAsync(Blob(finalName), cancellationToken).ConfigureAwait(false);
                if (existing is { Form: BlobForm.Retired }) throw Failure(PrivateObjectStorageError.Missing);
                await ObserveAsync(BlobStoragePhase.CommitPending, cancellationToken, key).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                tempGate.Validate();
                attemptGate.Validate();
                finalGate.Validate();
                if (existing is { Form: BlobForm.Gate } vacant && await TryCommitAsync(finalName, blocks, incarnation, reference,
                        vacant.ETag, finalGate.LeaseId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    // The committed name is now authoritative. Removing the empty marker is best
                    // effort and cannot change the result; tracked-temporary retirement covers it.
                    await DeleteAttemptQuietlyAsync(attemptName, attempt.ETag, attemptGate.LeaseId).ConfigureAwait(false);
                    ownedAttempt = null;
                    return PrivateObjectPublishResult.Created;
                }
                if (await InspectAsync(Blob(finalName), cancellationToken).ConfigureAwait(false) is { Form: BlobForm.Retired })
                    throw Failure(PrivateObjectStorageError.Missing);
                try
                {
                    _ = await ReadFinalAsync(reference, cancellationToken).ConfigureAwait(false);
                    await attemptGate.RenewAsync(cancellationToken).ConfigureAwait(false);
                    await finalGate.RenewAsync(cancellationToken).ConfigureAwait(false);
                    tempGate.Validate();
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
                if (ownedAttempt is { } etag)
                    await DeleteAttemptQuietlyAsync(attemptName, etag).ConfigureAwait(false);
            }
        }
        catch (Exception error) { throw Normalize(error, cancellationToken); }
    }

    private async Task RequireAttemptAsync(string name, BlobFacts expected, CancellationToken token)
    {
        var actual = await InspectAsync(Blob(name), token).ConfigureAwait(false);
        if (actual is { Form: BlobForm.Retired }) throw Failure(PrivateObjectStorageError.Missing);
        if (actual is not { Form: BlobForm.Attempt } current || current.ETag != expected.ETag ||
            current.Incarnation != expected.Incarnation)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private async Task DeleteAttemptQuietlyAsync(string name, ETag etag, string? leaseId = null)
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Blob(name).DeleteAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { IfMatch = etag, LeaseId = leaseId }, bounded.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is RequestFailedException or OperationCanceledException or AggregateException) { }
    }

    /// <summary>
    /// Streams the bounded source into uncommitted blocks on the final name, enforcing the exact
    /// reference digest, count and class limit. Uncommitted blocks are never readable and become
    /// content only through the later create-only commit of exactly these block IDs.
    /// </summary>
    private async Task<IReadOnlyList<string>> StageVerifiedAsync(PrivateObjectReference reference, Guid tempId, Stream source,
        string leaseId, CancellationToken token)
    {
        var target = BlockBlob(BlobLayout.FinalName(reference.Id));
        var blocks = new List<string>();
        var buffer = new byte[BlockSize];
        var filled = 0;
        long count = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(filled, Math.Min(ChunkSize, buffer.Length - filled)), token)
                .ConfigureAwait(false);
            if (read < 0 || read > ChunkSize) throw Failure(PrivateObjectStorageError.Io);
            if (read == 0) break;
            count += read;
            if (count > LimitFor(reference.Id.Class)) throw Failure(PrivateObjectStorageError.Limit);
            if (count > reference.ByteCount) throw Failure(PrivateObjectStorageError.Integrity);
            hash.AppendData(buffer.AsSpan(filled, read));
            filled += read;
            if (filled == buffer.Length)
            {
                await StageAsync(target, tempId, blocks, buffer, filled, leaseId, token).ConfigureAwait(false);
                filled = 0;
            }
        }
        if (count != reference.ByteCount || Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Digest.Value)
            throw Failure(PrivateObjectStorageError.Integrity);
        if (filled > 0) await StageAsync(target, tempId, blocks, buffer, filled, leaseId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return blocks;
    }

    private static async Task StageAsync(BlockBlobClient target, Guid tempId, List<string> blocks, byte[] buffer, int count,
        string leaseId, CancellationToken token)
    {
        // Every block ID in this store has one fixed length: TempID, big-endian ordinal and padding.
        var raw = new byte[24];
        tempId.TryWriteBytes(raw, bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(16), blocks.Count);
        var blockId = Convert.ToBase64String(raw);
        using var content = new MemoryStream(buffer, 0, count, writable: false);
        await target.StageBlockAsync(blockId, content, new BlockBlobStageBlockOptions
        {
            Conditions = new BlobRequestConditions { LeaseId = leaseId },
            TransferValidation = new() { ChecksumAlgorithm = Azure.Storage.StorageChecksumAlgorithm.MD5 }
        }, token).ConfigureAwait(false);
        blocks.Add(blockId);
    }

    /// <summary>The sole promotion from an empty reservation, conditional on its ETag and current data lease.</summary>
    private async Task<bool> TryCommitAsync(string name, IReadOnlyList<string> blocks, ulong incarnation,
        PrivateObjectReference reference, ETag reservation, string leaseId, CancellationToken token)
    {
        try
        {
            await BlockBlob(name).CommitBlockListAsync(blocks, new CommitBlockListOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = reservation, LeaseId = leaseId },
                Metadata = BlobFacts.ObjectMetadata(incarnation, reference)
            }, token).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            if (await InspectAsync(Blob(name), token).ConfigureAwait(false) is not { Form: BlobForm.Object or BlobForm.Retired })
                throw Failure(PrivateObjectStorageError.Io);
            return false;
        }
        catch (RequestFailedException error) when (error.Status == 400)
        {
            // Another commit discards these uncommitted blocks; only an occupied name explains that.
            if (await InspectAsync(Blob(name), token).ConfigureAwait(false) is not { Form: BlobForm.Object or BlobForm.Retired }) throw;
            return false;
        }
    }

    private async Task<PrivatePublicationReservation> ReserveAsync(IPrivateObjectPublicationRegistry registry,
        PrivatePublicationOwner owner, PrivatePublicationKey key, Sha256Digest absenceDigest, CancellationToken token)
    {
        PrivatePublicationReserveResult result;
        try
        {
            result = await registry.ReserveAsync(owner, PublicationStoreKind, key.RootId, key.Object, key.TempId, absenceDigest, token)
                .ConfigureAwait(false);
        }
        catch
        {
            // The original Task has settled. Recover only its exact natural key, never a new TempId.
            token.ThrowIfCancellationRequested();
            return Accept(await RecoverAsync(registry, owner, key, token).ConfigureAwait(false));
        }
        if (result.Outcome == PrivatePublicationReserveOutcome.Retired) throw Failure(PrivateObjectStorageError.Missing);
        if (result.Outcome == PrivatePublicationReserveOutcome.Conflict) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (result.Outcome == PrivatePublicationReserveOutcome.OwnerUnavailable) throw Failure(PrivateObjectStorageError.Io);
        return Accept(result.Snapshot);

        PrivatePublicationReservation Accept(PrivatePublicationSnapshot? snapshot)
        {
            RequireWritable(snapshot);
            var reservation = snapshot!.Record.Reservation;
            if (reservation.Key != key || reservation.StoreKind != PublicationStoreKind || reservation.Owner != owner ||
                reservation.AbsenceRecordDigest != absenceDigest || snapshot.Record.Binding is not null)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            token.ThrowIfCancellationRequested();
            return reservation;
        }
    }

    private static async Task<PrivatePublicationBinding> BindAsync(IPrivateObjectPublicationRegistry registry,
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
            return await registry.GetAsync(owner, PublicationStoreKind, key.RootId, key.Object, key.TempId, recovery.Token)
                .ConfigureAwait(false);
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
}
