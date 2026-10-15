using Weft.Domain;

namespace Weft.Application;

/// <summary>
/// Composes short, freshly authorized metadata operations with exact native bytes,
/// private storage, and one validation invocation per claimed attempt.
/// </summary>
public sealed class RevisionIngestionService : IRevisionIngestionService
{
    private static readonly TimeSpan CancellationBookkeepingLimit = TimeSpan.FromSeconds(2);
    private readonly IIngestionMetadataStore _metadata;
    private readonly IIngestionWorkflowReader _workflowReader;
    private readonly IProjectReader _projectReader;
    private readonly IngestionPrivateStoreRegistration _privateStore;
    private readonly IIngestionBackendRegistry _backends;
    private readonly RevisionIngestionOptions _options;
    private readonly Guid _serverInstanceId = Guid.NewGuid();

    public RevisionIngestionService(IIngestionMetadataStore metadata,
        IIngestionWorkflowReader workflowReader, IProjectReader projectReader,
        IngestionPrivateStoreRegistration privateStore, IIngestionBackendRegistry backends,
        RevisionIngestionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(workflowReader);
        ArgumentNullException.ThrowIfNull(projectReader);
        ArgumentNullException.ThrowIfNull(privateStore);
        ArgumentNullException.ThrowIfNull(backends);
        _metadata = metadata;
        _workflowReader = workflowReader;
        _projectReader = projectReader;
        _privateStore = privateStore;
        _backends = backends;
        _options = options ?? new RevisionIngestionOptions();
    }

