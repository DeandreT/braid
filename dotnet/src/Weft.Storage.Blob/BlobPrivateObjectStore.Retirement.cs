using Azure;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.Blob;

public sealed partial class BlobPrivateObjectStore
{
    public async Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return claim.Retirement.Target.Kind == CleanupTargetKind.FinalObject
                ? await RetireFinalAsync(claim, cancellationToken).ConfigureAwait(false)
                : await RetireTemporaryAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception raw)
        {
            var error = Normalize(raw, cancellationToken);
            if (error is not PrivateObjectStorageException storage) return PrivateObjectRetirementOutcome.Io;
            return storage.Error switch
            {
                PrivateObjectStorageError.Unsupported => PrivateObjectRetirementOutcome.Unsupported,
                PrivateObjectStorageError.UnsafeStorage or PrivateObjectStorageError.Missing or
                    PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Integrity => PrivateObjectRetirementOutcome.UnsafeStorage,
                _ => PrivateObjectRetirementOutcome.Io
            };
        }
    }

    /// <summary>
    /// Under the object gate, replaces the exact published object (or claims the absent name) with
    /// the permanent retirement fence. The fence occupies the final name and a stale
    /// publisher's lease cannot authorize a later commit.
    /// </summary>
    private async Task<PrivateObjectRetirementOutcome> RetireFinalAsync(ObjectCleanupClaim claim, CancellationToken token)
    {
        var id = claim.Retirement.Target.Object;
        var gateName = BlobLayout.ObjectGate(_controlId, id);
        await EnsureGateAsync(gateName, token, finalObject: true).ConfigureAwait(false);
        await using var gate = await TryAcquireGateAsync(gateName, token, finalObject: true).ConfigureAwait(false);
        if (gate is null) return PrivateObjectRetirementOutcome.Busy;
        await ObserveAsync(BlobStoragePhase.FinalGateAcquired, token).ConfigureAwait(false);
        var name = BlobLayout.FinalName(id);
        // A conditional replacement that loses a race is retried once against the new facts.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = await InspectAsync(Blob(name), token).ConfigureAwait(false);
            if (current is { Form: BlobForm.Retired })
            {
                gate.Validate();
                return PrivateObjectRetirementOutcome.Missing;
            }
            if (current is { } existing && existing.Form != BlobForm.Gate && (existing.Form != BlobForm.Object ||
                existing.DeclaredLength != existing.Length || existing.Length > (id.Class == PrivateObjectClass.NativeArtifact
                    ? BlobStorageOptions.NativeHardMaximum : BlobStorageOptions.DerivedHardMaximum)))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            gate.Validate();
            token.ThrowIfCancellationRequested();
            if (await TryFenceAsync(name, current, token, gate.LeaseId).ConfigureAwait(false))
            {
                // No post-fence cancellation check hides an observed physical result.
                await ObserveAsync(BlobStoragePhase.FenceInstalled, CancellationToken.None).ConfigureAwait(false);
                return current is null or { Form: BlobForm.Gate }
                    ? PrivateObjectRetirementOutcome.Missing : PrivateObjectRetirementOutcome.Deleted;
            }
        }
        throw Failure(PrivateObjectStorageError.Io);
    }

    /// <summary>
    /// Under the temporary gate, checks the captured original publication against the complete
    /// immutable seal chain and the actual attempt marker, then fences the attempt name.
    /// </summary>
    private async Task<PrivateObjectRetirementOutcome> RetireTemporaryAsync(ObjectCleanupClaim claim, CancellationToken token)
    {
        var publication = claim.Retirement.Publication ?? throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var reservation = publication.Reservation;
        var key = reservation.Key;
        if (key.RootId != _rootId || key.Object != claim.Retirement.Target.Object ||
            reservation.PublicationId != claim.Retirement.Target.PublicationAttemptId)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var id = key.Object;
        // Retirement and promotion lease the same destination. A publisher whose
        // lease expired cannot send a delayed commit after this retirement settles.
        var objectGateName = BlobLayout.ObjectGate(_controlId, id);
        await EnsureGateAsync(objectGateName, token, finalObject: true).ConfigureAwait(false);
        await using var objectGate = await TryAcquireGateAsync(objectGateName, token, finalObject: true).ConfigureAwait(false);
        if (objectGate is null) return PrivateObjectRetirementOutcome.Busy;
        var gateName = BlobLayout.TempGate(_controlId, id, key.TempId);
        // An existing exact publication leaf is required; a missing temporary gate is never recreated.
        if (await InspectAsync(Blob(gateName), token).ConfigureAwait(false) is not { Form: BlobForm.Gate })
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        await using var gate = await TryAcquireGateAsync(gateName, token).ConfigureAwait(false);
        if (gate is null) return PrivateObjectRetirementOutcome.Busy;
        await ObserveAsync(BlobStoragePhase.TempGateAcquired, token, key).ConfigureAwait(false);

        var absence = await ReadSealAsync(id, key.TempId, BlobRecordKind.Absence, token).ConfigureAwait(false) ??
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var context = absence.Record.Context;
        if (context.OwnerKind != reservation.Owner.Kind || context.OwnerDigest != BlobRecordCodec.OwnerDigest(reservation.Owner) ||
            absence.Digest != reservation.AbsenceRecordDigest)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var reserved = await ReadSealAsync(id, key.TempId, BlobRecordKind.Reservation, token).ConfigureAwait(false);
        var bound = await ReadSealAsync(id, key.TempId, BlobRecordKind.Bound, token).ConfigureAwait(false);
        var prepared = await ReadSealAsync(id, key.TempId, BlobRecordKind.Prepared, token).ConfigureAwait(false);
        var attemptName = BlobLayout.AttemptName(id, key.TempId);
        var actual = await InspectAsync(Blob(attemptName), token).ConfigureAwait(false);
        if (actual is { Form: BlobForm.Retired })
        {
            gate.Validate();
            return PrivateObjectRetirementOutcome.Missing;
        }
        if (actual is { Form: not BlobForm.Attempt }) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        // The marker itself is leased too: an expired cleanup holder cannot later
        // replace an attempt that a live publisher has reacquired.
        await using var attemptGate = actual is not null
            ? await BlobGate.TryAcquireAsync(Blob(attemptName), token).ConfigureAwait(false) : null;
        if (actual is not null && attemptGate is null) return PrivateObjectRetirementOutcome.Busy;
        if (actual is { } expectedAttempt && await InspectAsync(Blob(attemptName), token).ConfigureAwait(false) != expectedAttempt)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);

        if (reserved is null)
        {
            // Death after Reserve committed, before the local reservation seal. The exact captured
            // row and complete absence seal prove only an ABSENT target.
            if (actual is not null || bound is not null || prepared is not null || publication.Binding is not null)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        else
        {
            var acknowledged = reserved.Value.Record;
            if (acknowledged.Context != context || acknowledged.PreviousDigest != absence.Digest ||
                acknowledged.PublicationId != reservation.PublicationId || acknowledged.TimeUtc != reservation.RegisteredAtUtc)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            if (bound is { } localBound)
            {
                var record = localBound.Record;
                if (record.Context != context || record.PreviousDigest != reserved.Value.Digest ||
                    record.PublicationId != reservation.PublicationId)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                var identity = BlobIdentity.Publication(_rootId, _controlId, id.Class, id.Scope, record.Incarnation!.Value);
                if (publication.Binding is { } binding && (binding.BoundRecordDigest != localBound.Digest ||
                    binding.ActualIdentity != identity))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                if (actual is { } present && (present.Incarnation != record.Incarnation ||
                    present.ETag != new ETag(record.AttemptETag!)))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
            else if (publication.Binding is not null)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);

            if (prepared is { } ready)
            {
                var record = ready.Record;
                if (publication.Binding is not { } binding || bound is null || record.Context != context ||
                    record.PreviousDigest != bound.Value.Digest || record.PublicationId != reservation.PublicationId ||
                    record.TimeUtc != binding.BoundAtUtc || record.Incarnation != bound.Value.Record.Incarnation)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
        }
        if (actual is not null && publication.Binding is not null &&
            BlobIdentity.Publication(_rootId, _controlId, id.Class, id.Scope, actual.Value.Incarnation) !=
                publication.Binding.ActualIdentity)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);

        gate.Validate();
        objectGate.Validate();
        attemptGate?.Validate();
        token.ThrowIfCancellationRequested();
        if (!await TryFenceAsync(attemptName, actual, token, attemptGate?.LeaseId).ConfigureAwait(false))
        {
            // Only an existing fence explains a lost race under the held gate.
            return await InspectAsync(Blob(attemptName), CancellationToken.None).ConfigureAwait(false) is { Form: BlobForm.Retired }
                ? PrivateObjectRetirementOutcome.Missing
                : throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        await ObserveAsync(BlobStoragePhase.FenceInstalled, CancellationToken.None, key).ConfigureAwait(false);
        return actual is null ? PrivateObjectRetirementOutcome.Missing : PrivateObjectRetirementOutcome.Deleted;
    }
}
