using Weft.Domain;

namespace Weft.Application;

public static class IngestionWorkflowLimits
{
    public const int MaximumClaims = 8;
    public const int ClaimLeaseSeconds = 120;
}

public enum IngestionFailureCode
{
    NeedsSource = 1,
    StorageIntegrity = 2,
    StorageIo = 3,
    BackendUnavailable = 4,
    BackendFailure = 5,
    TimedOut = 6,
    Canceled = 7,
    IsolationFailure = 8,
    ProtocolFailure = 9,
    ReservedProfileUnavailable = 10,
    EvidenceLimit = 11
}

public enum IngestionAttemptCompletionKind { Validation = 1, OperationalFailure = 2, ReusedEvidence = 3 }

/// <summary>Trusted completion facts. Eligible classification is computed against the immutable reservation.</summary>
public sealed record IngestionAttemptCompletion
{
    private IngestionAttemptCompletion(IngestionAttemptCompletionKind kind, IngestionEvidenceDocument? evidence,
        IngestionFailureCode? failure, long? reusedAttemptGeneration, IngestionProvenance? observedProvenance)
    {
        Kind = kind;
        Evidence = evidence;
        Failure = failure;
        ReusedAttemptGeneration = reusedAttemptGeneration;
        ObservedProvenance = observedProvenance;
    }

    public IngestionAttemptCompletionKind Kind { get; }
    public IngestionEvidenceDocument? Evidence { get; }
    public IngestionFailureCode? Failure { get; }
    public long? ReusedAttemptGeneration { get; }
    public IngestionProvenance? ObservedProvenance { get; }

    public static IngestionAttemptCompletion FromValidation(IngestionEvidenceDocument evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new(IngestionAttemptCompletionKind.Validation, evidence, null, null, null);
    }

    public static IngestionAttemptCompletion FromOperationalFailure(IngestionFailureCode failure,
        IngestionProvenance? observedProvenance = null)
    {
        if (!Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(failure), "The ingestion failure must be known.");
        if (observedProvenance is not null && failure != IngestionFailureCode.EvidenceLimit)
            throw new ArgumentException("Only an evidence policy limit can retain actual observed provenance.", nameof(observedProvenance));
        return new(IngestionAttemptCompletionKind.OperationalFailure, null, failure, null, observedProvenance);
    }

    public static IngestionAttemptCompletion ReuseEligibleEvidence(long originalAttemptGeneration)
    {
        RequireGeneration(originalAttemptGeneration, nameof(originalAttemptGeneration));
        return new(IngestionAttemptCompletionKind.ReusedEvidence, null, null, originalAttemptGeneration, null);
    }

    public IngestionAdmissionClassification Classify(ReservedIngestionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (Kind == IngestionAttemptCompletionKind.OperationalFailure)
            return Failure == IngestionFailureCode.EvidenceLimit
                ? IngestionAdmissionClassification.RejectedResourceLimit
                : IngestionAdmissionClassification.RetryableFailure;
        if (Kind == IngestionAttemptCompletionKind.ReusedEvidence)
            return IngestionAdmissionClassification.ReusedEvidence;

        var evidence = Evidence!;
        var result = evidence.Facts;
        if (!Matches(result.Provenance, facts) || evidence.HasDiagnosticCode("ferrule.digestMismatch"))
            return IngestionAdmissionClassification.RetryableFailure;
        return (result.Status, result.IsValid, result.Provenance.Confirmed) switch
        {
            (IngestionBackendStatus.Succeeded, true, true) => IngestionAdmissionClassification.EligibleValid,
            (IngestionBackendStatus.InvalidMapping, false, true) => IngestionAdmissionClassification.EligibleDecodedInvalid,
            (IngestionBackendStatus.InvalidMapping, null, true) when evidence.HasDiagnosticCode("ferrule.nativeDecodeFailed") =>
                IngestionAdmissionClassification.RejectedNativeDecode,
            (IngestionBackendStatus.UnsupportedArtifact, null, _) => IngestionAdmissionClassification.RejectedUnsupported,
            (IngestionBackendStatus.ResourceLimit, null, _) => IngestionAdmissionClassification.RejectedResourceLimit,
            _ => IngestionAdmissionClassification.RetryableFailure
        };
    }

    internal static bool Matches(IngestionProvenance provenance, ReservedIngestionFacts facts) =>
        provenance.Scope == facts.FutureRevision && provenance.NativeDigest == facts.NativeObject.Digest &&
        provenance.BackendProfile == facts.BackendProfile;