    public async Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project,
        Guid idempotencyKey, CreateRevisionRequest request, Stream nativeSource,
        CancellationToken cancellationToken = default)
    {
        RequireSubmission(actor, project, idempotencyKey, request, nativeSource);
        if (!await HasUploadAccessAsync(actor, project, cancellationToken))
            return Hidden();

        var existing = await _workflowReader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Create, null, idempotencyKey, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwnKey(existing, actor, project, IngestionOperationKind.Create, null, idempotencyKey);
        return await SubmitAsync(actor, project, idempotencyKey, existing, request.BackendId,
            (digest, count) => IngestionIntent.ForCreate(request.Metadata, request.BackendId,
                request.OriginalFilename, digest, count), nativeSource, cancellationToken);
    }

    public async Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target,
        Guid idempotencyKey, AppendRevisionRequest request, Stream nativeSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var project = new ProjectScope(target.OrganizationId, target.ProjectId);
        RequireSubmission(actor, project, idempotencyKey, request, nativeSource);
        if (!await HasUploadAccessAsync(actor, project, cancellationToken))
            return Hidden();

        var appendTarget = await _workflowReader.GetAppendTargetAsync(actor, target, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (appendTarget is null)
            return Hidden();
        if (appendTarget.Scope != target)
            throw InvalidMetadata();
        var existing = await _workflowReader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Append, target, idempotencyKey, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwnKey(existing, actor, project, IngestionOperationKind.Append, target, idempotencyKey);
        return await SubmitAsync(actor, project, idempotencyKey, existing, appendTarget.BackendId,
            (digest, count) => IngestionIntent.ForAppend(target, appendTarget.BackendId,
                request.OriginalFilename, request.HeadCondition, digest, count), nativeSource, cancellationToken);
    }

    public async Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project,
        Guid operationId, CancellationToken cancellationToken = default)
    {
        RequireIdentity(actor, project, operationId, nameof(operationId));
        if (!await HasUploadAccessAsync(actor, project, cancellationToken))
            return Hidden();
        var receipt = await _metadata.GetReceiptAsync(actor, project, operationId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt is null)
            return Hidden();
        RequireOwnReceipt(receipt, actor, project, operationId);
        if (IsTerminal(receipt.Phase))
            return FromReceipt(receipt);
        return await ClaimAndRunAsync(actor, receipt, null, cancellationToken);
    }

    public async Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor,
        ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
    {
        RequireIdentity(actor, project, operationId, nameof(operationId));
        if (!await HasUploadAccessAsync(actor, project, cancellationToken))
            return null;
        var receipt = await _metadata.GetReceiptAsync(actor, project, operationId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt is null)
            return null;
        RequireOwnReceipt(receipt, actor, project, operationId);
        return RevisionIngestionReceipt.FromMetadata(receipt);
    }

    private async Task<RevisionIngestionResult> SubmitAsync(ActorContext actor, ProjectScope project,
        Guid key, IngestionReceipt? existing, string backendId,
        Func<Sha256Digest, long, IngestionIntent> createIntent, Stream source,
        CancellationToken cancellationToken)
    {
        IngestionBackendProfile profile;
        IngestionAdmissionPolicy policy;
        if (existing is not null)
        {
            profile = existing.Facts.BackendProfile;
            policy = existing.Facts.AdmissionPolicy;
        }
        else
        {
            var registration = _backends.GetCurrent(backendId);
            if (registration is null)
                return RevisionIngestionResult.RejectedBeforeReservation(RevisionIngestionRejection.UnregisteredBackend);
            if (registration.Profile.BackendId != backendId)
                throw new InvalidOperationException("The registered ingestion backend identity is inconsistent.");
            profile = registration.Profile;
            policy = new IngestionAdmissionPolicy(IngestionAdmissionPolicy.InitialIdentity,
                Math.Min(_options.NativeByteLimit, Math.Min(_privateStore.NativeByteLimit,
                    registration.NativeByteLimit)));
        }

        var sourceRead = await IngestionSourceAdmission.ReadAsync(source,
            existing is null ? policy.EffectiveNativeByteLimit : IngestionAdmissionPolicy.MaximumNativeByteCount,
            cancellationToken);
        if (sourceRead.Rejection is { } rejection)
            return RevisionIngestionResult.RejectedBeforeReservation(rejection);
        var intent = createIntent(sourceRead.Digest!, sourceRead.Bytes!.LongLength);
        var reservation = await _metadata.ReserveAsync(actor, project, key, intent,
            profile, policy, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (reservation.Receipt is not { } receipt)
            return WithoutReceipt(reservation);
        RequireOwnKey(receipt, actor, project, intent.Kind, intent.Target, key);
        if (receipt.Facts.Intent != intent || receipt.Facts.Fingerprint !=
            CanonicalFingerprint.Create(actor.PrincipalId, project, intent) ||
            existing is not null && receipt.Facts != existing.Facts ||
            reservation.Outcome == IngestionStoreOutcome.Reserved &&
                (receipt.Facts.BackendProfile != profile || receipt.Facts.AdmissionPolicy != policy))
            throw InvalidMetadata();
        if (reservation.Outcome is not (IngestionStoreOutcome.Reserved or IngestionStoreOutcome.Existing))
            throw InvalidMetadata();
        if (IsTerminal(receipt.Phase))
            return FromReceipt(receipt);
        return await ClaimAndRunAsync(actor, receipt, sourceRead.Bytes, cancellationToken);
    }

    private async Task<RevisionIngestionResult> ClaimAndRunAsync(ActorContext actor,
        IngestionReceipt receipt, byte[]? sourceBytes, CancellationToken cancellationToken)
    {
        var claimResult = await _metadata.ClaimAsync(actor, receipt.Facts.Project,
            receipt.Facts.OperationId, _serverInstanceId, cancellationToken);
        if (claimResult.Receipt is not { } claimedReceipt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WithoutReceipt(claimResult);
        }
        RequireFrozenReceipt(claimedReceipt, receipt.Facts);
        if (claimResult.Outcome != IngestionStoreOutcome.Claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claimResult.Outcome is not (IngestionStoreOutcome.Existing or IngestionStoreOutcome.InProgress))
                throw InvalidMetadata();
            return FromReceipt(claimedReceipt, claimResult.Outcome == IngestionStoreOutcome.InProgress);
        }

        var claim = claimResult.Claim!;
        if (claim.ServerInstanceId != _serverInstanceId || claim.OriginalActor != actor.PrincipalId ||
            claim.Project != receipt.Facts.Project || claim.OperationId != receipt.Facts.OperationId ||
            claim.Generation != claimedReceipt.CurrentGeneration || claim.Generation <= receipt.CurrentGeneration)
            throw InvalidMetadata();
        try
        {
            return await RunClaimAsync(actor, claim, claimedReceipt, sourceBytes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryRecordCancellationAsync(actor, claim);
            throw;
        }
    }

    private async Task<RevisionIngestionResult> RunClaimAsync(ActorContext actor, IngestionClaim claim,
        IngestionReceipt claimedReceipt, byte[]? sourceBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = claimedReceipt.Facts;
        var reference = facts.NativeObject;
        if (reference.Id.Scope != facts.FutureRevision || reference.Id.Class != PrivateObjectClass.NativeArtifact ||
            reference.Digest != facts.Intent.NativeDigest || reference.ByteCount != facts.Intent.NativeByteCount ||
            reference.ByteCount > facts.AdmissionPolicy.EffectiveNativeByteLimit)
            return await RecordFailureAsync(actor, claim, facts, IngestionFailureCode.StorageIntegrity, cancellationToken);
        if (reference.ByteCount > _privateStore.NativeByteLimit)
            return await RecordFailureAsync(actor, claim, facts, IngestionFailureCode.StorageIo, cancellationToken);

        byte[] verifiedBytes;
        try
        {
            if (sourceBytes is not null)
            {
                using var ownedSource = new MemoryStream(sourceBytes, writable: false);
                var store = _privateStore.Store;
                var published = store is IPrivateObjectPublicationStore publicationStore
                    ? await publicationStore.PublishCreateOnlyAsync(PrivatePublicationOwner.ForIngestion(actor, claim),
                        facts.FutureRevision, reference, ownedSource, cancellationToken)
                    : await store.PublishCreateOnlyAsync(facts.FutureRevision, reference, ownedSource, cancellationToken);
                if (published is not (PrivateObjectPublishResult.Created or PrivateObjectPublishResult.AlreadyPresent))
                    throw new PrivateObjectStorageException(PrivateObjectStorageError.Integrity);
            }
            verifiedBytes = await _privateStore.Store.ReadVerifiedAsync(facts.FutureRevision,
                reference, cancellationToken);
        }
        catch (PrivateObjectStorageException error)
        {
            var failure = error.Error switch
            {
                PrivateObjectStorageError.Missing when sourceBytes is null => IngestionFailureCode.NeedsSource,
                PrivateObjectStorageError.ScopeMismatch or PrivateObjectStorageError.Conflict or
                    PrivateObjectStorageError.Integrity => IngestionFailureCode.StorageIntegrity,
                _ => IngestionFailureCode.StorageIo
            };
            return await RecordFailureAsync(actor, claim, facts, failure, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (verifiedBytes is null || verifiedBytes.LongLength != reference.ByteCount || verifiedBytes.Length == 0 ||
            verifiedBytes.LongLength > facts.AdmissionPolicy.EffectiveNativeByteLimit ||
            Sha256Digest.FromBytes(verifiedBytes) != reference.Digest)
            return await RecordFailureAsync(actor, claim, facts, IngestionFailureCode.StorageIntegrity, cancellationToken);
        var artifact = NativeArtifact.Admit(facts.FutureRevision, verifiedBytes,
            checked((int)facts.AdmissionPolicy.EffectiveNativeByteLimit));
        if (artifact.Scope != reference.Id.Scope || artifact.ByteCount != reference.ByteCount || artifact.Digest != reference.Digest)
            return await RecordFailureAsync(actor, claim, facts, IngestionFailureCode.StorageIntegrity, cancellationToken);

        var staged = await _metadata.MarkStagedAsync(actor, claim, reference, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (staged.Receipt is not { } stagedReceipt)
            return WithoutReceipt(staged);
        RequireCurrentReceipt(stagedReceipt, facts, claim);
        if (staged.Outcome != IngestionStoreOutcome.Applied || stagedReceipt.VerifiedStageGeneration != claim.Generation)
            throw InvalidMetadata();

        IngestionAttemptCompletion completion;
        if (stagedReceipt.EligibleAttemptGeneration is { } originalGeneration)
        {
            if (originalGeneration >= claim.Generation)
                throw InvalidMetadata();
            completion = IngestionAttemptCompletion.ReuseEligibleEvidence(originalGeneration);
        }
        else
        {
            var backend = _backends.GetReserved(facts.BackendProfile);
            if (backend is null)
                return await RecordFailureAsync(actor, claim, facts, IngestionFailureCode.ReservedProfileUnavailable, cancellationToken);
            if (backend.Profile != facts.BackendProfile)
                throw new InvalidOperationException("The reserved ingestion backend identity is inconsistent.");
            if (artifact.ByteCount > backend.NativeByteLimit)
                return await RecordFailureAsync(actor, claim, facts, IngestionFailureCode.StorageIo, cancellationToken);
            try
            {
                completion = await backend.Backend.ValidateAsync(artifact, cancellationToken);
                if (completion is null || completion.Kind == IngestionAttemptCompletionKind.ReusedEvidence)
                    completion = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.ProtocolFailure);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                completion = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.BackendFailure);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var completed = await _metadata.CompleteAttemptAsync(actor, claim, completion, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed.Receipt is not { } completedReceipt)
            return WithoutReceipt(completed);
        RequireCurrentReceipt(completedReceipt, facts, claim);
        if (completed.Outcome != IngestionStoreOutcome.Applied)
            throw InvalidMetadata();
        // The store can honestly replace an oversized JSONB observation with EvidenceLimit.
        // Its durable receipt, rather than the proposed completion, decides publication.
        if (completedReceipt.Phase != IngestionPhase.Ready)
            return FromReceipt(completedReceipt);

        var finalized = await _metadata.FinalizeAsync(actor, claim, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (finalized.Receipt is not { } finalReceipt)
            return WithoutReceipt(finalized);
        RequireCurrentReceipt(finalReceipt, facts, claim);
        if (finalized.Outcome != IngestionStoreOutcome.Applied || !IsTerminal(finalReceipt.Phase))
            throw InvalidMetadata();
        return FromReceipt(finalReceipt);
    }

    private async Task<RevisionIngestionResult> RecordFailureAsync(ActorContext actor, IngestionClaim claim,
        ReservedIngestionFacts facts, IngestionFailureCode failure, CancellationToken cancellationToken)
    {
        var result = await _metadata.CompleteAttemptAsync(actor, claim,
            IngestionAttemptCompletion.FromOperationalFailure(failure), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Receipt is not { } receipt)
            return WithoutReceipt(result);
        RequireCurrentReceipt(receipt, facts, claim);
        if (result.Outcome != IngestionStoreOutcome.Applied)
            throw InvalidMetadata();
        return FromReceipt(receipt);
    }

    private async Task TryRecordCancellationAsync(ActorContext actor, IngestionClaim claim)
    {
        using var cleanup = new CancellationTokenSource(CancellationBookkeepingLimit);
        try
        {
            await _metadata.CompleteAttemptAsync(actor, claim,
                IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.Canceled), cleanup.Token)
                .WaitAsync(cleanup.Token);
        }
        catch (Exception)
        {
            // A lost fence, revoked actor, or unavailable metadata leaves honest state for lease recovery.
        }
    }

    private async Task<bool> HasUploadAccessAsync(ActorContext actor, ProjectScope project,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var access = await _projectReader.GetAccessAsync(actor, project, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return access is not null && access.Scope == project && access.PrincipalId == actor.PrincipalId &&
            access.Can(ProjectAction.UploadRevision);
    }

    private static RevisionIngestionResult FromReceipt(IngestionReceipt receipt, bool inProgress = false)
    {
        var outcome = receipt.Phase switch
        {
            IngestionPhase.Committed when receipt.CommittedRevision!.InitialAdmission ==
                IngestionAdmissionClassification.EligibleValid => RevisionIngestionOutcome.CommittedValid,
            IngestionPhase.Committed => RevisionIngestionOutcome.CommittedDecodedInvalid,
            IngestionPhase.Rejected => RevisionIngestionOutcome.Rejected,
            IngestionPhase.HeadConflict => RevisionIngestionOutcome.HeadConflict,
            IngestionPhase.AttemptExhausted => RevisionIngestionOutcome.AttemptExhausted,
            _ when !inProgress && receipt.Attempts.LastOrDefault()?.Completion?.Failure ==
                IngestionFailureCode.NeedsSource => RevisionIngestionOutcome.NeedsSource,
            _ when !inProgress && receipt.Attempts.LastOrDefault()?.Classification ==
                IngestionAdmissionClassification.RetryableFailure => RevisionIngestionOutcome.RetryableFailure,
            _ => RevisionIngestionOutcome.InProgress
        };
        return RevisionIngestionResult.ForReceipt(outcome, RevisionIngestionReceipt.FromMetadata(receipt));
    }

    private static RevisionIngestionResult WithoutReceipt(IngestionStoreResult result) => result.Outcome switch
    {
        IngestionStoreOutcome.ClaimLost => RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost),
        IngestionStoreOutcome.IdempotencyConflict => RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.IdempotencyConflict),
        IngestionStoreOutcome.NotAuthorizedOrNotFound => Hidden(),
        _ => throw InvalidMetadata()
    };

    private static RevisionIngestionResult Hidden() =>
        RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound);

    private static bool IsTerminal(IngestionPhase phase) => phase is IngestionPhase.Committed or
        IngestionPhase.Rejected or IngestionPhase.HeadConflict or IngestionPhase.AttemptExhausted;

    private static void RequireSubmission(ActorContext actor, ProjectScope project, Guid key,
        object request, Stream source)
    {
        RequireIdentity(actor, project, key, nameof(key));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The native source must be readable.", nameof(source));
    }

    private static void RequireIdentity(ActorContext actor, ProjectScope project, Guid id, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        if (id == Guid.Empty)
            throw new ArgumentException("An ingestion identifier cannot be empty.", parameterName);
    }

    private static void RequireOwnKey(IngestionReceipt? receipt, ActorContext actor, ProjectScope project,
        IngestionOperationKind kind, MappingScope? target, Guid key)
    {
        if (receipt is null)
            return;
        RequireOwnReceipt(receipt, actor, project, receipt.Facts.OperationId);
        if (receipt.Facts.Intent.Kind != kind || receipt.Facts.Intent.Target != target || receipt.Facts.IdempotencyKey != key)
            throw InvalidMetadata();
    }

    private static void RequireOwnReceipt(IngestionReceipt receipt, ActorContext actor, ProjectScope project, Guid operationId)
    {
        if (receipt.Facts.Project != project || receipt.Facts.OriginalActor != actor.PrincipalId ||
            receipt.Facts.OperationId != operationId)
            throw InvalidMetadata();
    }

    private static void RequireFrozenReceipt(IngestionReceipt receipt, ReservedIngestionFacts facts)
    {
        if (receipt.Facts != facts)
            throw InvalidMetadata();
    }

    private static void RequireCurrentReceipt(IngestionReceipt receipt, ReservedIngestionFacts facts, IngestionClaim claim)
    {
        RequireFrozenReceipt(receipt, facts);
        if (receipt.CurrentGeneration != claim.Generation)
            throw InvalidMetadata();
    }

    private static InvalidOperationException InvalidMetadata() =>
        new("The ingestion metadata response is inconsistent with the requested operation.");
}
