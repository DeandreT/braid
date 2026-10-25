using Weft.Domain;

namespace Weft.Application;

public sealed partial class RevisionReportService
{
    public async Task<RevisionReportEnsureResult> EnsureAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid? repairDerivationId = null,
        CancellationToken cancellationToken = default)
    {
        Input(actor, scope, operation, repairDerivationId, false);
        cancellationToken.ThrowIfCancellationRequested();
        RevisionReportRegistration registration;
        RevisionReportKey key;
        Inspection inspection;
        using (var observationDeadline = Deadline(cancellationToken))
        {
            var observationToken = observationDeadline.Token;
            try
            {
                var source = await SourceAsync(actor, scope, null, observationToken);
                if (source is null) return HiddenEnsure();
                var selected = SelectCurrent(source.Source, operation);
                if (selected is null)
                    return RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.ProfileUnavailable);
                (registration, key) = selected.Value;
                var initialSnapshot = await CurrentAsync(actor, key, observationToken);
                if (initialSnapshot is null) return HiddenEnsure();
                inspection = await InspectAsync(actor, initialSnapshot, repairDerivationId, observationToken);
                if (inspection.Result is { } shortcut) return shortcut;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && observationDeadline.IsCancellationRequested)
            {
                throw new RevisionReportServiceException(RevisionReportFailureCategory.TimedOut);
            }
        }
        if (!_workGate.Wait(0))
            return RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.AdmissionBusy);
        try
        {
            using var deadline = Deadline(cancellationToken);
            var token = deadline.Token;
            try
            {
                var snapshot = await CurrentAsync(actor, key, token);
                if (snapshot is null) return HiddenEnsure();
                // Inspect checks the selector before any further selected-object read. A prior
                // authorized corruption-proving read never permits adopting a replacement ID.
                inspection = await InspectAsync(actor, snapshot, repairDerivationId, token);
                if (inspection.Result is { } admittedShortcut) return admittedShortcut;
                try { _storage.ValidateNativeSource(key); }
                catch (RevisionReportValidationException error)
                {
                    throw new RevisionReportServiceException(Failure(error));
                }
                if (!registration.CanExecute)
                    return RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.AdmissionBusy);
                var owned = await MetadataReadAsync(() => inspection.Repair is { } repair
                    ? _metadata.RepairAsync(actor, repair, _serverInstanceId, token)
                    : _metadata.TryClaimAsync(actor, key, _serverInstanceId, token));
                if (owned.Outcome != RevisionReportStoreOutcome.Claimed)
                    return await StoreShortcutAsync(actor, key, owned, repairDerivationId, token);
                var claim = owned.Claim ?? throw Inconsistent();
                if (claim.Key != key || claim.OriginalActor != actor.PrincipalId || claim.ServerInstanceId != _serverInstanceId ||
                    claim.RepairTargetDerivationId != repairDerivationId) throw Inconsistent();
                if (owned.Snapshot is not { } claimedSnapshot) throw Inconsistent();
                MatchSnapshot(actor, key, claimedSnapshot);
                return await RunClaimAsync(actor, registration, claim, token, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new RevisionReportServiceException(RevisionReportFailureCategory.TimedOut);
            }
        }
        finally { _workGate.Release(); }
    }

    private async Task<Inspection> InspectAsync(ActorContext actor, RevisionReportReadSnapshot snapshot,
        Guid? repairId, CancellationToken token)
    {
        if (repairId is { } expected && snapshot.SelectedResult?.DerivationId != expected)
            return new(RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.SelectionChanged), null);
        if (snapshot.SelectedResult is { } selected)
        {
            var read = await ReadAdmittedAsync(actor, selected, token);
            if (read.Source is null) return new(EnsureFromRead(read), null);
            if (repairId is not null && read.Outcome is RevisionReportReadOutcome.ObjectUnavailable or RevisionReportReadOutcome.StorageIntegrity)
            {
                var reason = read.Outcome == RevisionReportReadOutcome.ObjectUnavailable
                    ? RevisionReportRepairReason.Missing : RevisionReportRepairReason.Integrity;
                return new(null, new(snapshot.Key, selected.CacheEntryId, selected.DerivationId, selected.DerivedObject, reason));
            }
            if (repairId is not null)
            {
                var current = await CurrentAsync(actor, snapshot.Key, token);
                if (current is null) return new(HiddenEnsure(), null);
                if (current.SelectedResult?.DerivationId != repairId)
                    return new(RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.SelectionChanged), null);
                if (current.SelectedResult != selected) throw Inconsistent();
            }
            return new(EnsureFromRead(read), null);
        }
        if (snapshot.IsClaimLive) return new(RevisionReportEnsureResult.ForPending(snapshot), null);
        if (snapshot.State == RevisionReportState.Failed && snapshot.NextAttemptAtUtc > snapshot.Source.ObservedAtUtc)
            return new(RevisionReportEnsureResult.ForRetryDelayed(snapshot), null);
        return new(null, null);
    }

    private async Task<RevisionReportEnsureResult> StoreShortcutAsync(ActorContext actor, RevisionReportKey key,
        RevisionReportStoreResult result, Guid? repairId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (result.Snapshot is { } snapshot)
        {
            MatchSnapshot(actor, key, snapshot);
            var inspection = await InspectAsync(actor, snapshot, repairId, token);
            return inspection.Result ?? throw Inconsistent();
        }
        return result.Outcome switch
        {
            RevisionReportStoreOutcome.NotAuthorizedOrNotFound => HiddenEnsure(),
            RevisionReportStoreOutcome.ClaimLost => RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.ClaimLost),
            RevisionReportStoreOutcome.SelectionChanged => RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.SelectionChanged),
            RevisionReportStoreOutcome.GenerationExhausted => RevisionReportEnsureResult.WithoutData(RevisionReportEnsureOutcome.GenerationExhausted),
            _ => throw Inconsistent()
        };
    }

    private static RevisionReportEnsureResult EnsureFromRead(RevisionReportReadResult read) => read.Source is not null
        ? RevisionReportEnsureResult.ForObserved(read)
        : RevisionReportEnsureResult.WithoutData(read.Outcome switch
        {
            RevisionReportReadOutcome.NotAuthorizedOrNotFound => RevisionReportEnsureOutcome.NotAuthorizedOrNotFound,
            RevisionReportReadOutcome.AdmissionBusy => RevisionReportEnsureOutcome.AdmissionBusy,
            RevisionReportReadOutcome.ProfileUnavailable => RevisionReportEnsureOutcome.ProfileUnavailable,
            _ => throw Inconsistent()
        });

    private async Task<RevisionReportEnsureResult> RunClaimAsync(ActorContext actor,
        RevisionReportRegistration registration, RevisionReportClaim claim, CancellationToken token, CancellationToken caller)
    {
        RevisionReportObservation? observation = null;
        RevisionReportCompletion? expected = null;
        using var recovery = new RecoveryBudget();
        try
        {
            try
            {
                _storage.ValidateNativeSource(claim.Key);
                if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                var store = Store();
                if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                var source = claim.Key.Source.NativeObject;
                var returned = await ReadObjectAsync(store, source, token);
                var native = OwnBytes(returned, source, Math.Min(claim.Key.Policy.NativeByteLimit, _storage.NativeByteLimit), token);
                token.ThrowIfCancellationRequested();
                var artifact = NativeArtifact.Admit(claim.Key.Scope, native, claim.Key.Policy.NativeByteLimit);
                token.ThrowIfCancellationRequested();
                if (artifact.Digest != source.Digest || artifact.ByteCount != source.ByteCount) throw Inconsistent();
                if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                var backend = Backend(registration);
                if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                var result = await DeriveAsync(backend, claim.Key, artifact, token);
                observation = result.Observation;
                if (observation is not null && observation.Operation != claim.Key.Operation)
                {
                    observation = null;
                    throw Inconsistent();
                }
                // Read actual failure first: cleanup/isolation observations survive a canceled
                // request token. Neither a canceled token nor a missing report invents validity.
                if (!result.IsUsable)
                    expected = RevisionReportCompletion.ForFailure(result.Failure ?? throw Inconsistent(), observation);
                else
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Key != claim.Key || observation is null) throw Inconsistent();
                    var encoded = _codec.Encode(claim, result, token);
                    _storage.ValidateDerivedReference(claim.Key, encoded.Reference, _readCaps);
                    var bytes = encoded.CopyBytes(token);
                    if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                    var publication = await PublishAsync(store, actor, claim, encoded.Reference, bytes, token);
                    if (publication != PrivateObjectPublishResult.Created)
                        throw new PrivateObjectStorageException(PrivateObjectStorageError.Conflict);
                    _storage.ValidateDerivedReference(claim.Key, encoded.Reference, _readCaps);
                    if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                    returned = await ReadObjectAsync(store, encoded.Reference, token);
                    var caps = RevisionReportWorkflowValue.EffectiveReadCaps(claim.Key, _readCaps);
                    var readback = OwnBytes(returned, encoded.Reference, caps.StoredObjectByteLimit, token);
                    try { _codec.VerifyCandidate(claim, result, encoded, readback, _readCaps, token); }
                    catch (RevisionReportCodecException error) when (error.Failure == RevisionReportCodecFailure.Integrity)
                    {
                        throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
                    }
                    if (await SourceAsync(actor, claim.Key.Scope, claim.Key.Source, token) is null) return HiddenEnsure();
                    expected = RevisionReportCompletion.ForUsable(claim.Key, observation, encoded.Reference);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { expected = RevisionReportCompletion.ForFailure(Failure(error), observation); }

            token.ThrowIfCancellationRequested();
            var completion = expected ?? throw Inconsistent();
            var acknowledgement = await FinishAcknowledgedAsync(actor, claim, completion, token, recovery);
            caller.ThrowIfCancellationRequested();
            return await DeliverAcknowledgedAsync(actor, claim, completion, acknowledgement, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || caller.IsCancellationRequested)
        {
            return await RecoverCanceledAsync(actor, claim, expected, observation, token, caller, recovery);
        }
        catch (OperationCanceledException)
        {
            // An exhausted independent acknowledgement budget is not a fabricated caller or
            // backend cancellation. The original pending/terminal facts remain authoritative.
            throw new RevisionReportServiceException(RevisionReportFailureCategory.BackendUnavailable);
        }
    }

    private static IRevisionReportBackend Backend(RevisionReportRegistration registration)
    {
        try { return registration.GetBackend(); }
        catch (OperationCanceledException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw new RevisionReportServiceException(RevisionReportFailureCategory.BackendUnavailable); }
    }

    private static async Task<RevisionReportBackendResult> DeriveAsync(IRevisionReportBackend backend,
        RevisionReportKey key, NativeArtifact artifact, CancellationToken token)
    {
        try { return await backend.DeriveAsync(key, artifact, token) ?? throw Inconsistent(); }
        catch (OperationCanceledException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception error) when (error is not RevisionReportValidationException)
        {
            throw new RevisionReportServiceException(RevisionReportFailureCategory.BackendFailure);
        }
    }

    private static async Task<PrivateObjectPublishResult> PublishAsync(IPrivateObjectStore store,
        ActorContext actor, RevisionReportClaim claim, PrivateObjectReference reference, byte[] bytes,
        CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            return store is IPrivateObjectPublicationStore publicationStore
                ? await publicationStore.PublishCreateOnlyAsync(PrivatePublicationOwner.ForReport(actor, claim),
                    reference.Id.Scope, reference, stream, token)
                : await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, stream, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (RevisionReportServiceException) { throw; }
        catch (Exception) { throw new RevisionReportServiceException(RevisionReportFailureCategory.StorageIo); }
    }

    private sealed record Inspection(RevisionReportEnsureResult? Result, RevisionReportRepairTarget? Repair);
}