    internal static void RequireGeneration(long generation, string parameterName)
    {
        if (generation is < 1 or > IngestionWorkflowLimits.MaximumClaims)
            throw new ArgumentOutOfRangeException(parameterName, "Claim generation must fit the explicit claim budget.");
    }
}

/// <summary>Fenced lease issued by the trusted store using database time; never a public request or role grant.</summary>
public sealed record IngestionClaim
{
    public IngestionClaim(ProjectScope project, Guid operationId, PrincipalId originalActor,
        Guid serverInstanceId, Guid leaseToken, long generation, DateTimeOffset leaseExpiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(originalActor);
        RequireId(operationId, nameof(operationId));
        RequireId(serverInstanceId, nameof(serverInstanceId));
        RequireId(leaseToken, nameof(leaseToken));
        IngestionAttemptCompletion.RequireGeneration(generation, nameof(generation));
        Project = project;
        OperationId = operationId;
        OriginalActor = originalActor;
        ServerInstanceId = serverInstanceId;
        LeaseToken = leaseToken;
        Generation = generation;
        LeaseExpiresAtUtc = leaseExpiresAtUtc.ToUniversalTime();
    }

    public ProjectScope Project { get; }
    public Guid OperationId { get; }
    public PrincipalId OriginalActor { get; }
    public Guid ServerInstanceId { get; }
    public Guid LeaseToken { get; }
    public long Generation { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }

    private static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A claim identifier cannot be empty.", parameterName);
    }
}

public sealed record IngestionAttemptReceipt
{
    public IngestionAttemptReceipt(long generation, DateTimeOffset startedAtUtc, DateTimeOffset? finishedAtUtc,
        IngestionAttemptCompletion? completion, IngestionAdmissionClassification? classification)
    {
        IngestionAttemptCompletion.RequireGeneration(generation, nameof(generation));
        if ((completion is null) != (finishedAtUtc is null) || (completion is null) != (classification is null))
            throw new ArgumentException("Attempt completion, classification, and finish time must be recorded together.");
        if (finishedAtUtc < startedAtUtc)
            throw new ArgumentException("An attempt cannot finish before it starts.", nameof(finishedAtUtc));
        if (classification is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(classification), "The attempt classification must be known.");
        if (completion is { Kind: IngestionAttemptCompletionKind.ReusedEvidence } &&
            (classification != IngestionAdmissionClassification.ReusedEvidence || completion.ReusedAttemptGeneration >= generation))
            throw new ArgumentException("A recovery attempt must reference an earlier eligible generation.", nameof(completion));
        if (completion is not null && completion.Kind != IngestionAttemptCompletionKind.ReusedEvidence &&
            classification == IngestionAdmissionClassification.ReusedEvidence)
            throw new ArgumentException("Only a recovery attempt can be classified as reused evidence.", nameof(classification));

        Generation = generation;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        FinishedAtUtc = finishedAtUtc?.ToUniversalTime();
        Completion = completion;
        Classification = classification;
    }

    public long Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public IngestionAttemptCompletion? Completion { get; }
    public IngestionAdmissionClassification? Classification { get; }
}

/// <summary>Owned receipt snapshot; active lease tokens and claimant identities are never exposed here.</summary>
public sealed record IngestionReceipt
{
    public IngestionReceipt(ReservedIngestionFacts facts, IngestionPhase phase, long currentGeneration, int claimCount,
        long? verifiedStageGeneration, DateTimeOffset? verifiedStageAtUtc, long? eligibleAttemptGeneration,
        IEnumerable<IngestionAttemptReceipt> attempts, CommittedRevisionFacts? committedRevision = null,
        DateTimeOffset? completedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(attempts);
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), "The ingestion phase must be known.");
        if (claimCount is < 0 or > IngestionWorkflowLimits.MaximumClaims || currentGeneration != claimCount)
            throw new ArgumentOutOfRangeException(nameof(claimCount), "Claim count and generation must agree within the budget.");
        if ((verifiedStageGeneration is null) != (verifiedStageAtUtc is null) ||
            verifiedStageGeneration is { } staged && (staged < 1 || staged > currentGeneration))
            throw new ArgumentException("Verified stage must record its valid generation and timestamp together.");
        var snapshot = attempts.Take(IngestionWorkflowLimits.MaximumClaims + 1).ToArray();
        if (snapshot.Any(attempt => attempt is null) || snapshot.Length != claimCount)
            throw new ArgumentException("Receipt attempts must be an owned complete claim history.", nameof(attempts));
        snapshot = snapshot.OrderBy(attempt => attempt.Generation).ToArray();
        for (var index = 0; index < snapshot.Length; index++)
        {
            var attempt = snapshot[index];
            if (attempt.Generation != index + 1 || attempt.StartedAtUtc < facts.ReservedAtUtc ||
                attempt.Completion is { } completion && attempt.Classification != completion.Classify(facts))
                throw new ArgumentException("Receipt attempts must agree with reserved facts and consecutive claim generations.", nameof(attempts));
        }

        IngestionAttemptReceipt? eligible = null;
        if (eligibleAttemptGeneration is { } eligibleGeneration)
        {
            eligible = snapshot.SingleOrDefault(attempt => attempt.Generation == eligibleGeneration);
            if (eligible is null || eligible.Completion?.Kind != IngestionAttemptCompletionKind.Validation ||
                eligible.Classification is not (IngestionAdmissionClassification.EligibleValid or
                    IngestionAdmissionClassification.EligibleDecodedInvalid))
                throw new ArgumentException("The original eligible pointer must name completed matching validation evidence.");
        }
        var originalEligible = snapshot.FirstOrDefault(attempt => attempt.Classification is
            IngestionAdmissionClassification.EligibleValid or IngestionAdmissionClassification.EligibleDecodedInvalid);
        if (originalEligible?.Generation != eligibleAttemptGeneration)
            throw new ArgumentException("The original eligible attempt must remain the first completed eligible observation.");
        foreach (var attempt in snapshot.Where(attempt => attempt.Completion?.Kind == IngestionAttemptCompletionKind.ReusedEvidence))
        {
            if (eligible is null || attempt.Completion!.ReusedAttemptGeneration != eligible.Generation)
                throw new ArgumentException("Recovery evidence must reference the original eligible attempt.", nameof(attempts));
        }

        var terminal = phase is IngestionPhase.Committed or IngestionPhase.Rejected or IngestionPhase.HeadConflict or
            IngestionPhase.AttemptExhausted;
        if (terminal != (completedAtUtc is not null) || (phase == IngestionPhase.Committed) != (committedRevision is not null))
            throw new ArgumentException("Terminal receipt fields must agree with its phase.");
        if (phase is IngestionPhase.Ready or IngestionPhase.Committed or IngestionPhase.HeadConflict && eligible is null)
            throw new ArgumentException("Ready or published receipts require original eligible evidence.");
        if ((phase is IngestionPhase.Staged or IngestionPhase.Ready) && verifiedStageGeneration is null)
            throw new ArgumentException("A staged or ready receipt requires verified stage facts.");
        if (phase == IngestionPhase.AttemptExhausted && claimCount != IngestionWorkflowLimits.MaximumClaims)
            throw new ArgumentException("Only the full explicit claim budget can exhaust an operation.");
        if (phase is IngestionPhase.Committed or IngestionPhase.HeadConflict)
        {
            var current = snapshot.LastOrDefault();
            if (verifiedStageGeneration != currentGeneration || current?.Completion is null ||
                current.Classification is not (IngestionAdmissionClassification.EligibleValid or
                    IngestionAdmissionClassification.EligibleDecodedInvalid or IngestionAdmissionClassification.ReusedEvidence))
                throw new ArgumentException("Publication requires verified and completed current fenced work.");
        }
        if (phase == IngestionPhase.Rejected && snapshot.LastOrDefault()?.Classification is not
            (IngestionAdmissionClassification.RejectedNativeDecode or IngestionAdmissionClassification.RejectedUnsupported or
                IngestionAdmissionClassification.RejectedResourceLimit))
            throw new ArgumentException("A rejected receipt requires an explicit policy rejection.");
        if (committedRevision is { } revision &&
            (revision.Scope != facts.FutureRevision || revision.NativeObject != facts.NativeObject ||
                revision.BackendProfile != facts.BackendProfile || revision.Creator != facts.OriginalActor ||
                revision.OperationId != facts.OperationId || revision.InitialAdmissionAttemptGeneration != eligible?.Generation ||
                revision.InitialAdmission != eligible?.Classification))
            throw new ArgumentException("The committed revision must preserve all reserved and initial admission facts.");

        Facts = facts;
        Phase = phase;
        CurrentGeneration = currentGeneration;
        ClaimCount = claimCount;
        VerifiedStageGeneration = verifiedStageGeneration;
        VerifiedStageAtUtc = verifiedStageAtUtc?.ToUniversalTime();
        EligibleAttemptGeneration = eligibleAttemptGeneration;
        Attempts = Array.AsReadOnly(snapshot);
        CommittedRevision = committedRevision;
        CompletedAtUtc = completedAtUtc?.ToUniversalTime();
    }

    public ReservedIngestionFacts Facts { get; }
    public IngestionPhase Phase { get; }
    public long CurrentGeneration { get; }
    public int ClaimCount { get; }
    public long? VerifiedStageGeneration { get; }
    public DateTimeOffset? VerifiedStageAtUtc { get; }
    public long? EligibleAttemptGeneration { get; }
    public IReadOnlyList<IngestionAttemptReceipt> Attempts { get; }
    public CommittedRevisionFacts? CommittedRevision { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
}

public enum IngestionStoreOutcome
{
    Reserved = 1,
    Existing = 2,
    Claimed = 3,
    Applied = 4,
    InProgress = 5,
    ClaimLost = 6,
    IdempotencyConflict = 7,
    NotAuthorizedOrNotFound = 8
}

public sealed record IngestionStoreResult
{
    private IngestionStoreResult(IngestionStoreOutcome outcome, IngestionReceipt? receipt, IngestionClaim? claim)
    {
        Outcome = outcome;
        Receipt = receipt;
        Claim = claim;
    }

    public IngestionStoreOutcome Outcome { get; }
    public IngestionReceipt? Receipt { get; }
    public IngestionClaim? Claim { get; }

    public static IngestionStoreResult ForReceipt(IngestionStoreOutcome outcome, IngestionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (outcome is not (IngestionStoreOutcome.Reserved or IngestionStoreOutcome.Existing or
            IngestionStoreOutcome.Applied or IngestionStoreOutcome.InProgress))
            throw new ArgumentOutOfRangeException(nameof(outcome), "This result requires a different receipt shape.");
        return new(outcome, receipt, null);
    }

    public static IngestionStoreResult ForClaim(IngestionReceipt receipt, IngestionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Project != receipt.Facts.Project || claim.OperationId != receipt.Facts.OperationId ||
            claim.OriginalActor != receipt.Facts.OriginalActor || claim.Generation != receipt.CurrentGeneration ||
            receipt.Phase is IngestionPhase.Committed or IngestionPhase.Rejected or IngestionPhase.HeadConflict or
                IngestionPhase.AttemptExhausted)
            throw new ArgumentException("A claim result must agree with the live receipt identity.", nameof(claim));
        return new(IngestionStoreOutcome.Claimed, receipt, claim);
    }

    public static IngestionStoreResult WithoutReceipt(IngestionStoreOutcome outcome)
    {
        if (outcome is not (IngestionStoreOutcome.ClaimLost or IngestionStoreOutcome.IdempotencyConflict or
            IngestionStoreOutcome.NotAuthorizedOrNotFound))
            throw new ArgumentOutOfRangeException(nameof(outcome), "This result requires a receipt.");
        return new(outcome, null, null);
    }
}

/// <summary>
/// Trusted metadata workflow only. Every call freshly checks active principal,
/// organization admission and exact-project Publisher/Administrator membership;
/// existing operations additionally require their original actor. No call holds
/// a transaction open across object storage or backend I/O. Database failures
/// remain exceptions, and cancellation uses OperationCanceledException.
/// </summary>
public interface IIngestionMetadataStore
{
    /// <summary>Reserves or compares complete exact intent; matching retries retain original future IDs and profile.</summary>
    Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey,
        IngestionIntent intent, IngestionBackendProfile proposedProfile, IngestionAdmissionPolicy proposedPolicy,
        CancellationToken cancellationToken = default);

    /// <summary>Claims expired/released work using 120 seconds of database time and the eight-claim budget.</summary>
    Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project, Guid operationId,
        Guid serverInstanceId, CancellationToken cancellationToken = default);

    /// <summary>Records a trusted exact-object verification in this fenced generation without invoking storage.</summary>
    Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim,
        PrivateObjectReference verifiedNativeObject, CancellationToken cancellationToken = default);

    /// <summary>Completes one current attempt with immutable classified facts; never accepts a caller-selected phase.</summary>
    Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim,
        IngestionAttemptCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Atomically publishes eligible current work or records EqualHead conflict under fresh authority.</summary>
    Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim,
        CancellationToken cancellationToken = default);

    /// <summary>Returns null for absence, lost authority, or another original actor, without disclosing a receipt.</summary>
    Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId,
        CancellationToken cancellationToken = default);
}
